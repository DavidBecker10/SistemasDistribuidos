using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;

var builder = WebApplication.CreateBuilder(args);

var factory = new ConnectionFactory { HostName = "localhost" };
using var connection = await factory.CreateConnectionAsync();
using var channel = await connection.CreateChannelAsync();

var externalPaymentUrl = "http://localhost:5002/api/pagamentos"; // URL do sistema externo de pagamentos

var app = builder.Build();

var options = new JsonSerializerOptions
{
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    WriteIndented = true // Opcional, para saída formatada
};

// Dicionário para armazenar as informações de pagamentos com status
var paymentStatusCache = new ConcurrentDictionary<string, string>();

await channel.ExchangeDeclareAsync(exchange: "direct_pagamento", type: "direct");

// exchange reserva-criada
await channel.ExchangeDeclareAsync(exchange: "reserva-criada", type: ExchangeType.Fanout);

var privateKeyDir = "keys/private";
var publicKeyDir = "keys/public";

Directory.CreateDirectory(privateKeyDir);
Directory.CreateDirectory(publicKeyDir);

var privateKeyPath = Path.Combine(privateKeyDir, "private.key");
var publicKeyPath = Path.Combine(publicKeyDir, "public.key");

RSA rsa;

// Verificar se as chaves já existem
if (File.Exists(privateKeyPath) && File.Exists(publicKeyPath))
{
    Console.WriteLine("Chaves existentes encontradas. Carregando...");
    var privateKey = File.ReadAllBytes(privateKeyPath);
    rsa = RSA.Create();
    rsa.ImportRSAPrivateKey(privateKey, out _);
}
else
{
    Console.WriteLine("Chaves não encontradas. Gerando novas...");
    rsa = RSA.Create(2048);

    var privateKey = rsa.ExportRSAPrivateKey();
    var publicKey = rsa.ExportRSAPublicKey();
    File.WriteAllBytes(privateKeyPath, privateKey);
    File.WriteAllBytes(publicKeyPath, publicKey);

    Console.WriteLine($"Chaves geradas e salvas em:\n - {privateKeyPath}\n - {publicKeyPath}");
}

// Consumidor para a fila reserva-criada
var consumer = new AsyncEventingBasicConsumer(channel);
consumer.ReceivedAsync += async (model, ea) =>
{
    try
    {
        byte[] body = ea.Body.ToArray();
        var message = Encoding.UTF8.GetString(body);
        Console.WriteLine($" [x] Received: {message}");

        var httpClient = new HttpClient();

        var content = new StringContent(message, Encoding.UTF8, "application/json");

        Console.WriteLine($" [x] Sending payment data to external system: {externalPaymentUrl}");
        var response = await httpClient.PostAsync(externalPaymentUrl, content);

        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($" [!] Error: Unable to process payment. Status code: {response.StatusCode}");
            return;
        }

        var responseBody = await response.Content.ReadAsStringAsync();
        Console.WriteLine($" [x] Response from external system: {responseBody}");

        // Parse da resposta
        var jsonDocument = JsonDocument.Parse(responseBody);
        var rootElement = jsonDocument.RootElement;

        // Acessa o campo "status" no JSON
        string status = rootElement.GetProperty("status").GetString() ?? "Indefinido";
        string transactionId = rootElement.GetProperty("transactionId").GetString() ?? "Desconhecido";

        // Atualizar o cache com o status do pagamento
        paymentStatusCache[transactionId] = status;

        // Publicar na fila apropriada
        string routingKey = status == "Aprovado" ? "pagamento.aprovado" : "pagamento.recusado";

        // publica mensagem assinada na exchange
        var messageResponseBody = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(responseBody, options));
        await channel.BasicPublishAsync(exchange: "direct_pagamento", routingKey: routingKey, body: messageResponseBody);

        Console.WriteLine($" [x] Published to '{routingKey}': {JsonSerializer.Serialize(responseBody, options)}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($" [!] Error: {ex.Message}");
    }
};

// consumo da fila "reserva-criada"
await channel.QueueDeclareAsync(queue: "reserva-criada-pagamento", durable: true, exclusive: false, autoDelete: false);
await channel.QueueBindAsync(queue: "reserva-criada-pagamento", exchange: "reserva-criada", routingKey: string.Empty);
await channel.BasicConsumeAsync(queue: "reserva-criada-pagamento", autoAck: true, consumer: consumer);

app.MapGet("/api/pagamento/status/{transactionId}", async (HttpContext context, string transactionId) =>
{
    if (paymentStatusCache.TryGetValue(transactionId, out var paymentInfo))
    {
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(paymentInfo);
    }
    else
    {
        context.Response.StatusCode = 404;
        await context.Response.WriteAsync($"Pagamento com ID {transactionId} não encontrado.");
    }
});

Console.WriteLine(" [*] Waiting for messages in 'reserva-criada'.");
Console.WriteLine(" Press [enter] to exit.");
Console.ReadLine();
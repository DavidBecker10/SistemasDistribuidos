using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

var factory = new ConnectionFactory { HostName = "localhost" };
using var connection = await factory.CreateConnectionAsync();
using var channel = await connection.CreateChannelAsync();

await channel.ExchangeDeclareAsync(exchange: "direct_pagamento", type: "direct");
// fila para bilhete
var queueName = "bilhete-pagamento-aprovado";
await channel.QueueDeclareAsync(queue: queueName, durable: true, exclusive: false, autoDelete: false);

// assicia a fila e a exchange com a routing key "pagamento.aprovado"
await channel.QueueBindAsync(queue: queueName, exchange: "direct_pagamento", routingKey: "pagamento.aprovado");

await channel.QueueDeclareAsync(queue: "bilhete-gerado", durable: true, exclusive: false, autoDelete: false);

// chave publica do microsservico de Pagamento
var publicKeyPath = "keys/public/public.key";

if (!File.Exists(publicKeyPath))
{
    Console.WriteLine($"Chave pública não encontrada no caminho: {publicKeyPath}");
    Environment.Exit(1);
}
var publicKeyBytes = File.ReadAllBytes(publicKeyPath);
using var rsa = RSA.Create();
rsa.ImportRSAPublicKey(publicKeyBytes, out _);

// Metodo para verificar a assinatura
bool VerifySignature(string message, string signature)
{
    try
    {
        var messageBytes = Encoding.UTF8.GetBytes(message);
        var signatureBytes = Convert.FromBase64String(signature);

        return rsa.VerifyData(messageBytes, signatureBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }
    catch
    {
        return false;
    }
}

// Consumidor para a fila
var consumer = new AsyncEventingBasicConsumer(channel);
consumer.ReceivedAsync += async (model, ea) =>
{
    try
    {
        byte[] body = ea.Body.ToArray();
        var rawMessage = Encoding.UTF8.GetString(body);

        var rawJson = JsonSerializer.Deserialize<string>(rawMessage);

        var paymentInfo = JsonSerializer.Deserialize<JsonElement>(rawJson);

        Console.WriteLine($"[Bilhete] Mensagem válida recebida: {paymentInfo}");

        // Publicar mensagem na fila "bilhete-gerado"
        var responseMessage = new { OriginalMessage = paymentInfo, bilhete = "gerado" };
        var responseBody = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(responseMessage));
        await channel.BasicPublishAsync(exchange: string.Empty, routingKey: "bilhete-gerado", body: responseBody);

        Console.WriteLine($"[Bilhete] Published to 'bilhete-gerado': {JsonSerializer.Serialize(responseMessage)}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Bilhete] Error: {ex.Message}");
    }

    await Task.CompletedTask;
};

// consumo da fila "bilhete-pagamento-aprovado"
await channel.BasicConsumeAsync(queue: queueName, autoAck: true, consumer: consumer);

Console.WriteLine(" [*] Waiting for messages with routing key 'pagamento.aprovado'.");
Console.WriteLine(" Press [enter] to exit.");
Console.ReadLine();

// Classe para representar a estrutura da mensagem assinada
public class SignedMessage
{
    public string? Message { get; set; }
    public string? Signature { get; set; }
}
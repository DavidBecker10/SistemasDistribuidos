using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO;
using System.Text.Encodings.Web;

var factory = new ConnectionFactory { HostName = "localhost" };
using var connection = await factory.CreateConnectionAsync();
using var channel = await connection.CreateChannelAsync();

var options = new JsonSerializerOptions
{
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    WriteIndented = true // Opcional, para saída formatada
};

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

        await Task.Delay(5000);

        var random = new Random();
        bool pagamentoAprovado = random.Next(0, 2) == 0;

        // routingKey baseada no status do pagamento
        string routingKey = pagamentoAprovado ? "pagamento.aprovado" : "pagamento.recusado";

        var responseMessage = new { OriginalMessage = message, Status = pagamentoAprovado ? "Aprovado" : "Recusado" };
        var messageBody = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(responseMessage, options));

        // assinar a mensagem
        var signature = rsa.SignData(messageBody, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var signedMessage = new
        {
            Message = JsonSerializer.Serialize(responseMessage, options),
            Signature = Convert.ToBase64String(signature)
        };

        // publica mensagem assinada na exchange
        var responseBody = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(signedMessage, options));
        await channel.BasicPublishAsync(exchange: "direct_pagamento", routingKey: routingKey, body: responseBody);

        Console.WriteLine($" [x] Published to '{routingKey}': {JsonSerializer.Serialize(signedMessage, options)}");
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

Console.WriteLine(" [*] Waiting for messages in 'reserva-criada'.");
Console.WriteLine(" Press [enter] to exit.");
Console.ReadLine();
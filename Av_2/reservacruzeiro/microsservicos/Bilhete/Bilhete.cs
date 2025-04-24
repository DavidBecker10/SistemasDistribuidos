using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// Configurar conexão RabbitMQ
var factory = new ConnectionFactory { HostName = "localhost" };
using var connection = await factory.CreateConnectionAsync();
using var channel = await connection.CreateChannelAsync();

// Declaração da exchange
await channel.ExchangeDeclareAsync(exchange: "direct_pagamento", type: "direct");

// Declaração de fila específica para bilhete
var queueName = "bilhete-pagamento-aprovado";
await channel.QueueDeclareAsync(queue: queueName, durable: true, exclusive: false, autoDelete: false);

// Vincular a fila à exchange com a routing key "pagamento.aprovado"
await channel.QueueBindAsync(queue: queueName, exchange: "direct_pagamento", routingKey: "pagamento.aprovado");

// Caminho para a chave pública do microsserviço de Pagamento
var publicKeyPath = "keys/public/public.key";

// Carregar a chave pública
if (!File.Exists(publicKeyPath))
{
    Console.WriteLine($"Chave pública não encontrada no caminho: {publicKeyPath}");
    Environment.Exit(1);
}
var publicKeyBytes = File.ReadAllBytes(publicKeyPath);
using var rsa = RSA.Create();
rsa.ImportRSAPublicKey(publicKeyBytes, out _);

// Método para verificar a assinatura
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

        // Deserializar a mensagem recebida
        var signedMessage = JsonSerializer.Deserialize<SignedMessage>(rawMessage);

        if (signedMessage != null && VerifySignature(signedMessage.Message, signedMessage.Signature))
        {
            Console.WriteLine($"[Bilhete] Mensagem válida recebida: {signedMessage.Message}");

            // Publicar mensagem na fila "bilhete-gerado"
            var responseMessage = new { OriginalMessage = signedMessage.Message, bilhete = "gerado" };
            var responseBody = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(responseMessage));
            await channel.BasicPublishAsync(exchange: string.Empty, routingKey: "bilhete-gerado", body: responseBody);

            Console.WriteLine($"[Bilhete] Published to 'bilhete-gerado': {JsonSerializer.Serialize(responseMessage)}");
        }
        else
        {
            Console.WriteLine("[Bilhete] Assinatura inválida. Mensagem descartada.");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Bilhete] Error: {ex.Message}");
    }

    await Task.CompletedTask;
};

// Iniciar consumo
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
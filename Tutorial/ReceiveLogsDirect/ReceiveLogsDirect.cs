using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: {0} [info] [warning] [error]", Environment.GetCommandLineArgs()[0]);
    Console.WriteLine(" Press [enter] to exit.");
    Environment.ExitCode = 1;
    return;
}

var factory = new ConnectionFactory { HostName = "localhost" };
using var connection = await factory.CreateConnectionAsync();
using var channel = await connection.CreateChannelAsync();

// Declaração da exchange "direct_logs"
await channel.ExchangeDeclareAsync(exchange: "direct_logs", type: ExchangeType.Direct);

// Declaração de fila dinâmica
var queueDeclareResult = await channel.QueueDeclareAsync();
string queueName = queueDeclareResult.QueueName;

// Vincular a fila às chaves de roteamento fornecidas nos argumentos
foreach (string severity in args)
{
    await channel.QueueBindAsync(queue: queueName, exchange: "direct_logs", routingKey: severity);
}

Console.WriteLine(" [*] Waiting for messages.");

// Carregar a chave pública para verificar mensagens
var publicKeyPath = "keys/public/public.key";
if (!File.Exists(publicKeyPath))
{
    Console.WriteLine("Chave pública não encontrada. Certifique-se de que o arquivo 'public.key' está disponível em 'keys/public/'.");
    Environment.ExitCode = 1;
    return;
}

var publicKey = File.ReadAllBytes(publicKeyPath);
using var rsa = RSA.Create();
rsa.ImportRSAPublicKey(publicKey, out _);

// Configurar o consumidor
var consumer = new AsyncEventingBasicConsumer(channel);
consumer.ReceivedAsync += (model, ea) =>
{
    try
    {
        // Ler a mensagem recebida
        var body = ea.Body.ToArray();
        var signedMessage = JsonSerializer.Deserialize<SignedMessage>(Encoding.UTF8.GetString(body));

        if (signedMessage == null || string.IsNullOrWhiteSpace(signedMessage.Message) || string.IsNullOrWhiteSpace(signedMessage.Signature))
        {
            Console.WriteLine("[!] Mensagem inválida ou assinatura ausente.");
            return Task.CompletedTask;
        }

        // Verificar a assinatura da mensagem
        var isValid = rsa.VerifyData(
            Encoding.UTF8.GetBytes(signedMessage.Message),
            Convert.FromBase64String(signedMessage.Signature),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1
        );

        if (isValid)
        {
            Console.WriteLine($" [x] Mensagem válida recebida: '{ea.RoutingKey}' : '{signedMessage.Message}'");
        }
        else
        {
            Console.WriteLine("[!] Assinatura inválida. Mensagem rejeitada.");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[!] Erro ao processar mensagem: {ex.Message}");
    }

    return Task.CompletedTask;
};

// Iniciar o consumo da fila
await channel.BasicConsumeAsync(queueName, autoAck: true, consumer: consumer);

Console.WriteLine(" Press [enter] to exit.");
Console.ReadLine();

// Classe para representar a estrutura da mensagem assinada
public class SignedMessage
{
    public string Message { get; set; }
    public string Signature { get; set; }
}
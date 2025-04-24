using RabbitMQ.Client;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO;

var factory = new ConnectionFactory { HostName = "localhost" };
using var connection = await factory.CreateConnectionAsync();
using var channel = await connection.CreateChannelAsync();

// Declaração da exchange "direct_logs"
await channel.ExchangeDeclareAsync(exchange: "direct_logs", type: ExchangeType.Direct);

// Definir diretórios para as chaves
var privateKeyDir = "keys/private";
var publicKeyDir = "keys/public";

// Garantir que os diretórios existem
Directory.CreateDirectory(privateKeyDir);
Directory.CreateDirectory(publicKeyDir);

// Caminhos para os arquivos de chaves
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

    // Salvar as chaves em arquivos
    var privateKey = rsa.ExportRSAPrivateKey();
    var publicKey = rsa.ExportRSAPublicKey();
    File.WriteAllBytes(privateKeyPath, privateKey);
    File.WriteAllBytes(publicKeyPath, publicKey);

    Console.WriteLine($"Chaves geradas e salvas em:\n - {privateKeyPath}\n - {publicKeyPath}");
}

// Definir severidade e mensagem
var severity = (args.Length > 0 ? args[0] : "info");
var message = (args.Length > 1 ? string.Join(" ", args.Skip(1).ToArray()) : "Hello World!");

// Serializar e assinar a mensagem
var messageBody = Encoding.UTF8.GetBytes(message);
var signature = rsa.SignData(messageBody, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

// Criar mensagem assinada
var signedMessage = new
{
    Message = message,
    Signature = Convert.ToBase64String(signature)
};

// Publicar na exchange
var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(signedMessage));
await channel.BasicPublishAsync(exchange: "direct_logs", routingKey: severity, body: body);

Console.WriteLine($" [x] Sent '{severity}' : '{message}' with signature.");

Console.WriteLine(" Press [enter] to exit.");
Console.ReadLine();
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO;

var factory = new ConnectionFactory { HostName = "localhost" };
using var connection = await factory.CreateConnectionAsync();
using var channel = await connection.CreateChannelAsync();

// Declaração da exchange "direct_pagamento"
await channel.ExchangeDeclareAsync(exchange: "direct_pagamento", type: "direct");

// Declaração da fila "reserva-criada" (consumida por este serviço)
await channel.QueueDeclareAsync(queue: "reserva-criada", durable: true, exclusive: false, autoDelete: false, arguments: null);

// Diretórios para armazenar as chaves
var privateKeyDir = "keys/private";
var publicKeyDir = "keys/public";

// Criar os diretórios, se não existirem
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

    // Salvar as chaves em arquivos
    var privateKey = rsa.ExportRSAPrivateKey();
    var publicKey = rsa.ExportRSAPublicKey();
    File.WriteAllBytes(privateKeyPath, privateKey);
    File.WriteAllBytes(publicKeyPath, publicKey);

    Console.WriteLine($"Chaves geradas e salvas em:\n - {privateKeyPath}\n - {publicKeyPath}");
}

// Consumidor para a fila "reserva-criada"
var consumer = new AsyncEventingBasicConsumer(channel);
consumer.ReceivedAsync += async (model, ea) =>
{
    try
    {
        // Ler a mensagem da fila "reserva-criada"
        byte[] body = ea.Body.ToArray();
        var message = Encoding.UTF8.GetString(body);
        Console.WriteLine($" [x] Received: {message}");

        // Simular tempo de processamento (5 segundos)
        await Task.Delay(5000);

        // Gerar aleatoriamente se o pagamento foi aprovado ou recusado
        var random = new Random();
        bool pagamentoAprovado = random.Next(0, 2) == 0; // 50% de chance

        // Routing key baseada no status do pagamento
        string routingKey = pagamentoAprovado ? "pagamento.aprovado" : "pagamento.recusado";

        // Criar a mensagem de resposta
        var responseMessage = new { OriginalMessage = message, Status = pagamentoAprovado ? "Aprovado" : "Recusado" };

        // Serializar e assinar a mensagem
        var messageBody = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(responseMessage));
        var signature = rsa.SignData(messageBody, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        // Empacotar a mensagem e a assinatura
        var signedMessage = new
        {
            Message = JsonSerializer.Serialize(responseMessage),
            Signature = Convert.ToBase64String(signature)
        };

        // Publicar mensagem assinada na exchange
        var responseBody = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(signedMessage));
        await channel.BasicPublishAsync(exchange: "direct_pagamento", routingKey: routingKey, body: responseBody);

        Console.WriteLine($" [x] Published to '{routingKey}': {JsonSerializer.Serialize(signedMessage)}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($" [!] Error: {ex.Message}");
    }
};

// Iniciar consumo da fila "reserva-criada"
await channel.BasicConsumeAsync(queue: "reserva-criada", autoAck: true, consumer: consumer);

Console.WriteLine(" [*] Waiting for messages in 'reserva-criada'.");
Console.WriteLine(" Press [enter] to exit.");
Console.ReadLine();
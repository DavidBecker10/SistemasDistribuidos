using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

var itinerariosJsonPath = "../../src/itinerarios.json";
var publicKeyPath = "keys/public/public.key"; // Caminho da chave pública do Pagamento
var builder = WebApplication.CreateBuilder(args);

// Configurar suporte a CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

// Configurar o aplicativo
var app = builder.Build();

// Usar CORS
app.UseCors("AllowAll");

// Estado compartilhado para armazenar mensagens de status de pagamento
var pagamentoStatus = new ConcurrentQueue<string>();

// Configurar RabbitMQ
var factory = new ConnectionFactory { HostName = "localhost" };
using var connection = await factory.CreateConnectionAsync();
using var channel = await connection.CreateChannelAsync();

// Declaração da exchange "direct_pagamento"
await channel.ExchangeDeclareAsync(exchange: "direct_pagamento", type: "direct");

// Carregar a chave pública do Pagamento
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

// Consumidor para a exchange com routing key "pagamento.aprovado"
var consumerPagamentoAprovado = new AsyncEventingBasicConsumer(channel);
consumerPagamentoAprovado.ReceivedAsync += async (model, ea) =>
{
    byte[] body = ea.Body.ToArray();
    var rawMessage = Encoding.UTF8.GetString(body);

    try
    {
        var signedMessage = JsonSerializer.Deserialize<SignedMessage>(rawMessage);

        if (signedMessage != null && VerifySignature(signedMessage.Message, signedMessage.Signature))
        {
            pagamentoStatus.Enqueue($"[Aprovado] {signedMessage.Message}");
            Console.WriteLine($"[Reserva] Pagamento Aprovado (mensagem assinada): {signedMessage.Message}");
        }
        else
        {
            Console.WriteLine("[Reserva] Assinatura inválida para mensagem de pagamento aprovado.");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Reserva] Erro ao processar mensagem: {ex.Message}");
    }

    await Task.CompletedTask;
};

// Consumidor para a exchange com routing key "pagamento.recusado"
var consumerPagamentoRecusado = new AsyncEventingBasicConsumer(channel);
consumerPagamentoRecusado.ReceivedAsync += async (model, ea) =>
{
    byte[] body = ea.Body.ToArray();
    var rawMessage = Encoding.UTF8.GetString(body);

    try
    {
        var signedMessage = JsonSerializer.Deserialize<SignedMessage>(rawMessage);

        if (signedMessage != null && VerifySignature(signedMessage.Message, signedMessage.Signature))
        {
            pagamentoStatus.Enqueue($"[Recusado] {signedMessage.Message}");
            Console.WriteLine($"[Reserva] Pagamento Recusado (mensagem assinada): {signedMessage.Message}");
        }
        else
        {
            Console.WriteLine("[Reserva] Assinatura inválida para mensagem de pagamento recusado.");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Reserva] Erro ao processar mensagem: {ex.Message}");
    }

    await Task.CompletedTask;
};

// Declaração de filas para consumo
var queueAprovado = "reserva-pagamento-aprovado";
var queueRecusado = "reserva-pagamento-recusado";

await channel.QueueDeclareAsync(queue: queueAprovado, durable: true, exclusive: false, autoDelete: false);
await channel.QueueDeclareAsync(queue: queueRecusado, durable: true, exclusive: false, autoDelete: false);

// Vincular as filas à exchange "direct_pagamento" com as routing keys apropriadas
await channel.QueueBindAsync(queue: queueAprovado, exchange: "direct_pagamento", routingKey: "pagamento.aprovado");
await channel.QueueBindAsync(queue: queueRecusado, exchange: "direct_pagamento", routingKey: "pagamento.recusado");

// Iniciar consumo
await channel.BasicConsumeAsync(queue: queueAprovado, autoAck: true, consumer: consumerPagamentoAprovado);
await channel.BasicConsumeAsync(queue: queueRecusado, autoAck: true, consumer: consumerPagamentoRecusado);

// Endpoint para buscar os status de pagamento
app.MapGet("/api/pagamento/status", async (HttpContext context) =>
{
    context.Response.ContentType = "application/json";

    // Retornar todas as mensagens armazenadas
    var mensagens = pagamentoStatus.ToArray();
    pagamentoStatus.Clear();
    await context.Response.WriteAsync(JsonSerializer.Serialize(mensagens));
});

// Endpoint para obter itinerários
app.MapGet("/api/itinerarios", async (HttpContext context) =>
{
    try
    {
        if (!System.IO.File.Exists(itinerariosJsonPath))
        {
            context.Response.StatusCode = 404;
            await context.Response.WriteAsync("Arquivo de itinerários não encontrado.");
            return;
        }

        var itinerarios = await System.IO.File.ReadAllTextAsync(itinerariosJsonPath);
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(itinerarios);
    }
    catch (Exception ex)
    {
        context.Response.StatusCode = 500;
        await context.Response.WriteAsync($"Erro ao carregar itinerários: {ex.Message}");
    }
});

// Endpoint para criar uma reserva
app.MapPost("/api/reserva/criar", async (HttpContext context) =>
{
    try
    {
        // Ler o corpo da requisição
        var body = await new StreamReader(context.Request.Body).ReadToEndAsync();
        Console.WriteLine($"[Reserva Criada] Dados recebidos: {body}");

        // Publicar mensagem na fila "reserva-criada"
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        await channel.BasicPublishAsync(exchange: string.Empty, routingKey: "reserva-criada", body: bodyBytes);

        // Retornar sucesso
        context.Response.StatusCode = 201;
        await context.Response.WriteAsync("Reserva criada e publicada com sucesso!");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Erro ao criar reserva: {ex.Message}");
        context.Response.StatusCode = 500;
        await context.Response.WriteAsync($"Erro ao criar reserva: {ex.Message}");
    }
});

Console.WriteLine(" [*] RabbitMQ consumer running...");
Console.WriteLine(" [*] Waiting for messages. Access '/api/itinerarios' for itineraries.");
Console.WriteLine(" [*] Access '/api/reserva/criar' to create a reservation.");

await app.RunAsync();

// Classe para representar a estrutura da mensagem assinada
public class SignedMessage
{
    public string? Message { get; set; }
    public string? Signature { get; set; }
}
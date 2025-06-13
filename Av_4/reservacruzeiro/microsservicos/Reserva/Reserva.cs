using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Encodings.Web;

var publicKeyPath = "keys/public/public.key"; // Caminho da chave pública do Pagamento
var itinerariosServiceUrl = "http://localhost:5001/api/itinerarios"; // URL do microsserviço de Itinerários
var reservasJsonPath = "reservas.json"; // Arquivo para armazenar as reservas

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var app = builder.Build();

app.UseCors("AllowAll");

// Configuração para SSE
var sseConnections = new ConcurrentDictionary<string, HttpResponse>();

var pagamentoStatus = new ConcurrentQueue<string>();

var factory = new ConnectionFactory { HostName = "localhost" };
using var connection = await factory.CreateConnectionAsync();
using var channel = await connection.CreateChannelAsync();

// exchange "direct_pagamento"
await channel.ExchangeDeclareAsync(exchange: "direct_pagamento", type: "direct");
await channel.QueueDeclareAsync(queue: "reserva-cancelada", durable: true, exclusive: false, autoDelete: false);

// chave publica do Pagamento
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

// SSE Endpoint
app.MapGet("/sse", async (
    HttpContext context,
    CancellationToken cancellationToken) =>
{
    var userId = context.Request.Query["userId"].ToString();


    if (string.IsNullOrEmpty(userId))
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("Parâmetro 'userId' é obrigatório.");
        return;
    }

    context.Response.Headers.Append("Cache-Control", "no-cache");
    context.Response.Headers.Append("Content-Type", "text/event-stream");
    context.Response.Headers.Append("Connection", "keep-alive");


    // Adiciona ou substitui a conexão existente para o userId
    sseConnections[userId] = context.Response;

    Console.WriteLine($"[SSE] Conexão iniciada para userId: {userId}");

    try
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            // Comentário SSE para manter a conexão viva
            await context.Response.WriteAsync(":\n\n");
            await context.Response.Body.FlushAsync();
            await Task.Delay(10_000, cancellationToken); // ping ocasional para manter a conexão viva
        }
    }
    catch (OperationCanceledException) { /* conexão encerrada */ }
    finally
    {
        sseConnections.TryRemove(userId, out _);
        Console.WriteLine($"[SSE] Conexão encerrada para userId: {userId}");
    }

    /*while (!cancellationToken.IsCancellationRequested)
    {
        Console.WriteLine("ENDPOINT SSE");

        var payload = JsonSerializer.Serialize(new { type = "pagamento", data = "aprovado/recusado" });
        await context.Response.WriteAsync("event: pagamento\n", cancellationToken);
        await context.Response.WriteAsync($"data: {payload}\n\n", cancellationToken);

        await context.Response.Body.FlushAsync(cancellationToken);

        await Task.Delay(500, cancellationToken);
    }*/

});

var options = new JsonSerializerOptions
{
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    WriteIndented = true
};

List<dynamic> reservas = File.Exists(reservasJsonPath)
    ? JsonSerializer.Deserialize<List<dynamic>>(File.ReadAllText(reservasJsonPath)) ?? new List<dynamic>()
    : new List<dynamic>();

void SaveReservas() => File.WriteAllText(reservasJsonPath, JsonSerializer.Serialize(reservas, options));

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

            var paymentInfo = JsonSerializer.Deserialize<JsonElement>(signedMessage.Message);
            var originalMessage = JsonSerializer.Deserialize<JsonElement>(paymentInfo.GetProperty("OriginalMessage").GetString());

            if (!originalMessage.TryGetProperty("UserId", out var userIdElement))
            {
                Console.WriteLine("[Reserva] Campo 'UserId' não encontrado.");
                return;
            }

            var userId = userIdElement.GetString();
            if (string.IsNullOrEmpty(userId)) return;

            var sseMessage = $"event: pagamentoAprovado\ndata: {JsonSerializer.Serialize(originalMessage)}\n\n";

            if (sseConnections.TryGetValue(userId, out var response))
            {
                try
                {
                    await response.WriteAsync(sseMessage);
                    await response.Body.FlushAsync();
                }
                catch
                {
                    Console.WriteLine($"[Reserva] Falha ao enviar SSE para {userId}.");
                    sseConnections.TryRemove(userId, out _);
                }
            }

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

            var paymentInfo = JsonSerializer.Deserialize<JsonElement>(signedMessage.Message);

            if (paymentInfo.GetProperty("Status").GetString() == "Recusado")
            {
                var originalMessage = JsonSerializer.Deserialize<JsonElement>(paymentInfo.GetProperty("OriginalMessage").GetString());

                if (!originalMessage.TryGetProperty("Id", out var idElement) ||
                    !originalMessage.TryGetProperty("UserId", out var userIdElement))
                {
                    Console.WriteLine("[Reserva] Campos obrigatórios não encontrados.");
                    return;
                }

                var id = idElement.GetString();
                var userId = userIdElement.GetString();
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(userId)) return;

                var sseMessage = $"event: pagamentoRecusado\ndata: {JsonSerializer.Serialize(originalMessage)}\n\n";


                if (sseConnections.TryGetValue(userId, out var response))
                {
                    try
                    {
                        await response.WriteAsync(sseMessage);
                        await response.Body.FlushAsync();
                    }
                    catch
                    {
                        Console.WriteLine($"[Reserva] Falha ao enviar SSE para {userId}.");
                        sseConnections.TryRemove(userId, out _);
                    }
                }

                Console.WriteLine($"Cancelamento recebido: {id}");

                // Localizar a reserva
                var reservaElement = reservas.FirstOrDefault(r => r.GetProperty("Id").GetString() == id);
                if (reservaElement.ValueKind == JsonValueKind.Undefined)
                {
                    Console.WriteLine("Reserva não encontrada.");
                    return;
                }

                // Obter dados da reserva
                var reservaCanc = new Reserva
                {
                    ItinerarioId = reservaElement.GetProperty("ItinerarioId").GetInt32(),
                    NumeroCabines = reservaElement.GetProperty("NumeroCabines").GetInt32()
                };

                // Remover a reserva da lista e salvar no arquivo
                reservas.RemoveAll(r => r.GetProperty("Id").GetString() == id);
                SaveReservas();

                // Serializar e publicar na fila de cancelamento
                var cancelBody = JsonSerializer.Serialize(reservaCanc, options);
                var bodyBytes = Encoding.UTF8.GetBytes(cancelBody);

                await channel.BasicPublishAsync(exchange: string.Empty, routingKey: "reserva-cancelada", body: bodyBytes);

                Console.WriteLine($"Reserva cancelada publicada: {cancelBody}");
            }
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

var queueAprovado = "reserva-pagamento-aprovado";
var queueRecusado = "reserva-pagamento-recusado";

await channel.QueueDeclareAsync(queue: queueAprovado, durable: true, exclusive: false, autoDelete: false);
await channel.QueueDeclareAsync(queue: queueRecusado, durable: true, exclusive: false, autoDelete: false);

await channel.QueueBindAsync(queue: queueAprovado, exchange: "direct_pagamento", routingKey: "pagamento.aprovado");
await channel.QueueBindAsync(queue: queueRecusado, exchange: "direct_pagamento", routingKey: "pagamento.recusado");

await channel.BasicConsumeAsync(queue: queueAprovado, autoAck: true, consumer: consumerPagamentoAprovado);
await channel.BasicConsumeAsync(queue: queueRecusado, autoAck: true, consumer: consumerPagamentoRecusado);

// Endpoint para buscar os status de pagamento
app.MapGet("/api/pagamento/status", async (HttpContext context) =>
{
    context.Response.ContentType = "application/json";

    var mensagens = pagamentoStatus.ToArray();
    pagamentoStatus.Clear();
    await context.Response.WriteAsync(JsonSerializer.Serialize(mensagens, options));
});

// Endpoint para obter itinerários via microsserviço de Itinerários
app.MapGet("/api/itinerarios", async (HttpContext context) =>
{
    try
    {
        using var httpClient = new HttpClient();
        var response = await httpClient.GetAsync(itinerariosServiceUrl);

        if (response.IsSuccessStatusCode)
        {
            var itinerarios = await response.Content.ReadAsStringAsync();
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(itinerarios);
        }
        else
        {
            context.Response.StatusCode = (int)response.StatusCode;
            await context.Response.WriteAsync("Erro ao obter itinerários do microsserviço de Itinerários.");
        }
    }
    catch (Exception ex)
    {
        context.Response.StatusCode = 500;
        await context.Response.WriteAsync($"Erro ao conectar ao microsserviço de Itinerários: {ex.Message}");
    }
});

app.MapGet("/api/reservas", async (HttpContext context) =>
{
    context.Response.ContentType = "application/json";
    await context.Response.WriteAsync(JsonSerializer.Serialize(reservas, options));
});

// Endpoint para criar uma reserva
app.MapPost("/api/reserva/criar", async (HttpContext context) =>
{
    try
    {
        var body = await new StreamReader(context.Request.Body).ReadToEndAsync();
        var reserva = JsonSerializer.Deserialize<dynamic>(body);
        reservas.Add(reserva);
        SaveReservas();
        Console.WriteLine($"[Reserva Criada] Dados recebidos: {body}");

        // Publicar mensagem na fila "reserva-criada"
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        await channel.BasicPublishAsync(exchange: string.Empty, routingKey: "reserva-criada", body: bodyBytes);

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

// Endpoint para cancelar uma reserva
app.MapPost("/api/reserva/cancelar", async (HttpContext context) =>
{
    try
    {
        // Ler e desserializar o corpo da requisição
        var body = await new StreamReader(context.Request.Body).ReadToEndAsync();
        var cancelamento = JsonSerializer.Deserialize<JsonElement>(body);

        if (!cancelamento.TryGetProperty("Id", out JsonElement idElement))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("Campo 'Id' não encontrado no cancelamento.");
            return;
        }

        var id = idElement.GetString();
        if (string.IsNullOrEmpty(id))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("Id inválido no cancelamento.");
            return;
        }

        Console.WriteLine($"Cancelamento recebido: {id}");

        // Localizar a reserva
        var reservaElement = reservas.FirstOrDefault(r => r.GetProperty("Id").GetString() == id);
        if (reservaElement.ValueKind == JsonValueKind.Undefined)
        {
            context.Response.StatusCode = 404;
            await context.Response.WriteAsync("Reserva não encontrada.");
            return;
        }

        // Obter dados da reserva
        var reserva = new Reserva
        {
            ItinerarioId = reservaElement.GetProperty("ItinerarioId").GetInt32(),
            NumeroCabines = reservaElement.GetProperty("NumeroCabines").GetInt32()
        };

        // Remover a reserva da lista e salvar no arquivo
        reservas.RemoveAll(r => r.GetProperty("Id").GetString() == id);
        SaveReservas();

        // Serializar e publicar na fila de cancelamento
        var cancelBody = JsonSerializer.Serialize(reserva, options);
        var bodyBytes = Encoding.UTF8.GetBytes(cancelBody);

        await channel.BasicPublishAsync(exchange: string.Empty, routingKey: "reserva-cancelada", body: bodyBytes);

        Console.WriteLine($"Reserva cancelada publicada: {cancelBody}");

        context.Response.StatusCode = 201;
        await context.Response.WriteAsync("Reserva cancelada com sucesso!");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Erro ao cancelar reserva: {ex.Message}");
        context.Response.StatusCode = 500;
        await context.Response.WriteAsync($"Erro ao cancelar reserva: {ex.Message}");
    }
});

Console.WriteLine(" [*] RabbitMQ consumer running...");
Console.WriteLine(" [*] Waiting for messages. Access '/api/itinerarios' for itineraries.");
Console.WriteLine(" [*] Access '/api/reserva/criar' to create a reservation.");

await app.RunAsync("http://localhost:5000");

public class SignedMessage
{
    public string? Message { get; set; }
    public string? Signature { get; set; }
}

public class Reserva
{
    public int? ItinerarioId { get; set; }
    public int? NumeroCabines { get; set; }
}
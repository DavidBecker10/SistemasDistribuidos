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

var promocoes = new ConcurrentDictionary<string, bool>(); // Variável para armazenar interesse em promoções

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

await channel.ExchangeDeclareAsync(exchange: "promocoes", type: ExchangeType.Fanout);

await channel.QueueDeclareAsync(queue: "bilhete-gerado", durable: true, exclusive: false, autoDelete: false);

await channel.ExchangeDeclareAsync(exchange: "reserva-criada", type: ExchangeType.Fanout);
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
        var rawJson = JsonSerializer.Deserialize<string>(rawMessage);

        var paymentInfo = JsonSerializer.Deserialize<JsonElement>(rawJson);

        pagamentoStatus.Enqueue($"[Aprovado] {rawJson}");
        Console.WriteLine($"[Reserva] Pagamento Aprovado (mensagem assinada): {rawJson}");

        if (!paymentInfo.TryGetProperty("UserId", out var userIdElement))
        {
            Console.WriteLine("[Reserva] Campo 'UserId' não encontrado.");
            return;
        }

        var userId = userIdElement.GetString();
        if (string.IsNullOrEmpty(userId)) return;

        var sseMessage = $"event: pagamentoAprovado\ndata: {JsonSerializer.Serialize(paymentInfo)}\n\n";

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
        var rawJson = JsonSerializer.Deserialize<string>(rawMessage);

        var paymentInfo = JsonSerializer.Deserialize<JsonElement>(rawJson);

        pagamentoStatus.Enqueue($"[Recusado] {rawJson}");
        Console.WriteLine($"[Reserva] Pagamento Recusado (mensagem assinada): {rawJson}");

        if (paymentInfo.GetProperty("status").GetString() == "Recusado")
        {
            if (!paymentInfo.TryGetProperty("Id", out var idElement) ||
                !paymentInfo.TryGetProperty("UserId", out var userIdElement))
            {
                Console.WriteLine("[Reserva] Campos obrigatórios não encontrados.");
                return;
            }

            var id = idElement.GetString();
            var userId = userIdElement.GetString();
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(userId)) return;

            var sseMessage = $"event: pagamentoRecusado\ndata: {JsonSerializer.Serialize(paymentInfo)}\n\n";

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

        // mensagem para exchange "reserva-criada"
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        await channel.BasicPublishAsync(exchange: "reserva-criada", routingKey: string.Empty, body: bodyBytes);

        // Solicitar link de pagamento ao microsserviço de pagamento
        using var httpClient = new HttpClient();
        var paymentResponse = await httpClient.PostAsync("http://localhost:5003/api/gerar-link", new StringContent(body, Encoding.UTF8, "application/json"));
        var paymentContent = await paymentResponse.Content.ReadAsStringAsync();

        if (paymentResponse.IsSuccessStatusCode)
        {
            Console.WriteLine($"[x] Link de pagamento recebido: {paymentContent}");
            var paymentInfo = JsonSerializer.Deserialize<JsonElement>(paymentContent);
            reserva["paymentLink"] = paymentInfo.GetProperty("paymentLink").GetString();
            reserva["paymentId"] = paymentInfo.GetProperty("paymentId").GetString();
        }

        // Salvar reserva atualizada com o link de pagamento
        SaveReservas();

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

// Endpoint para registrar interesse em promoções
app.MapPost("/api/promocoes", async (HttpContext context) =>
{
    try
    {
        var body = await new StreamReader(context.Request.Body).ReadToEndAsync();

        var interesseRequest = JsonSerializer.Deserialize<InteressePromocoesRequest>(body, options);
        if (interesseRequest == null || string.IsNullOrEmpty(interesseRequest.UserId))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("Requisição inválida. O campo 'UserId' é obrigatório.");
            return;
        }

        promocoes[interesseRequest.UserId] = interesseRequest.Interested ?? false;
        Console.WriteLine($"Interesse registrado: {JsonSerializer.Serialize(promocoes, options)}");

        context.Response.ContentType = "application/json";
        context.Response.StatusCode = 200;
        await context.Response.WriteAsync(JsonSerializer.Serialize(new { message = "Interesse registrado com sucesso." }, options));
    }
    catch (Exception ex)
    {
        context.Response.StatusCode = 500;
        await context.Response.WriteAsync($"Erro interno no servidor: {ex.Message}");
    }
});

// Consumidor para a exchange "promoções"
var consumerPromocoes = new AsyncEventingBasicConsumer(channel);
consumerPromocoes.ReceivedAsync += async (model, ea) =>
{
    byte[] body = ea.Body.ToArray();
    var rawMessage = Encoding.UTF8.GetString(body);

    try
    {
        // Log da mensagem recebida
        Console.WriteLine($"[Reserva] Promoção recebida: {rawMessage}");

        // Criar a mensagem SSE
        var sseMessage = $"event: promocao\ndata: {JsonSerializer.Serialize(rawMessage)}\n\n";

        // Notificar usuários interessados
        foreach (var (userId, interesse) in promocoes)
        {
            // Verificar se o usuário está interessado em promoções
            if (!interesse || !sseConnections.TryGetValue(userId, out var response))
                continue;

            try
            {
                // Enviar a promoção via SSE
                await response.WriteAsync(sseMessage);
                await response.Body.FlushAsync();
                Console.WriteLine($"[Reserva] Promoção enviada para {userId}.");
            }
            catch
            {
                // Remover conexão quebrada
                Console.WriteLine($"[Reserva] Falha ao enviar promoção para {userId}. Conexão removida.");
                sseConnections.TryRemove(userId, out _);
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Reserva] Erro ao processar promoção: {ex.Message}");
    }

    await Task.CompletedTask;
};
await channel.QueueDeclareAsync(queue: "promocoes", durable: true, exclusive: false, autoDelete: false);
await channel.QueueBindAsync(queue: "promocoes", exchange: "promocoes", routingKey: string.Empty);
await channel.BasicConsumeAsync(queue: "promocoes", autoAck: true, consumer: consumerPromocoes);

// Consumidor para a fila "bilhete-gerado"
var consumerBilhete = new AsyncEventingBasicConsumer(channel);
consumerBilhete.ReceivedAsync += async (model, ea) =>
{
    byte[] body = ea.Body.ToArray();
    var rawMessage = Encoding.UTF8.GetString(body);

    try
    {
        // Log da mensagem recebida
        Console.WriteLine($"[Reserva] Bilhete gerado recebido: {rawMessage}");

        // Parse da mensagem recebida
        var responseMessage = JsonSerializer.Deserialize<JsonElement>(rawMessage);

        // Criar a mensagem SSE
        var sseMessage = $"event: bilheteGerado\ndata: {JsonSerializer.Serialize(responseMessage)}\n\n";

        // Notificar usuários interessados
        foreach (var (userId, response) in sseConnections)
        {
            try
            {
                // Enviar a mensagem via SSE
                await response.WriteAsync(sseMessage);
                await response.Body.FlushAsync();
                Console.WriteLine($"[Reserva] Notificação de bilhete gerado enviada para {userId}.");
            }
            catch
            {
                // Remover conexão quebrada
                Console.WriteLine($"[Reserva] Falha ao enviar bilhete gerado para {userId}. Conexão removida.");
                sseConnections.TryRemove(userId, out _);
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Reserva] Erro ao processar bilhete gerado: {ex.Message}");
    }

    await Task.CompletedTask;
};
await channel.BasicConsumeAsync(queue: "bilhete-gerado", autoAck: true, consumer: consumerBilhete);

Console.WriteLine(" [*] Waiting for messages from 'bilhete-gerado'.");

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

// Classe para deserializar o corpo da requisição
public class InteressePromocoesRequest
{
    public string? UserId { get; set; }
    public bool? Interested { get; set; }
}
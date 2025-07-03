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

var app = builder.Build();

var options = new JsonSerializerOptions
{
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    WriteIndented = true // Opcional, para saída formatada
};

// Dicionário para armazenar as informações de pagamentos com status
var paymentStatusCache = new ConcurrentDictionary<string, string>();

await channel.ExchangeDeclareAsync(exchange: "direct_pagamento", type: "direct");

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

app.MapPost("/api/pagamento/processar/{paymentId}", async (HttpContext context) =>
{
    try
    {
        var paymentId = context.Request.RouteValues["paymentId"]?.ToString();

        if (string.IsNullOrEmpty(paymentId))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("ID de pagamento inválido.");
            return;
        }

        Console.WriteLine($"[Pagamento] Processando pagamento para ID: {paymentId}");

        var externalPaymentUrl = $"http://localhost:5002/pagar/{paymentId}";

        // Requisição ao sistema de pagamento externo
        using var httpClient = new HttpClient();
        var response = await httpClient.GetAsync(externalPaymentUrl);

        if (!response.IsSuccessStatusCode)
        {
            context.Response.StatusCode = 500;
            await context.Response.WriteAsync($"Erro ao acessar o sistema de pagamento externo. Código: {response.StatusCode}");
            return;
        }

        var paymentResponse = await response.Content.ReadAsStringAsync();
        Console.WriteLine($"[Pagamento] Resposta do sistema de pagamento externo: {paymentResponse}");

        // Retornar a resposta do sistema externo para o front-end
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(paymentResponse);
    }
    catch (Exception ex)
    {
        context.Response.StatusCode = 500;
        await context.Response.WriteAsync($"Erro ao processar pagamento: {ex.Message}");
        Console.WriteLine($"[!] Erro ao processar pagamento: {ex.Message}");
    }
});

app.MapPost("/api/gerar-link", async (HttpContext context) =>
{
    try
    {
        var requestBody = await new StreamReader(context.Request.Body).ReadToEndAsync();
        Console.WriteLine($"[x] Payment link request received: {requestBody}");

        // Solicitar link de pagamento ao sistema externo
        using var httpClient = new HttpClient();
        var externalResponse = await httpClient.PostAsync("http://localhost:5002/api/gerar-link", new StringContent(requestBody, Encoding.UTF8, "application/json"));
        var responseContent = await externalResponse.Content.ReadAsStringAsync();

        if (externalResponse.IsSuccessStatusCode)
        {
            Console.WriteLine($"[x] Link de pagamento obtido: {responseContent}");
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(responseContent);
        }
        else
        {
            Console.WriteLine($"[!] Erro ao obter link: {externalResponse.StatusCode}");
            context.Response.StatusCode = (int)externalResponse.StatusCode;
            await context.Response.WriteAsync("Erro ao obter link de pagamento.");
        }
    }
    catch (Exception ex)
    {
        context.Response.StatusCode = 500;
        await context.Response.WriteAsync($"Erro interno do servidor: {ex.Message}");
    }
});

Console.WriteLine(" [*] Waiting for messages in 'reserva-criada'.");
Console.WriteLine(" Press [enter] to exit.");
Console.ReadLine();

await app.RunAsync("http://localhost:5003");
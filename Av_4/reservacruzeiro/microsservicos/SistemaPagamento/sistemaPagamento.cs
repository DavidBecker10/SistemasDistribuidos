using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var options = new JsonSerializerOptions
{
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    WriteIndented = true
};

app.MapGet("/pagar/{paymentId}", async (HttpContext context) =>
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

        Console.WriteLine($"[x] Processando pagamento para ID: {paymentId}");

        // Simular o processamento do pagamento (aprovado ou recusado)
        var random = new Random();
        string status = random.Next(0, 2) == 0 ? "Aprovado" : "Recusado";

        // Construir a resposta do pagamento
        var response = new
        {
            paymentId,
            status
        };

        Console.WriteLine($"[x] Pagamento {status} para ID: {paymentId}");

        // Enviar a resposta ao usuário
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(response, options));
    }
    catch (Exception ex)
    {
        context.Response.StatusCode = 500;
        await context.Response.WriteAsync($"Erro interno do servidor: {ex.Message}");
        Console.WriteLine($"[!] Erro ao processar pagamento: {ex.Message}");
    }
});

app.MapPost("/api/gerar-link", async (HttpContext context) =>
{
    try
    {
        var paymentId = Guid.NewGuid().ToString();
        var paymentLink = $"http://localhost:5002/pagar/{paymentId}";

        var response = new
        {
            paymentId,
            paymentLink
        };

        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(response, options));
        Console.WriteLine($"[x] Payment link generated: {response.paymentLink}");
    }
    catch (Exception ex)
    {
        context.Response.StatusCode = 500;
        await context.Response.WriteAsync($"Erro interno do servidor: {ex.Message}");
    }
});

Console.WriteLine("Sistema de pagamento externo iniciado em http://localhost:5002");
await app.RunAsync("http://localhost:5002");
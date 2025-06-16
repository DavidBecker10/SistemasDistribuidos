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

app.MapPost("/api/pagamentos", async (HttpContext context) =>
{
    try
    {
        // Lê o corpo da requisição
        var requestBody = await new StreamReader(context.Request.Body).ReadToEndAsync();
        Console.WriteLine($" [x] Received payment data: {requestBody}");

        // Delay de 5 segundos para simular processamento
        await Task.Delay(5000);

        // Randomizar o status do pagamento
        var random = new Random();
        string status = random.Next(0, 2) == 0 ? "Aprovado" : "Recusado";

        // Gerar um novo ID para a transação
        string transactionId = Guid.NewGuid().ToString();

        // Montar a resposta com as informações recebidas e os novos dados
        var response = JsonSerializer.Deserialize<Dictionary<string, object>>(requestBody) ?? new Dictionary<string, object>();
        response["status"] = status;
        response["transactionId"] = transactionId;

        var responseJson = JsonSerializer.Serialize(response, options);

        // Enviar a resposta
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(responseJson);

        Console.WriteLine($" [x] Responded with: {responseJson}");
    }
    catch (Exception ex)
    {
        context.Response.StatusCode = 500;
        await context.Response.WriteAsync($"Erro interno do servidor: {ex.Message}");
        Console.WriteLine($" [!] Error: {ex.Message}");
    }
});

Console.WriteLine("Sistema de pagamento externo iniciado em http://localhost:5002");
await app.RunAsync("http://localhost:5002");
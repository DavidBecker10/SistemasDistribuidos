using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using System.Text;

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

var app = builder.Build();

app.UseCors("AllowAll");

// Endpoint para obter pagamentos
app.MapGet("/api/pagamentos", async (HttpContext context) =>
{
    try
    {

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

Console.WriteLine(" [*] Microsserviço de Itinerários iniciado.");
Console.WriteLine(" [*] Access '/api/itinerarios' to retrieve itineraries.");
await app.RunAsync("http://localhost:5002");

// Método para salvar itinerários atualizados no JSON
static void SalvarItinerarios(List<Itinerario> itinerarios, string path)
{
    var json = JsonSerializer.Serialize(itinerarios, new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(path, json);
}

// Classe para representar itinerários
public class Itinerario
{
    public int Id { get; set; }
    public List<string> DatasPartida { get; set; }
    public string NomeNavio { get; set; }
    public string PortoEmbarque { get; set; }
    public string PortoDesembarque { get; set; }
    public List<string> LugaresVisitados { get; set; }
    public int NumeroNoites { get; set; }
    public decimal ValorPorPessoa { get; set; }
    public int CabinesDisponiveis { get; set; }
}

// Classe para representar reservas
public class Reserva
{
    public int ItinerarioId { get; set; }
    public int NumeroCabines { get; set; }
}
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using System.Text;

var itinerariosJsonPath = "../../src/itinerarios.json";
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

// Estrutura para representar itinerários
var itinerarios = File.Exists(itinerariosJsonPath)
    ? JsonSerializer.Deserialize<List<Itinerario>>(File.ReadAllText(itinerariosJsonPath), new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    }) ?? new List<Itinerario>()
    : new List<Itinerario>();

Console.WriteLine($"Itinerários carregados: {JsonSerializer.Serialize(itinerarios, new JsonSerializerOptions { WriteIndented = true })}");

// Configurar RabbitMQ
var factory = new ConnectionFactory { HostName = "localhost" };
using var connection = await factory.CreateConnectionAsync();
using var channel = await connection.CreateChannelAsync();

// Declaração das filas
await channel.QueueDeclareAsync(queue: "reserva-criada", durable: true, exclusive: false, autoDelete: false, arguments: null);
await channel.QueueDeclareAsync(queue: "reserva-cancelada", durable: true, exclusive: false, autoDelete: false, arguments: null);

// Consumidor para a fila "reserva-criada"
var consumerReservaCriada = new AsyncEventingBasicConsumer(channel);
consumerReservaCriada.ReceivedAsync += async (model, ea) =>
{
    var body = ea.Body.ToArray();
    var message = Encoding.UTF8.GetString(body);
    try
    {
        var reserva = JsonSerializer.Deserialize<Reserva>(message);
        if (reserva != null)
        {
            var itinerario = itinerarios.Find(i => i.Id == reserva.ItinerarioId);
            if (itinerario != null && itinerario.CabinesDisponiveis >= reserva.NumeroCabines)
            {
                itinerario.CabinesDisponiveis -= reserva.NumeroCabines;
                Console.WriteLine($"[Itinerário Atualizado] Reserva criada. ID: {reserva.ItinerarioId}, Cabines Disponíveis: {itinerario.CabinesDisponiveis}");
                SalvarItinerarios(itinerarios, itinerariosJsonPath);
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Erro] Falha ao processar mensagem de reserva criada: {ex.Message}");
    }
    await Task.CompletedTask;
};

// Consumidor para a fila "reserva-cancelada"
var consumerReservaCancelada = new AsyncEventingBasicConsumer(channel);
consumerReservaCancelada.ReceivedAsync += async (model, ea) =>
{
    var body = ea.Body.ToArray();
    var message = Encoding.UTF8.GetString(body);
    try
    {
        var reserva = JsonSerializer.Deserialize<Reserva>(message);
        Console.WriteLine(reserva.ItinerarioId);
        if (reserva != null)
        {
            var itinerario = itinerarios.Find(i => i.Id == reserva.ItinerarioId);
            if (itinerario != null)
            {
                itinerario.CabinesDisponiveis += reserva.NumeroCabines;
                Console.WriteLine($"[Itinerário Atualizado] Reserva cancelada. ID: {reserva.ItinerarioId}, Cabines Disponíveis: {itinerario.CabinesDisponiveis}");
                SalvarItinerarios(itinerarios, itinerariosJsonPath);
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Erro] Falha ao processar mensagem de reserva cancelada: {ex.Message}");
    }
    await Task.CompletedTask;
};

// Consumir filas
await channel.BasicConsumeAsync(queue: "reserva-criada", autoAck: true, consumer: consumerReservaCriada);
await channel.BasicConsumeAsync(queue: "reserva-cancelada", autoAck: true, consumer: consumerReservaCancelada);

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

Console.WriteLine(" [*] Microsserviço de Itinerários iniciado.");
Console.WriteLine(" [*] Access '/api/itinerarios' to retrieve itineraries.");
await app.RunAsync("http://localhost:5001");

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
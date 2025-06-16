using RabbitMQ.Client;
using System.Text;
using System.Threading.Tasks;

var factory = new ConnectionFactory { HostName = "localhost" };
using var connection = await factory.CreateConnectionAsync();
using var channel = await connection.CreateChannelAsync();

await channel.ExchangeDeclareAsync(exchange: "promocoes", type: ExchangeType.Fanout);

Random random = new Random();

while (true)
{
    var message = $"DESCONTO DE {random.Next(10, 51)}% NO ITINERÁRIO DE ID {random.Next(1, 20)}!";
    var body = Encoding.UTF8.GetBytes(message);

    await channel.BasicPublishAsync(exchange: "promocoes", routingKey: string.Empty, body: body);
    Console.WriteLine($" [x] Sent: '{message}'");

    Thread.Sleep(5000);
}

Console.WriteLine(" Press [enter] to exit.");
Console.ReadLine();
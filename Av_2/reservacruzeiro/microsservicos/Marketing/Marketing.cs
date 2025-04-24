using RabbitMQ.Client;
using System.Text;
using System.Threading.Tasks;

var factory = new ConnectionFactory { HostName = "localhost" };
using var connection = await factory.CreateConnectionAsync();
using var channel = await connection.CreateChannelAsync();

await channel.ExchangeDeclareAsync(exchange: "direct_logs", type: ExchangeType.Direct);

Random random = new Random();

string local = "";

var message = "PROMOCAO";
var body = Encoding.UTF8.GetBytes(message);

int i = 0;
while (i < 10)
{
    // Randomiza o local de promocao a cada segundos
    int randPromo = random.Next(1, 4);

    if (randPromo == 1)
        local = "curitiba";
    else if (randPromo == 2)
        local = "saopaulo";
    else
        local = "rio";

    await channel.BasicPublishAsync(exchange: "direct_logs", routingKey: local, body: body);
    Console.WriteLine($" [x] Sent '{local}' : '{message}'");

    Thread.Sleep(5000);
    i++;
}

Console.WriteLine(" Press [enter] to exit.");
Console.ReadLine();
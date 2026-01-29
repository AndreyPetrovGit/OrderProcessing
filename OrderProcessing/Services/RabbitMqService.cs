using System.Text;
using System.Text.Json;
using RabbitMQ.Client;

namespace OrderProcessing.Services;

public class RabbitMqService : IAsyncDisposable
{
    private readonly IConnection _connection;
    private readonly IChannel _channel;
    private readonly string _queueName;

    private RabbitMqService(IConnection connection, IChannel channel, string queueName)
    {
        _connection = connection;
        _channel = channel;
        _queueName = queueName;
    }

    public static async Task<RabbitMqService> CreateAsync(string hostName, string queueName)
    {
        var factory = new ConnectionFactory { HostName = hostName };
        
        IConnection? connection = null;
        for (int i = 0; i < 30; i++)
        {
            try
            {
                connection = await factory.CreateConnectionAsync();
                Console.WriteLine("Connected to RabbitMQ successfully!");
                break;
            }
            catch (Exception)
            {
                Console.WriteLine($"RabbitMQ connection attempt {i + 1}/30 failed. Retrying in 2s...");
                await Task.Delay(2000);
            }
        }
        
        if (connection == null)
            throw new Exception("Failed to connect to RabbitMQ after 30 attempts");
        
        var channel = await connection.CreateChannelAsync();
        
        await channel.QueueDeclareAsync(
            queue: queueName,
            durable: true,
            exclusive: false,
            autoDelete: false);

        return new RabbitMqService(connection, channel, queueName);
    }

    public async Task PublishOrderAsync(Guid orderId)
    {
        var message = JsonSerializer.Serialize(new { OrderId = orderId });
        var body = Encoding.UTF8.GetBytes(message);

        await _channel.BasicPublishAsync(
            exchange: string.Empty,
            routingKey: _queueName,
            mandatory: false,
            basicProperties: new BasicProperties { Persistent = true },
            body: body);
    }

    public IChannel GetChannel() => _channel;
    public string GetQueueName() => _queueName;

    public async ValueTask DisposeAsync()
    {
        await _channel.CloseAsync();
        await _connection.CloseAsync();
    }
}

using RabbitMQ.Client;

namespace OrderProcessing.Services;

public interface IRabbitMqService
{
    Task PublishOrderAsync(Guid orderId);
    IChannel GetChannel();
    string GetQueueName();
}

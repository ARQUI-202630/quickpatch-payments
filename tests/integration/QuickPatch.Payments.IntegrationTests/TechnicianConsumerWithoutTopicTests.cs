using Confluent.Kafka;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using QuickPatch.Payments.Infrastructure.Messaging;

using Testcontainers.Kafka;

namespace QuickPatch.Payments.IntegrationTests;

/// <summary>
/// <c>technician.registered</c> lo consume Payments desde SCRUM-501: en un despliegue el servicio puede arrancar antes que el topic
/// exista, y el consumidor debe esperar sin detener el servicio. Kafka propio y sin topics creados.
/// </summary>
public sealed class TechnicianConsumerWithoutTopicTests : IAsyncLifetime
{
    private readonly KafkaContainer kafka = new KafkaBuilder("confluentinc/cp-kafka:7.7.1").Build();

    public Task InitializeAsync() => kafka.StartAsync();

    public Task DisposeAsync() => kafka.DisposeAsync().AsTask();

    [Fact]
    public async Task TopicInexistente_ElConsumidorSigueVivoYReintenta()
    {
        var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = kafka.GetBootstrapAddress(),
            GroupId = "payments-service",
            EnableAutoCommit = false,
            AutoOffsetReset = AutoOffsetReset.Earliest,
        }).Build();
        var scopes = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        using var service = new TechnicianRegisteredConsumer(scopes, consumer, NullLogger<TechnicianRegisteredConsumer>.Instance);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromSeconds(8));

        Assert.NotNull(service.ExecuteTask);
        Assert.False(service.ExecuteTask!.IsCompleted, "El consumidor terminó en lugar de reintentar.");

        await service.StopAsync(CancellationToken.None);
    }
}
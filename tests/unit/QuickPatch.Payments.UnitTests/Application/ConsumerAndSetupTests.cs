using Confluent.Kafka;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using QuickPatch.Payments.Application;
using QuickPatch.Payments.Application.Abstractions;
using QuickPatch.Payments.Application.Payments;
using QuickPatch.Payments.Infrastructure;
using QuickPatch.Payments.Infrastructure.Messaging;
using QuickPatch.Payments.Infrastructure.Options;
using QuickPatch.Payments.UnitTests.Support;

namespace QuickPatch.Payments.UnitTests.Application;

public class ConsumerAndSetupTests
{
    private static ServiceProvider Services(InMemoryPayments store, IPaymentProvider? provider = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApplication();
        services.AddSingleton<ITenantUnitOfWork>(store);
        services.AddSingleton<IPaymentRepository>(store);
        services.AddSingleton<IProcessedEventRepository>(store);
        services.AddSingleton<IOutbox>(store);
        services.AddSingleton<IReconciliationSource>(store);
        services.AddSingleton<IPaymentProvider>(provider ?? new ScriptedProvider());
        return services.BuildServiceProvider();
    }

    private static string Message(Guid tenant, Guid request, string amount = "\"185000.00\"", string type = "service-request.completed") =>
        $$$"""
        {"eventId":"{{{Guid.NewGuid()}}}","eventType":"{{{type}}}","eventVersion":1,"occurredAt":"2026-10-21T15:00:00Z",
         "correlationId":"{{{Guid.NewGuid()}}}","tenantId":"{{{tenant}}}","producer":"service-request-service",
         "data":{"serviceRequestId":"{{{request}}}","clientId":"{{{Guid.NewGuid()}}}","companyId":null,
                 "technicianId":"{{{Guid.NewGuid()}}}","quoteId":"{{{Guid.NewGuid()}}}","totalAmount":{{{amount}}},
                 "completedAt":"2026-10-21T15:00:00Z"}}
        """;

    [Fact]
    public async Task Consumidor_AplicaElEvento_YDescartaLoQueNoCumpleElContrato()
    {
        var store = new InMemoryPayments();
        await using var services = Services(store);
        using var kafka = new ConsumerBuilder<string, string>(new ConsumerConfig { BootstrapServers = "localhost:1", GroupId = "pruebas" }).Build();
        var consumer = new ServiceRequestCompletedConsumer(
            services.GetRequiredService<IServiceScopeFactory>(), kafka, NullLogger<ServiceRequestCompletedConsumer>.Instance);
        var tenant = Guid.NewGuid();

        Assert.True(await consumer.TryProcessAsync(Message(tenant, Guid.NewGuid()), CancellationToken.None));
        Assert.True(await consumer.TryProcessAsync("{no es json", CancellationToken.None));
        Assert.True(await consumer.TryProcessAsync(Message(tenant, Guid.NewGuid(), type: "otro.evento"), CancellationToken.None));
        Assert.True(await consumer.TryProcessAsync(Message(tenant, Guid.Empty), CancellationToken.None));
        Assert.True(await consumer.TryProcessAsync(Message(Guid.Empty, Guid.NewGuid()), CancellationToken.None));
        Assert.True(await consumer.TryProcessAsync(Message(tenant, Guid.NewGuid(), "null"), CancellationToken.None));
        Assert.True(await consumer.TryProcessAsync("null", CancellationToken.None));

        Assert.Single(store.Payments);
    }

    [Fact]
    public async Task Consumidor_SiFallaLaBase_PideReintento()
    {
        await using var services = new ServiceCollection()
            .AddLogging()
            .AddApplication()
            .AddSingleton<ITenantUnitOfWork, FailingUnitOfWork>()
            .AddSingleton<IPaymentRepository>(new InMemoryPayments())
            .AddSingleton<IProcessedEventRepository>(new InMemoryPayments())
            .BuildServiceProvider();
        using var kafka = new ConsumerBuilder<string, string>(new ConsumerConfig { BootstrapServers = "localhost:1", GroupId = "pruebas" }).Build();
        var consumer = new ServiceRequestCompletedConsumer(
            services.GetRequiredService<IServiceScopeFactory>(), kafka, NullLogger<ServiceRequestCompletedConsumer>.Instance);

        Assert.False(await consumer.TryProcessAsync(Message(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None));
    }

    [Fact]
    public async Task TareaDeConciliacion_UnaVuelta_CierraLosPendientes()
    {
        var store = new InMemoryPayments();
        var (tenant, client) = (Guid.NewGuid(), Guid.NewGuid());
        var payment = store.SeedPending(tenant, client);
        payment.StartProcessing(client, """{"name":"Ana","documentType":"CC","documentNumber":"1234","email":"a@b.co"}""", DateTimeOffset.UtcNow);
        store.Pending.Add(new PendingReconciliation(tenant, payment.Id));
        await using var services = Services(store, new QuickPatch.Payments.Infrastructure.Providers.SimulatedPaymentProvider());
        var job = new ReconciliationJob(
            services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new ReconciliationOptions { Interval = TimeSpan.FromMilliseconds(10) }),
            TimeProvider.System,
            NullLogger<ReconciliationJob>.Instance);

        Assert.Equal(1, await job.RunOnceAsync(CancellationToken.None));
        Assert.Equal("rechazado", payment.Status);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await job.StartAsync(cts.Token);
        await Task.Delay(50);
        await job.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void Infraestructura_SinKafka_NoRegistraConsumidores_YSinConciliacionNoLaRegistra()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Reconciliation:Enabled"] = "false",
        }).Build();
        var services = new ServiceCollection().AddInfrastructure(config);
        var withKafka = new ServiceCollection().AddInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Kafka:BootstrapServers"] = "localhost:9092" }).Build());

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(ReconciliationJob));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(ServiceRequestCompletedConsumer));
        Assert.Contains(withKafka, d => d.ServiceType == typeof(ServiceRequestCompletedConsumer));
        Assert.Contains(withKafka, d => d.ServiceType == typeof(ReconciliationJob));
        Assert.Contains(withKafka, d => d.ServiceType == typeof(IHostedService));
        Assert.Throws<ArgumentNullException>(() => QuickPatch.Payments.Application.DependencyInjection.AddApplication(null!));
        Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddInfrastructure(null!));
    }

    private sealed class FailingUnitOfWork : ITenantUnitOfWork
    {
        public Task<T> ExecuteAsync<T>(Guid tenantId, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("La base no responde.");
    }

    [Fact]
    public void Handlers_SeResuelvenDelContenedor()
    {
        using var services = Services(new InMemoryPayments());
        using var scope = services.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<PayHandler>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<GetPaymentHandler>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ReconcilePaymentsHandler>());
    }
}
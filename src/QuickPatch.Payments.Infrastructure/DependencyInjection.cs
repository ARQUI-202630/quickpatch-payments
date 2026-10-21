using Confluent.Kafka;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using QuickPatch.Payments.Application.Abstractions;
using QuickPatch.Payments.Infrastructure.Messaging;
using QuickPatch.Payments.Infrastructure.Options;
using QuickPatch.Payments.Infrastructure.Persistence;
using QuickPatch.Payments.Infrastructure.Providers;

namespace QuickPatch.Payments.Infrastructure;

/// <summary>
/// Capa de infraestructura: PostgreSQL (EF Core), Kafka y la pasarela de pagos (SDD 6.3).
/// Implementa las interfaces que definen Application y Domain.
/// </summary>
public static class DependencyInjection
{
    public const string ConnectionStringName = "Payments";
    public const string ReadyTag = "ready";

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<KafkaOptions>(configuration.GetSection(KafkaOptions.Section));
        services.Configure<OutboxOptions>(configuration.GetSection(OutboxOptions.Section));
        services.Configure<ReconciliationOptions>(configuration.GetSection(ReconciliationOptions.Section));

        services.AddDbContext<PaymentsDbContext>(options => options.UseNpgsql(configuration.GetConnectionString(ConnectionStringName)));

        services.AddScoped<ITenantUnitOfWork, TenantUnitOfWork>();
        services.AddScoped<IPaymentRepository, PaymentRepository>();
        services.AddScoped<IProcessedEventRepository, ProcessedEventRepository>();
        services.AddScoped<IReconciliationSource, ReconciliationSource>();
        services.AddScoped<IOutbox, EfOutbox>();

        // Pasarela simulada del MVP (ADR-009 modificado). Una pasarela certificada se registra aquí en su lugar.
        services.AddSingleton<IPaymentProvider, SimulatedPaymentProvider>();

        services.AddHealthChecks().AddDbContextCheck<PaymentsDbContext>("postgresql", tags: [ReadyTag]);

        var reconciliation = configuration.GetSection(ReconciliationOptions.Section).Get<ReconciliationOptions>() ?? new ReconciliationOptions();
        if (reconciliation.Enabled)
        {
            services.AddSingleton<ReconciliationJob>();
            services.AddHostedService(sp => sp.GetRequiredService<ReconciliationJob>());
        }

        var kafka = configuration.GetSection(KafkaOptions.Section).Get<KafkaOptions>() ?? new KafkaOptions();
        if (kafka.Enabled)
        {
            services.AddSingleton(_ => new ProducerBuilder<string, string>(new ProducerConfig
            {
                BootstrapServers = kafka.BootstrapServers,
                Acks = Acks.All,
                EnableIdempotence = true,
            }).Build());
            services.AddSingleton(_ => new ConsumerBuilder<string, string>(new ConsumerConfig
            {
                BootstrapServers = kafka.BootstrapServers,
                GroupId = kafka.ConsumerGroupId,
                EnableAutoCommit = false,
                AutoOffsetReset = AutoOffsetReset.Earliest,
            }).Build());

            services.AddSingleton<OutboxPublisher>();
            services.AddHostedService(sp => sp.GetRequiredService<OutboxPublisher>());
            services.AddSingleton<ServiceRequestCompletedConsumer>();
            services.AddHostedService(sp => sp.GetRequiredService<ServiceRequestCompletedConsumer>());
        }

        return services;
    }
}
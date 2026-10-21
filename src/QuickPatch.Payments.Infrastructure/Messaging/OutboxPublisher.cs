using System.Text;

using Confluent.Kafka;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using QuickPatch.Payments.Infrastructure.Options;
using QuickPatch.Payments.Infrastructure.Persistence;

namespace QuickPatch.Payments.Infrastructure.Messaging;

/// <summary>
/// Publicador del Transactional Outbox (ADR-007). Lee los eventos pendientes de <c>outbox_events</c> con el rol
/// del publicador (DD, sección 10.2), los publica en el topic con el nombre del evento y la clave del agregado, y
/// marca <c>published_at</c>. Si Kafka falla, suma un intento y reintenta en la siguiente vuelta: ningún evento se pierde.
/// </summary>
public sealed partial class OutboxPublisher(
    IServiceScopeFactory scopes,
    IProducer<string, string> producer,
    IOptions<OutboxOptions> options,
    TimeProvider clock,
    ILogger<OutboxPublisher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var published = await PublishPendingAsync(stoppingToken);
                if (published > 0)
                {
                    continue;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogPublishFailed(logger, ex);
            }

            await Task.Delay(options.Value.PollInterval, clock, stoppingToken);
        }
    }

    /// <summary>Publica un lote de eventos pendientes; devuelve cuántos quedaron publicados.</summary>
    public async Task<int> PublishPendingAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        await SessionRole.SetAsync(db, options.Value.PublisherRole, cancellationToken);

        var batch = await db.OutboxEvents
            .FromSql($"SELECT * FROM outbox_events WHERE published_at IS NULL ORDER BY created_at LIMIT {options.Value.BatchSize} FOR UPDATE SKIP LOCKED")
            .ToListAsync(cancellationToken);

        var published = 0;
        foreach (var record in batch)
        {
            try
            {
                await producer.ProduceAsync(record.EventType, ToMessage(record), cancellationToken);
                record.PublishedAt = clock.GetUtcNow();
                published++;
            }
            catch (ProduceException<string, string> ex)
            {
                record.Attempts++;
                LogProduceFailed(logger, record.Id, record.Attempts, ex);
                break;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return published;
    }

    private static Message<string, string> ToMessage(OutboxEventRecord record) => new()
    {
        Key = record.AggregateId.ToString(),
        Value = record.Payload,
        Headers = new Headers
        {
            { "eventId", Encoding.UTF8.GetBytes(record.Id.ToString()) },
            { "eventType", Encoding.UTF8.GetBytes(record.EventType) },
        },
    };

    [LoggerMessage(Level = LogLevel.Error, Message = "Falló la publicación del Outbox.")]
    private static partial void LogPublishFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se pudo publicar el evento {EventId} (intento {Attempt}).")]
    private static partial void LogProduceFailed(ILogger logger, Guid eventId, int attempt, Exception exception);
}
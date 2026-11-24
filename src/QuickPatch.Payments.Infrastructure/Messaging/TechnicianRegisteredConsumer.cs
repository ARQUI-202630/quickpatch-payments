using System.Text.Json;

using Confluent.Kafka;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using QuickPatch.Payments.Application.Events;
using QuickPatch.Payments.Application.Payments;
using QuickPatch.Transversal.Messaging;

namespace QuickPatch.Payments.Infrastructure.Messaging;

/// <summary>
/// Consumidor de <c>technician.registered</c> v1 (P-02, SCRUM-501): guarda de cada técnico su proveedor. Confirma el offset solo después de
/// aplicar el evento, descarta el mensaje que no cumple el contrato y reintenta los errores de lectura no fatales, igual
/// que los consumidores de ServiceRequest.
/// </summary>
public sealed partial class TechnicianRegisteredConsumer(
    IServiceScopeFactory scopes,
    IConsumer<string, string> consumer,
    ILogger<TechnicianRegisteredConsumer> logger)
    : KafkaConsumerService(consumer, [EventTypes.TechnicianRegistered], logger)
{
    protected override Task<bool> ProcessAsync(ConsumeResult<string, string> result, CancellationToken cancellationToken) =>
        TryProcessAsync(result.Message.Value, cancellationToken);

    /// <summary>Aplica un mensaje; devuelve false si hay que reintentarlo.</summary>
    public async Task<bool> TryProcessAsync(string value, CancellationToken cancellationToken)
    {
        EventEnvelope<TechnicianRegisteredData>? envelope;
        try
        {
            envelope = EventSerializer.Deserialize<EventEnvelope<TechnicianRegisteredData>>(value);
        }
        catch (JsonException ex)
        {
            LogInvalidMessage(logger, ex);
            return true;
        }

        if (envelope?.Data is not { } data || envelope.EventType != EventTypes.TechnicianRegistered || envelope.EventVersion != 1
            || envelope.EventId == Guid.Empty || envelope.TenantId == Guid.Empty || data.TechnicianId == Guid.Empty
            || data.ProviderId == Guid.Empty || data.UpdatedAt == default)
        {
            LogInvalidMessage(logger, null);
            return true;
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<TechnicianRegisteredHandler>();
            await handler.HandleAsync(envelope, cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogProcessingFailed(logger, envelope.EventId, ex);
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Mensaje de technician.registered que no cumple el contrato; se descarta.")]
    private static partial void LogInvalidMessage(ILogger logger, Exception? exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Falló el procesamiento del evento {EventId}; se reintentará.")]
    private static partial void LogProcessingFailed(ILogger logger, Guid eventId, Exception exception);
}
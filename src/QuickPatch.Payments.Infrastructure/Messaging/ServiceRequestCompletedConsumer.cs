using System.Text.Json;

using Confluent.Kafka;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using QuickPatch.Payments.Application.Events;
using QuickPatch.Payments.Application.Payments;
using QuickPatch.Transversal.Messaging;

namespace QuickPatch.Payments.Infrastructure.Messaging;

/// <summary>
/// Consumidor de <c>service-request.completed</c> v1 (RN-P4): crea el pago pendiente. Confirma el offset solo después de
/// aplicar el evento, descarta el mensaje que no cumple el contrato y reintenta los errores de lectura no fatales, igual
/// que los consumidores de ServiceRequest.
/// </summary>
public sealed partial class ServiceRequestCompletedConsumer(
    IServiceScopeFactory scopes,
    IConsumer<string, string> consumer,
    ILogger<ServiceRequestCompletedConsumer> logger)
    : KafkaConsumerService(consumer, [EventTypes.ServiceRequestCompleted], logger)
{
    protected override Task<bool> ProcessAsync(ConsumeResult<string, string> result, CancellationToken cancellationToken) =>
        TryProcessAsync(result.Message.Value, cancellationToken);

    /// <summary>Aplica un mensaje; devuelve false si hay que reintentarlo.</summary>
    public async Task<bool> TryProcessAsync(string value, CancellationToken cancellationToken)
    {
        EventEnvelope<ServiceRequestCompletedData>? envelope;
        try
        {
            envelope = EventSerializer.Deserialize<EventEnvelope<ServiceRequestCompletedData>>(value);
        }
        catch (JsonException ex)
        {
            LogInvalidMessage(logger, ex);
            return true;
        }

        if (envelope?.Data is not { } data || envelope.EventType != EventTypes.ServiceRequestCompleted || envelope.EventVersion != 1
            || envelope.EventId == Guid.Empty || envelope.TenantId == Guid.Empty || data.ServiceRequestId == Guid.Empty
            || data.ClientId == Guid.Empty || data.TechnicianId == Guid.Empty || string.IsNullOrWhiteSpace(data.TotalAmount))
        {
            LogInvalidMessage(logger, null);
            return true;
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<ServiceRequestCompletedHandler>();
            await handler.HandleAsync(envelope, cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogProcessingFailed(logger, envelope.EventId, ex);
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Mensaje de service-request.completed que no cumple el contrato; se descarta.")]
    private static partial void LogInvalidMessage(ILogger logger, Exception? exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Falló el procesamiento del evento {EventId}; se reintentará.")]
    private static partial void LogProcessingFailed(ILogger logger, Guid eventId, Exception exception);
}
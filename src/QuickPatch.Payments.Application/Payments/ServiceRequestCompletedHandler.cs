using System.Globalization;

using Microsoft.Extensions.Logging;

using QuickPatch.Payments.Application.Abstractions;
using QuickPatch.Payments.Application.Events;
using QuickPatch.Payments.Domain.Payments;

namespace QuickPatch.Payments.Application.Payments;

public enum CompletedHandlingOutcome
{
    /// <summary>Se creó el pago <c>pendiente</c>.</summary>
    Created,

    /// <summary>El evento ya se había aplicado (mismo <c>eventId</c>, RN-EV1).</summary>
    Duplicate,

    /// <summary>La solicitud ya tenía pago (por ejemplo, el evento se publicó dos veces con ids distintos).</summary>
    AlreadyExists,

    /// <summary>El monto no es un decimal positivo: el mensaje no cumple el contrato.</summary>
    Invalid,
}

/// <summary>
/// Al consumir <c>service-request.completed</c> crea el pago <c>pendiente</c> con el total de la cotización aceptada y el
/// técnico (RN-P3, RN-P4), bajo el tenant del evento (DD 10.3). Es idempotente por <c>eventId</c> y por solicitud.
/// </summary>
public sealed partial class ServiceRequestCompletedHandler(
    ITenantUnitOfWork unitOfWork,
    IPaymentRepository payments,
    IProcessedEventRepository processedEvents,
    TimeProvider clock,
    ILogger<ServiceRequestCompletedHandler> logger)
{
    public Task<CompletedHandlingOutcome> HandleAsync(EventEnvelope<ServiceRequestCompletedData> envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var data = envelope.Data;
        if (!decimal.TryParse(data.TotalAmount, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount) || amount <= 0)
        {
            LogInvalidAmount(logger, envelope.EventId);
            return Task.FromResult(CompletedHandlingOutcome.Invalid);
        }

        return unitOfWork.ExecuteAsync(
            envelope.TenantId,
            async ct =>
            {
                if (await processedEvents.ExistsAsync(envelope.EventId, ct))
                {
                    return CompletedHandlingOutcome.Duplicate;
                }

                var now = clock.GetUtcNow();
                processedEvents.Add(envelope.EventId, envelope.TenantId, envelope.EventType, now);
                if (await payments.ExistsForServiceRequestAsync(data.ServiceRequestId, ct))
                {
                    return CompletedHandlingOutcome.AlreadyExists;
                }

                payments.Add(Payment.CreatePending(
                    envelope.TenantId, data.ServiceRequestId, data.ClientId, data.CompanyId, data.TechnicianId, amount, now));
                LogCreated(logger, data.ServiceRequestId);
                return CompletedHandlingOutcome.Created;
            },
            cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "service-request.completed {EventId} trae un monto inválido; se descarta.")]
    private static partial void LogInvalidAmount(ILogger logger, Guid eventId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Pago pendiente creado para la solicitud {ServiceRequestId}.")]
    private static partial void LogCreated(ILogger logger, Guid serviceRequestId);
}
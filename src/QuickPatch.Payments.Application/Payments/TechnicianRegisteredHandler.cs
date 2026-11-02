using Microsoft.Extensions.Logging;

using QuickPatch.Payments.Application.Abstractions;
using QuickPatch.Payments.Application.Events;
using QuickPatch.Payments.Domain.Payments;

namespace QuickPatch.Payments.Application.Payments;

public enum TechnicianRegisteredOutcome
{
    /// <summary>La proyección se creó o se actualizó.</summary>
    Applied,

    /// <summary>El evento ya se había aplicado (mismo <c>eventId</c>, RN-EV1).</summary>
    Duplicate,

    /// <summary>El evento es más antiguo o igual que el estado guardado (ADR-017).</summary>
    Stale,
}

/// <summary>
/// Al consumir <c>technician.registered</c> guarda de cada técnico su proveedor (P-02, SCRUM-501), bajo el tenant del evento
/// (DD 10.3). Es idempotente por <c>eventId</c> y descarta una versión más vieja por <c>updatedAt</c> (ADR-017).
/// </summary>
public sealed partial class TechnicianRegisteredHandler(
    ITenantUnitOfWork unitOfWork,
    IPaymentTechnicianRepository technicians,
    IProcessedEventRepository processedEvents,
    TimeProvider clock,
    ILogger<TechnicianRegisteredHandler> logger)
{
    public Task<TechnicianRegisteredOutcome> HandleAsync(EventEnvelope<TechnicianRegisteredData> envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        return unitOfWork.ExecuteAsync(
            envelope.TenantId,
            async ct =>
            {
                if (await processedEvents.ExistsAsync(envelope.EventId, ct))
                {
                    LogDuplicate(logger, envelope.EventId);
                    return TechnicianRegisteredOutcome.Duplicate;
                }

                processedEvents.Add(envelope.EventId, envelope.TenantId, envelope.EventType, clock.GetUtcNow());

                var data = envelope.Data;
                var existing = await technicians.FindAsync(data.TechnicianId, ct);
                if (existing is null)
                {
                    technicians.Add(new PaymentTechnician(data.TechnicianId, envelope.TenantId, data.ProviderId, data.UpdatedAt));
                    return TechnicianRegisteredOutcome.Applied;
                }

                return existing.ApplyChange(data.ProviderId, data.UpdatedAt)
                    ? TechnicianRegisteredOutcome.Applied
                    : TechnicianRegisteredOutcome.Stale;
            },
            cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Evento {EventId} ya aplicado; se descarta.")]
    private static partial void LogDuplicate(ILogger logger, Guid eventId);
}
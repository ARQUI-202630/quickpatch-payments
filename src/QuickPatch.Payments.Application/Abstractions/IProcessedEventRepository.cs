using QuickPatch.Payments.Application.Invoices;
using QuickPatch.Payments.Domain.Payments;

namespace QuickPatch.Payments.Application.Abstractions;

/// <summary>Idempotencia de los consumidores (tabla <c>processed_events</c>, RN-EV1).</summary>
public interface IProcessedEventRepository
{
    Task<bool> ExistsAsync(Guid eventId, CancellationToken cancellationToken);

    void Add(Guid eventId, Guid tenantId, string eventType, DateTimeOffset processedAt);
}
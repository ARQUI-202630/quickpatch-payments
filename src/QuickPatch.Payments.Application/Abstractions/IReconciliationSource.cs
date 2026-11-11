using QuickPatch.Payments.Application.Invoices;
using QuickPatch.Payments.Domain.Payments;

namespace QuickPatch.Payments.Application.Abstractions;

/// <summary>Pagos que la conciliación debe cerrar. Lee todos los tenants con un rol propio (DD 10.2).</summary>
public interface IReconciliationSource
{
    Task<IReadOnlyList<PendingReconciliation>> ListAsync(DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>Pago en <c>procesando</c> que espera conciliación.</summary>
public sealed record PendingReconciliation(Guid TenantId, Guid PaymentId);
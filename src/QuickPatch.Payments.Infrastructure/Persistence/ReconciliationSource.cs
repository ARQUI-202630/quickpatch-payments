using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Npgsql;

using QuickPatch.Payments.Application.Abstractions;
using QuickPatch.Payments.Domain.Payments;
using QuickPatch.Payments.Infrastructure.Options;

namespace QuickPatch.Payments.Infrastructure.Persistence;

/// <summary>
/// Pagos <c>procesando</c> que la conciliación debe cerrar, de todos los tenants (DD 10.2): adopta el rol
/// <c>payments_scheduler</c> (BYPASSRLS, solo lectura de <c>payments</c>) dentro de una transacción de solo lectura. Toma
/// los que llevan más de <see cref="ReconciliationOptions.MinimumAge"/> procesándose y tienen la referencia de la pasarela, o
/// más de <see cref="ReconciliationOptions.OrphanAge"/> sin ella (el servicio se cayó durante el cobro).
/// </summary>
public sealed class ReconciliationSource(PaymentsDbContext db, IOptions<ReconciliationOptions> options) : IReconciliationSource
{
    public async Task<IReadOnlyList<PendingReconciliation>> ListAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await SessionRole.SetAsync(db, settings.SchedulerRole, cancellationToken);
        var minimum = now - settings.MinimumAge;
        var orphan = now - settings.OrphanAge;
        var rows = await db.Payments.AsNoTracking()
            .Where(p => p.Status == PaymentStatus.Processing && p.ProcessingStartedAt != null
                && ((p.ProviderTokenRef != null && p.ProcessingStartedAt < minimum) || p.ProcessingStartedAt < orphan))
            .OrderBy(p => p.ProcessingStartedAt)
            .Take(settings.BatchSize)
            .Select(p => new PendingReconciliation(p.TenantId, p.Id))
            .ToListAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return rows;
    }
}
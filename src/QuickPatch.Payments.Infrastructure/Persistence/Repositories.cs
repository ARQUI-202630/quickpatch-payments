using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Npgsql;

using QuickPatch.Payments.Application.Abstractions;
using QuickPatch.Payments.Domain.Payments;
using QuickPatch.Payments.Infrastructure.Options;

namespace QuickPatch.Payments.Infrastructure.Persistence;

/// <summary>
/// Transacción por tenant (DD 10.2): fija <c>app.current_tenant</c> con <c>set_config(..., true)</c>,
/// equivalente a <c>SET LOCAL</c>, antes de ejecutar el trabajo; RLS filtra todo lo que se lea o escriba.
/// </summary>
public sealed class TenantUnitOfWork(PaymentsDbContext db) : ITenantUnitOfWork
{
    public async Task<T> ExecuteAsync<T>(Guid tenantId, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("El tenant es obligatorio.", nameof(tenantId));
        }

        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var tenant = tenantId.ToString();
        await db.Database.ExecuteSqlAsync($"SELECT set_config('app.current_tenant', {tenant}, true)", cancellationToken);

        var result = await work(cancellationToken);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505", ConstraintName: PaymentsDbContext.OpenPaymentIndex })
        {
            throw new OpenPaymentConflictException("La solicitud ya tiene un pago abierto.", ex);
        }

        await transaction.CommitAsync(cancellationToken);
        return result;
    }
}

public sealed class PaymentRepository(PaymentsDbContext db) : IPaymentRepository
{
    public Task<Payment?> FindLatestForUpdateAsync(Guid serviceRequestId, CancellationToken cancellationToken) =>
        db.Payments
            .FromSql($"SELECT * FROM payments WHERE service_request_id = {serviceRequestId} ORDER BY created_at DESC, id DESC LIMIT 1 FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

    public Task<Payment?> FindLatestAsync(Guid serviceRequestId, CancellationToken cancellationToken) =>
        db.Payments.AsNoTracking()
            .Where(x => x.ServiceRequestId == serviceRequestId)
            .OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<Payment?> FindForUpdateAsync(Guid paymentId, CancellationToken cancellationToken) =>
        db.Payments.FromSql($"SELECT * FROM payments WHERE id = {paymentId} FOR UPDATE").SingleOrDefaultAsync(cancellationToken);

    public Task<bool> ExistsForServiceRequestAsync(Guid serviceRequestId, CancellationToken cancellationToken) =>
        db.Payments.AnyAsync(x => x.ServiceRequestId == serviceRequestId, cancellationToken);

    public Task<Guid?> FindInvoiceIdAsync(Guid paymentId, CancellationToken cancellationToken) =>
        db.Invoices.Where(x => x.PaymentId == paymentId).Select(x => (Guid?)x.Id).SingleOrDefaultAsync(cancellationToken);

    public void Add(Payment payment) => db.Payments.Add(payment);

    public void AddInvoice(Invoice invoice) => db.Invoices.Add(invoice);
}

public sealed class ProcessedEventRepository(PaymentsDbContext db) : IProcessedEventRepository
{
    public Task<bool> ExistsAsync(Guid eventId, CancellationToken cancellationToken) =>
        db.ProcessedEvents.AnyAsync(x => x.EventId == eventId, cancellationToken);

    public void Add(Guid eventId, Guid tenantId, string eventType, DateTimeOffset processedAt) =>
        db.ProcessedEvents.Add(new ProcessedEventRecord { EventId = eventId, TenantId = tenantId, EventType = eventType, ProcessedAt = processedAt });
}

public sealed class EfOutbox(PaymentsDbContext db, TimeProvider clock) : IOutbox
{
    public void Enqueue(OutboxMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        db.OutboxEvents.Add(new OutboxEventRecord
        {
            Id = message.Id,
            TenantId = message.TenantId,
            AggregateId = message.AggregateId,
            EventType = message.EventType,
            Payload = message.Payload,
            CreatedAt = clock.GetUtcNow(),
            Attempts = 0,
        });
    }
}

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
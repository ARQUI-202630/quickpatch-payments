using Microsoft.EntityFrameworkCore;

using Npgsql;

using QuickPatch.Payments.Application.Abstractions;
using QuickPatch.Payments.Domain.Payments;
using QuickPatch.Transversal.Persistence;

namespace QuickPatch.Payments.Infrastructure.Persistence;

/// <summary>
/// Transacción por tenant (DD 10.2) con la implementación común de ADR-020. Antes limpia el seguimiento de cambios (un
/// reintento del consumidor no arrastra entidades del intento que falló) y traduce el índice de pago abierto a su
/// excepción de dominio.
/// </summary>
public sealed class TenantUnitOfWork(PaymentsDbContext db) : ITenantUnitOfWork
{
    public async Task<T> ExecuteAsync<T>(Guid tenantId, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        try
        {
            return await TenantTransaction.ExecuteAsync(db, tenantId, work, cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505", ConstraintName: PaymentsDbContext.OpenPaymentIndex })
        {
            throw new OpenPaymentConflictException("La solicitud ya tiene un pago abierto.", ex);
        }
    }
}
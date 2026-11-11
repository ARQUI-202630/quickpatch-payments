using QuickPatch.Payments.Application.Invoices;
using QuickPatch.Payments.Domain.Payments;

namespace QuickPatch.Payments.Application.Abstractions;

/// <summary>
/// Ejecuta un trabajo en una transacción que primero fija el tenant de la sesión (<c>SET LOCAL app.current_tenant</c>,
/// DD 10.2) para que RLS aísle los datos. Al terminar guarda los cambios y confirma.
/// </summary>
public interface ITenantUnitOfWork
{
    /// <exception cref="OpenPaymentConflictException">Otra petición ya abrió un pago para la solicitud (RN-P5).</exception>
    Task<T> ExecuteAsync<T>(Guid tenantId, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken);
}
using QuickPatch.Payments.Application.Invoices;
using QuickPatch.Payments.Domain.Payments;

namespace QuickPatch.Payments.Application.Abstractions;

/// <summary>Persistencia de pagos y comprobantes (tablas <c>payments</c> e <c>invoices</c>).</summary>
public interface IPaymentRepository
{
    /// <summary>El pago más reciente de la solicitud, bloqueado (<c>FOR UPDATE</c>) hasta el fin de la transacción.</summary>
    Task<Payment?> FindLatestForUpdateAsync(Guid serviceRequestId, CancellationToken cancellationToken);

    Task<Payment?> FindLatestAsync(Guid serviceRequestId, CancellationToken cancellationToken);

    /// <summary>El pago por id, bloqueado hasta el fin de la transacción.</summary>
    Task<Payment?> FindForUpdateAsync(Guid paymentId, CancellationToken cancellationToken);

    Task<bool> ExistsForServiceRequestAsync(Guid serviceRequestId, CancellationToken cancellationToken);

    Task<Guid?> FindInvoiceIdAsync(Guid paymentId, CancellationToken cancellationToken);

    /// <summary>El comprobante del pago con su pago, o <c>null</c> si el pago no tiene comprobante o no es del tenant.</summary>
    Task<(Invoice Invoice, Payment Payment)?> FindInvoiceAsync(Guid paymentId, CancellationToken cancellationToken);

    /// <summary>
    /// Pagos vigentes del técnico, sin los rechazados (RN-P4), del más reciente al más antiguo, con el total para paginar
    /// (contrato payments 1.2.0, RF-25). RLS deja solo los del tenant de la transacción.
    /// </summary>
    Task<(IReadOnlyList<Payment> Items, int TotalItems)> ListReceivedAsync(
        Guid technicianId, string? status, int page, int size, CancellationToken cancellationToken);

    /// <summary>
    /// Pagos vigentes de los técnicos del equipo del proveedor (proyección <c>payment_technicians</c>, P-02), con el mismo
    /// orden y filtros; <paramref name="technicianId"/> acota a un técnico y, si no es del equipo, la lista sale vacía
    /// (contrato payments 1.2.0).
    /// </summary>
    Task<(IReadOnlyList<Payment> Items, int TotalItems)> ListReceivedForProviderAsync(
        Guid providerId, Guid? technicianId, string? status, int page, int size, CancellationToken cancellationToken);

    void Add(Payment payment);

    void AddInvoice(Invoice invoice);
}
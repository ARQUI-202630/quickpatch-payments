using QuickPatch.Payments.Application.Abstractions;
using QuickPatch.Payments.Domain.Payments;

namespace QuickPatch.Payments.Application.Invoices;

/// <summary>
/// <c>GET /v1/payments/{id}/invoice</c> (DD 8.3.16, RF-24). Bajo el tenant del token (RLS): el pagador ve el comprobante
/// de su pago y el <c>admin_tenant</c>, cualquiera del tenant. Para los demás el comprobante no existe.
/// </summary>
public sealed class GetInvoiceHandler(ITenantUnitOfWork unitOfWork, IPaymentRepository payments)
{
    public Task<InvoiceDocument?> HandleAsync(
        Guid tenantId, Guid userId, bool isTenantAdmin, Guid paymentId, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync<InvoiceDocument?>(
            tenantId,
            async ct =>
            {
                var found = await payments.FindInvoiceAsync(paymentId, ct);
                if (found is not var (invoice, payment) || (!isTenantAdmin && !payment.CanReadInvoice(userId)))
                {
                    return null;
                }

                // El pago se aprueba siempre después de guardar los datos de facturación (StartProcessing).
                var payer = Billing.FromJson(payment.Billing)
                    ?? throw new InvalidOperationException($"El pago aprobado {payment.Id} no tiene datos de facturación.");
                return new InvoiceDocument(invoice, payment, payer);
            },
            cancellationToken);
}
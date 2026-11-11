using Microsoft.Extensions.Logging;

using QuickPatch.Payments.Application.Abstractions;
using QuickPatch.Payments.Application.Events;
using QuickPatch.Payments.Domain.Payments;

namespace QuickPatch.Payments.Application.Payments;

/// <summary>
/// Aplica la respuesta de la pasarela a un pago <c>procesando</c>, en su propia transacción y con el pago bloqueado. Lo
/// usan el pago y la conciliación; si el pago ya no está <c>procesando</c> (otro proceso lo cerró), no cambia nada.
/// </summary>
public sealed partial class PaymentSettlement(
    ITenantUnitOfWork unitOfWork,
    IPaymentRepository payments,
    IOutbox outbox,
    TimeProvider clock,
    ILogger<PaymentSettlement> logger)
{
    /// <returns>El pago tras aplicar el resultado, o null si no existe.</returns>
    public Task<PaymentView?> ApplyAsync(
        Guid tenantId, Guid paymentId, ProviderResult result, Guid correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        return unitOfWork.ExecuteAsync<PaymentView?>(
            tenantId,
            async ct =>
            {
                var payment = await payments.FindForUpdateAsync(paymentId, ct);
                if (payment is null)
                {
                    return null;
                }

                var now = clock.GetUtcNow();
                switch (result)
                {
                    case ProviderResult.Approved approved when payment.Approve(approved.Reference, now):
                        var billing = Billing.FromJson(payment.Billing)
                            ?? throw new InvalidOperationException($"El pago {payment.Id} no tiene datos de facturación.");
                        var invoice = Invoice.Issue(payment, billing, now);
                        payments.AddInvoice(invoice);
                        outbox.Enqueue(PaymentEvents.Approved(payment, invoice, correlationId, now));
                        LogSettled(logger, payment.Id, payment.Status);
                        return new PaymentView(payment, invoice.Id);
                    case ProviderResult.Rejected rejected when payment.Reject(rejected.Reference, rejected.ReasonCode, now):
                        outbox.Enqueue(PaymentEvents.Rejected(payment, correlationId, now));
                        LogSettled(logger, payment.Id, payment.Status);
                        return new PaymentView(payment, null);
                    case ProviderResult.Unavailable unavailable:
                        payment.RecordProviderReference(unavailable.Reference);
                        return new PaymentView(payment, null);
                    default:
                        return new PaymentView(payment, await payments.FindInvoiceIdAsync(payment.Id, ct));
                }
            },
            cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Pago {PaymentId} cerrado como {Status}.")]
    private static partial void LogSettled(ILogger logger, Guid paymentId, string status);
}
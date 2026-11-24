using Microsoft.Extensions.Logging;

using QuickPatch.Payments.Application.Abstractions;
using QuickPatch.Payments.Application.Events;
using QuickPatch.Payments.Domain.Payments;

namespace QuickPatch.Payments.Application.Payments;

/// <summary>Pago con el id de su comprobante, si lo tiene (esquema <c>Payment</c> del contrato).</summary>
public sealed record PaymentView(Payment Payment, Guid? InvoiceId);

public abstract record GetPaymentResult
{
    public sealed record Found(PaymentView View) : GetPaymentResult;

    /// <summary>La solicitud no existe para el usuario (otro cliente u otro tenant).</summary>
    public sealed record NotFound : GetPaymentResult;

    /// <summary>Payments aún no recibió <c>service-request.completed</c> (RN-P4).</summary>
    public sealed record NotReady : GetPaymentResult;
}

/// <summary><c>GET /v1/service-requests/{id}/payment</c>: el pago más reciente de la solicitud.</summary>
public sealed class GetPaymentHandler(ITenantUnitOfWork unitOfWork, IPaymentRepository payments)
{
    public Task<GetPaymentResult> HandleAsync(
        Guid tenantId, Guid userId, Guid? companyId, Guid serviceRequestId, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync<GetPaymentResult>(
            tenantId,
            async ct =>
            {
                var payment = await payments.FindLatestAsync(serviceRequestId, ct);
                if (payment is null)
                {
                    return new GetPaymentResult.NotReady();
                }

                if (!payment.CanBePaidBy(userId, companyId))
                {
                    return new GetPaymentResult.NotFound();
                }

                return new GetPaymentResult.Found(new PaymentView(payment, await payments.FindInvoiceIdAsync(payment.Id, ct)));
            },
            cancellationToken);
}
using QuickPatch.Payments.Application.Abstractions;
using QuickPatch.Payments.Domain.Payments;

namespace QuickPatch.Payments.Application.Payments;

/// <summary>Página de pagos recibidos (esquema <c>ReceivedPaymentPage</c> del contrato payments 1.2.0).</summary>
public sealed record ReceivedPaymentsPage(IReadOnlyList<Payment> Items, int Page, int Size, int TotalItems);

/// <summary>
/// <c>GET /v1/payments/received</c> para el técnico (RF-25, SCRUM-45): los pagos de los servicios que prestó, bajo el
/// tenant del token. El técnico es el usuario del token, nunca un parámetro (RN-P1: sin datos de tarjeta en la respuesta).
/// </summary>
public sealed class ReceivedPaymentsHandler(ITenantUnitOfWork unitOfWork, IPaymentRepository payments)
{
    /// <summary>Estados que lista el contrato; los <c>rechazado</c> no aparecen porque el reintento crea otro pago (RN-P4).</summary>
    public static readonly IReadOnlySet<string> ListedStatuses =
        new HashSet<string>(StringComparer.Ordinal) { PaymentStatus.Pending, PaymentStatus.Processing, PaymentStatus.Approved };

    public const int MaxSize = 100;

    public Task<ReceivedPaymentsPage> HandleForTechnicianAsync(
        Guid tenantId, Guid technicianId, string? status, int page, int size, CancellationToken cancellationToken)
    {
        if (status is not null && !ListedStatuses.Contains(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Estado fuera del contrato.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(size, MaxSize);

        return unitOfWork.ExecuteAsync(
            tenantId,
            async ct =>
            {
                var (items, total) = await payments.ListReceivedAsync(technicianId, status, page, size, ct);
                return new ReceivedPaymentsPage(items, page, size, total);
            },
            cancellationToken);
    }
}
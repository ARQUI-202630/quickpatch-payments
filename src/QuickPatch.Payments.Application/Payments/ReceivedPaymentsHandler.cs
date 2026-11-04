using QuickPatch.Payments.Application.Abstractions;
using QuickPatch.Payments.Domain.Payments;

namespace QuickPatch.Payments.Application.Payments;

/// <summary>Página de pagos recibidos (esquema <c>ReceivedPaymentPage</c> del contrato payments 1.2.0).</summary>
public sealed record ReceivedPaymentsPage(IReadOnlyList<Payment> Items, int Page, int Size, int TotalItems);

/// <summary>
/// <c>GET /v1/payments/received</c> (RF-25, SCRUM-45), bajo el tenant del token. El técnico ve los pagos de los servicios que
/// prestó y el proveedor los de su equipo; en los dos casos la persona es el usuario del token, nunca un parámetro, y solo el
/// proveedor puede acotar a un técnico (RN-P1: sin datos de tarjeta en la respuesta).
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
        Validate(status, page, size);

        return unitOfWork.ExecuteAsync(
            tenantId,
            async ct =>
            {
                var (items, total) = await payments.ListReceivedAsync(technicianId, status, page, size, ct);
                return new ReceivedPaymentsPage(items, page, size, total);
            },
            cancellationToken);
    }

    /// <summary>
    /// Los pagos de los técnicos del equipo del proveedor (proyección <c>payment_technicians</c>, P-02). Con
    /// <paramref name="technicianId"/> solo los de ese técnico; si no es de su equipo, la lista sale vacía.
    /// </summary>
    public Task<ReceivedPaymentsPage> HandleForProviderAsync(
        Guid tenantId, Guid providerId, Guid? technicianId, string? status, int page, int size, CancellationToken cancellationToken)
    {
        Validate(status, page, size);

        return unitOfWork.ExecuteAsync(
            tenantId,
            async ct =>
            {
                var (items, total) = await payments.ListReceivedForProviderAsync(providerId, technicianId, status, page, size, ct);
                return new ReceivedPaymentsPage(items, page, size, total);
            },
            cancellationToken);
    }

    private static void Validate(string? status, int page, int size)
    {
        if (status is not null && !ListedStatuses.Contains(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Estado fuera del contrato.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(size, MaxSize);
    }
}
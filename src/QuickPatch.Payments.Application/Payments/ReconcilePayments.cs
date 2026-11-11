using Microsoft.Extensions.Logging;

using QuickPatch.Payments.Application.Abstractions;
using QuickPatch.Payments.Application.Events;
using QuickPatch.Payments.Domain.Payments;

namespace QuickPatch.Payments.Application.Payments;

/// <summary>
/// Conciliación de pagos que quedaron <c>procesando</c> porque la pasarela no respondió (AC9-E5): consulta el estado del
/// cobro y lo cierra. Cada pago se cierra bajo su tenant; la lista sale de <see cref="IReconciliationSource"/>.
/// </summary>
public sealed partial class ReconcilePaymentsHandler(
    IReconciliationSource queue,
    ITenantUnitOfWork unitOfWork,
    IPaymentRepository payments,
    IPaymentProvider provider,
    PaymentSettlement settlement,
    TimeProvider clock,
    ILogger<ReconcilePaymentsHandler> logger)
{
    /// <returns>Cuántos pagos quedaron cerrados.</returns>
    public async Task<int> HandleAsync(CancellationToken cancellationToken)
    {
        var closed = 0;
        foreach (var pending in await queue.ListAsync(clock.GetUtcNow(), cancellationToken))
        {
            var reference = await unitOfWork.ExecuteAsync(
                pending.TenantId,
                async ct => (await payments.FindForUpdateAsync(pending.PaymentId, ct))?.ProviderTokenRef,
                cancellationToken);
            ProviderResult result;
            try
            {
                result = await provider.QueryAsync(pending.PaymentId, reference, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogQueryFailed(logger, pending.PaymentId, ex);
                continue;
            }

            if (result is ProviderResult.Unavailable)
            {
                continue;
            }

            var view = await settlement.ApplyAsync(pending.TenantId, pending.PaymentId, result, Guid.NewGuid(), cancellationToken);
            if (view?.Payment.Status is PaymentStatus.Approved or PaymentStatus.Rejected)
            {
                closed++;
            }
        }

        return closed;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se pudo consultar a la pasarela el pago {PaymentId}; se reintentará.")]
    private static partial void LogQueryFailed(ILogger logger, Guid paymentId, Exception exception);
}
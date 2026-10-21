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
    public Task<GetPaymentResult> HandleAsync(Guid tenantId, Guid userId, Guid serviceRequestId, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync<GetPaymentResult>(
            tenantId,
            async ct =>
            {
                var payment = await payments.FindLatestAsync(serviceRequestId, ct);
                if (payment is null)
                {
                    return new GetPaymentResult.NotReady();
                }

                if (!payment.CanBePaidBy(userId))
                {
                    return new GetPaymentResult.NotFound();
                }

                return new GetPaymentResult.Found(new PaymentView(payment, await payments.FindInvoiceIdAsync(payment.Id, ct)));
            },
            cancellationToken);
}

public sealed record PayCommand(Guid TenantId, Guid UserId, Guid CorrelationId, Guid ServiceRequestId, string PaymentToken, Billing Billing);

public abstract record PayResult
{
    /// <summary>La pasarela respondió: el pago quedó <c>aprobado</c> o <c>rechazado</c>.</summary>
    public sealed record Completed(PaymentView View) : PayResult;

    public sealed record NotFound : PayResult;

    public sealed record NotReady : PayResult;

    /// <summary>Ya hay un pago <c>procesando</c> (RN-P5).</summary>
    public sealed record InProgress : PayResult;

    /// <summary>La solicitud ya tiene un pago aprobado (RN-P2).</summary>
    public sealed record AlreadyPaid : PayResult;

    /// <summary>La pasarela no respondió: el pago queda <c>procesando</c> y lo cierra la conciliación (AC9-E5).</summary>
    public sealed record ProviderUnavailable : PayResult;
}

/// <summary>
/// <c>POST /v1/service-requests/{id}/payment</c> (DD 8.3.12). Tres pasos, para no tener una transacción abierta mientras
/// se espera a la pasarela:
/// <list type="number">
/// <item>Bajo el tenant del token y con el pago bloqueado: valida quién paga y el estado, y pasa el pago a
/// <c>procesando</c> (RN-P5). Tras un rechazo, abre un pago nuevo (RN-P4); el índice único de pagos abiertos impide que
/// dos peticiones lo hagan a la vez.</item>
/// <item>Fuera de la transacción: cobra con el token (nunca con la tarjeta, RN-P1).</item>
/// <item>En otra transacción: guarda el resultado; si es aprobado emite el comprobante, y publica
/// <c>payment.approved</c> o <c>payment.rejected</c> por Outbox (ADR-007).</item>
/// </list>
/// </summary>
public sealed partial class PayHandler(
    ITenantUnitOfWork unitOfWork,
    IPaymentRepository payments,
    IPaymentProvider provider,
    PaymentSettlement settlement,
    TimeProvider clock,
    ILogger<PayHandler> logger)
{
    public async Task<PayResult> HandleAsync(PayCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        Payment? processing;
        try
        {
            var start = await unitOfWork.ExecuteAsync<(PayResult? Result, Payment? Payment)>(
                command.TenantId, ct => StartAsync(command, ct), cancellationToken);
            if (start.Result is not null)
            {
                return start.Result;
            }

            processing = start.Payment!;
        }
        catch (OpenPaymentConflictException)
        {
            return new PayResult.InProgress();
        }

        ProviderResult result;
        try
        {
            result = await provider.ChargeAsync(processing.Id, command.PaymentToken, processing.Amount, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogProviderFailed(logger, processing.Id, ex);
            result = new ProviderResult.Unavailable(null);
        }

        var view = await settlement.ApplyAsync(command.TenantId, processing.Id, result, command.CorrelationId, cancellationToken);
        return result is ProviderResult.Unavailable || view is null
            ? new PayResult.ProviderUnavailable()
            : new PayResult.Completed(view);
    }

    private async Task<(PayResult? Result, Payment? Payment)> StartAsync(PayCommand command, CancellationToken ct)
    {
        var latest = await payments.FindLatestForUpdateAsync(command.ServiceRequestId, ct);
        if (latest is null)
        {
            return (new PayResult.NotReady(), null);
        }

        if (!latest.CanBePaidBy(command.UserId))
        {
            return (new PayResult.NotFound(), null);
        }

        var now = clock.GetUtcNow();
        switch (latest.Status)
        {
            case PaymentStatus.Approved:
                return (new PayResult.AlreadyPaid(), null);
            case PaymentStatus.Processing:
                return (new PayResult.InProgress(), null);
            case PaymentStatus.Rejected:
                var retry = latest.Retry(now);
                retry.StartProcessing(command.UserId, command.Billing.ToJson(), now);
                payments.Add(retry);
                return (null, retry);
            default:
                latest.StartProcessing(command.UserId, command.Billing.ToJson(), now);
                return (null, latest);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "La pasarela falló al cobrar el pago {PaymentId}; queda para conciliación.")]
    private static partial void LogProviderFailed(ILogger logger, Guid paymentId, Exception exception);
}

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
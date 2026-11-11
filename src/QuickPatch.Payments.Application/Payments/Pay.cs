using Microsoft.Extensions.Logging;

using QuickPatch.Payments.Application.Abstractions;
using QuickPatch.Payments.Application.Events;
using QuickPatch.Payments.Domain.Payments;

namespace QuickPatch.Payments.Application.Payments;

/// <summary><paramref name="CompanyId"/>: empresa del token del <c>empresa_contacto</c> (RN-P6); nula para el cliente.</summary>
public sealed record PayCommand(
    Guid TenantId, Guid UserId, Guid? CompanyId, Guid CorrelationId, Guid ServiceRequestId, string PaymentToken, Billing Billing);

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

        if (!latest.CanBePaidBy(command.UserId, command.CompanyId))
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
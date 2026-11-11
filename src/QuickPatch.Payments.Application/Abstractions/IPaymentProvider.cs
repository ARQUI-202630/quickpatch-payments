using QuickPatch.Payments.Application.Invoices;
using QuickPatch.Payments.Domain.Payments;

namespace QuickPatch.Payments.Application.Abstractions;

/// <summary>
/// Puerto hacia la pasarela de pagos (ADR-009). En el MVP lo implementa la pasarela simulada; una pasarela certificada
/// implementaría el mismo puerto sin cambiar los contratos.
/// </summary>
public interface IPaymentProvider
{
    /// <summary>Cobra con el token del dispositivo. <paramref name="paymentId"/> es la referencia idempotente del cobro.</summary>
    Task<ProviderResult> ChargeAsync(Guid paymentId, string paymentToken, decimal amount, CancellationToken cancellationToken);

    /// <summary>Estado de un cobro que quedó sin respuesta (conciliación, AC9-E5).</summary>
    Task<ProviderResult> QueryAsync(Guid paymentId, string? providerReference, CancellationToken cancellationToken);
}

/// <summary>Respuesta de la pasarela.</summary>
public abstract record ProviderResult(string? Reference)
{
    public sealed record Approved(string Reference) : ProviderResult(Reference);

    public sealed record Rejected(string? Reference, string ReasonCode) : ProviderResult(Reference);

    /// <summary>La pasarela no respondió o respondió con error técnico: el resultado se conoce después.</summary>
    public sealed record Unavailable(string? Reference) : ProviderResult(Reference);
}
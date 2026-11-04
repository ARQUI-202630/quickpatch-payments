using QuickPatch.Payments.Application.Abstractions;
using QuickPatch.Payments.Domain.Payments;

namespace QuickPatch.Payments.Infrastructure.Providers;

/// <summary>
/// Pasarela simulada del MVP (ADR-009, modificación del 20 de octubre de 2026). Hace las veces de la API de cobro de una
/// pasarela: decide por el token que generó el tokenizador de la app con una de las tres tarjetas de prueba y devuelve una
/// referencia de cobro. No usa la red ni recibe datos de tarjeta (RN-P1).
/// <list type="bullet">
/// <item><c>tok_sim_aprobada_…</c> → aprobado.</item>
/// <item><c>tok_sim_rechazada_…</c> → rechazado por <c>fondos_insuficientes</c>.</item>
/// <item><c>tok_sim_error_…</c> → sin respuesta; al consultarlo en la conciliación, rechazado por
/// <c>pasarela_sin_respuesta</c>.</item>
/// <item>Cualquier otro token no lo emitió esta pasarela: rechazado.</item>
/// </list>
/// </summary>
public sealed class SimulatedPaymentProvider : IPaymentProvider
{
    public const string ApprovedPrefix = "tok_sim_aprobada_";
    public const string RejectedPrefix = "tok_sim_rechazada_";
    public const string ErrorPrefix = "tok_sim_error_";
    public const string UnknownTokenReason = "token_invalido";

    public Task<ProviderResult> ChargeAsync(Guid paymentId, string paymentToken, decimal amount, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paymentToken);
        var reference = $"sim_txn_{paymentId:N}";
        ProviderResult result = paymentToken switch
        {
            _ when paymentToken.StartsWith(ApprovedPrefix, StringComparison.Ordinal) => new ProviderResult.Approved(reference),
            _ when paymentToken.StartsWith(RejectedPrefix, StringComparison.Ordinal) =>
                new ProviderResult.Rejected(reference, RejectionReasons.InsufficientFunds),
            _ when paymentToken.StartsWith(ErrorPrefix, StringComparison.Ordinal) => new ProviderResult.Unavailable(reference),
            _ => new ProviderResult.Rejected(reference, UnknownTokenReason),
        };
        return Task.FromResult(result);
    }

    /// <summary>Un cobro que quedó sin respuesta nunca llegó a aprobarse: se cierra como rechazado.</summary>
    public Task<ProviderResult> QueryAsync(Guid paymentId, string? providerReference, CancellationToken cancellationToken) =>
        Task.FromResult<ProviderResult>(new ProviderResult.Rejected(providerReference, RejectionReasons.ProviderUnavailable));
}
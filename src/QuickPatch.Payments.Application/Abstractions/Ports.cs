using QuickPatch.Payments.Domain.Payments;

namespace QuickPatch.Payments.Application.Abstractions;

/// <summary>
/// Ejecuta un trabajo en una transacción que primero fija el tenant de la sesión (<c>SET LOCAL app.current_tenant</c>,
/// DD 10.2) para que RLS aísle los datos. Al terminar guarda los cambios y confirma.
/// </summary>
public interface ITenantUnitOfWork
{
    /// <exception cref="OpenPaymentConflictException">Otra petición ya abrió un pago para la solicitud (RN-P5).</exception>
    Task<T> ExecuteAsync<T>(Guid tenantId, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken);
}

/// <summary>Persistencia de pagos y comprobantes (tablas <c>payments</c> e <c>invoices</c>).</summary>
public interface IPaymentRepository
{
    /// <summary>El pago más reciente de la solicitud, bloqueado (<c>FOR UPDATE</c>) hasta el fin de la transacción.</summary>
    Task<Payment?> FindLatestForUpdateAsync(Guid serviceRequestId, CancellationToken cancellationToken);

    Task<Payment?> FindLatestAsync(Guid serviceRequestId, CancellationToken cancellationToken);

    /// <summary>El pago por id, bloqueado hasta el fin de la transacción.</summary>
    Task<Payment?> FindForUpdateAsync(Guid paymentId, CancellationToken cancellationToken);

    Task<bool> ExistsForServiceRequestAsync(Guid serviceRequestId, CancellationToken cancellationToken);

    Task<Guid?> FindInvoiceIdAsync(Guid paymentId, CancellationToken cancellationToken);

    void Add(Payment payment);

    void AddInvoice(Invoice invoice);
}

/// <summary>Pagos que la conciliación debe cerrar. Lee todos los tenants con un rol propio (DD 10.2).</summary>
public interface IReconciliationSource
{
    Task<IReadOnlyList<PendingReconciliation>> ListAsync(DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>Pago en <c>procesando</c> que espera conciliación.</summary>
public sealed record PendingReconciliation(Guid TenantId, Guid PaymentId);

/// <summary>Idempotencia de los consumidores (tabla <c>processed_events</c>, RN-EV1).</summary>
public interface IProcessedEventRepository
{
    Task<bool> ExistsAsync(Guid eventId, CancellationToken cancellationToken);

    void Add(Guid eventId, Guid tenantId, string eventType, DateTimeOffset processedAt);
}

/// <summary>Transactional Outbox (ADR-007): el evento se guarda en la misma transacción que el cambio.</summary>
public interface IOutbox
{
    void Enqueue(OutboxMessage message);
}

/// <summary>Mensaje pendiente de publicar en Kafka. <see cref="Id"/> se publica como <c>eventId</c>.</summary>
public sealed record OutboxMessage(Guid Id, Guid TenantId, Guid AggregateId, string EventType, string Payload);

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

/// <summary>El índice único de pagos abiertos rechazó un segundo pago <c>pendiente</c> o <c>procesando</c> (RN-P5).</summary>
public sealed class OpenPaymentConflictException : Exception
{
    public OpenPaymentConflictException()
        : base("La solicitud ya tiene un pago abierto.")
    {
    }

    public OpenPaymentConflictException(string message)
        : base(message)
    {
    }

    public OpenPaymentConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
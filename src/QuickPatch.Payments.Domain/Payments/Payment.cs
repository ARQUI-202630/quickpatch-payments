using System.Globalization;

namespace QuickPatch.Payments.Domain.Payments;

/// <summary>Estados del pago (DD 5.12).</summary>
public static class PaymentStatus
{
    public const string Pending = "pendiente";
    public const string Processing = "procesando";
    public const string Approved = "aprobado";
    public const string Rejected = "rechazado";
}

/// <summary>Motivos de rechazo que devuelve la pasarela (DD 5.12, <c>reason_code</c>).</summary>
public static class RejectionReasons
{
    public const string InsufficientFunds = "fondos_insuficientes";
    public const string ProviderUnavailable = "pasarela_sin_respuesta";
}

/// <summary>
/// Pago de una solicitud completada (tabla <c>payments</c>, DD 5.12). Nace <c>pendiente</c> con el total de la cotización
/// aceptada (RN-P3, RN-P4); una sola petición lo pasa a <c>procesando</c> (RN-P5); termina <c>aprobado</c> o
/// <c>rechazado</c>. Un rechazo no se reabre: el reintento es un pago nuevo con el mismo monto y técnico (RN-P4).
/// Nunca guarda datos de la tarjeta (RN-P1): solo la referencia que devuelve la pasarela.
/// </summary>
public sealed class Payment
{
    public const string PayerClient = "cliente";
    public const string PayerCompany = "empresa";

    private Payment()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ServiceRequestId { get; private set; }

    public Guid TechnicianId { get; private set; }

    public Guid ClientId { get; private set; }

    public Guid? CompanyId { get; private set; }

    public string PayerType { get; private set; } = PayerClient;

    public Guid? AuthorizedByUserId { get; private set; }

    public decimal Amount { get; private set; }

    public string? ProviderTokenRef { get; private set; }

    public string Status { get; private set; } = PaymentStatus.Pending;

    public string? ReasonCode { get; private set; }

    /// <summary>Datos de facturación del intento (JSON), para emitir el comprobante si lo aprueba la conciliación.</summary>
    public string? Billing { get; private set; }

    public DateTimeOffset? ProcessingStartedAt { get; private set; }

    public DateTimeOffset? ProcessedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>El total como lo publican el contrato y los eventos: texto con dos decimales.</summary>
    public string AmountText => Amount.ToString("0.00", CultureInfo.InvariantCulture);

    public static Payment CreatePending(
        Guid tenantId, Guid serviceRequestId, Guid clientId, Guid? companyId, Guid technicianId, decimal amount, DateTimeOffset now)
    {
        if (tenantId == Guid.Empty || serviceRequestId == Guid.Empty || clientId == Guid.Empty || technicianId == Guid.Empty)
        {
            throw new ArgumentException("El tenant, la solicitud, el cliente y el técnico son obligatorios.");
        }

        if (amount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "El monto debe ser mayor que cero.");
        }

        return new Payment
        {
            Id = Guid.CreateVersion7(now),
            TenantId = tenantId,
            ServiceRequestId = serviceRequestId,
            ClientId = clientId,
            CompanyId = companyId,
            PayerType = companyId is null ? PayerClient : PayerCompany,
            TechnicianId = technicianId,
            Amount = decimal.Round(amount, 2),
            Status = PaymentStatus.Pending,
            CreatedAt = now,
        };
    }

    /// <summary>
    /// Quién puede pagar: el usuario que creó la solicitud. El pago por cualquier contacto de la empresa (RN-P6) llega con
    /// SCRUM-43, cuando el token traiga la empresa del usuario.
    /// </summary>
    public bool CanBePaidBy(Guid userId) => userId != Guid.Empty && userId == ClientId;

    /// <summary>Pago nuevo para reintentar tras un rechazo, con el mismo monto, cliente y técnico (RN-P4).</summary>
    public Payment Retry(DateTimeOffset now)
    {
        if (Status != PaymentStatus.Rejected)
        {
            throw new InvalidOperationException("Solo se reintenta un pago rechazado.");
        }

        return CreatePending(TenantId, ServiceRequestId, ClientId, CompanyId, TechnicianId, Amount, now);
    }

    /// <summary><c>pendiente</c> → <c>procesando</c> (RN-P5). Guarda quién lo autorizó y los datos de facturación.</summary>
    public void StartProcessing(Guid userId, string billing, DateTimeOffset now)
    {
        if (Status != PaymentStatus.Pending)
        {
            throw new InvalidOperationException($"El pago está en '{Status}' y no se puede procesar.");
        }

        Status = PaymentStatus.Processing;
        AuthorizedByUserId = userId;
        Billing = billing;
        ProcessingStartedAt = now;
    }

    /// <summary>La pasarela no respondió: el pago sigue <c>procesando</c> con la referencia que haya dado (AC9-E5).</summary>
    public void RecordProviderReference(string? providerReference)
    {
        if (Status == PaymentStatus.Processing && providerReference is not null)
        {
            ProviderTokenRef = providerReference;
        }
    }

    /// <summary>Cierra el pago como aprobado; devuelve false si ya no estaba <c>procesando</c> (otro proceso lo cerró).</summary>
    public bool Approve(string? providerReference, DateTimeOffset now)
    {
        if (Status != PaymentStatus.Processing)
        {
            return false;
        }

        Status = PaymentStatus.Approved;
        ProviderTokenRef = providerReference ?? ProviderTokenRef;
        ReasonCode = null;
        ProcessedAt = now;
        return true;
    }

    /// <summary>Cierra el pago como rechazado; devuelve false si ya no estaba <c>procesando</c>.</summary>
    public bool Reject(string? providerReference, string reasonCode, DateTimeOffset now)
    {
        if (Status != PaymentStatus.Processing)
        {
            return false;
        }

        Status = PaymentStatus.Rejected;
        ProviderTokenRef = providerReference ?? ProviderTokenRef;
        ReasonCode = reasonCode;
        ProcessedAt = now;
        return true;
    }
}
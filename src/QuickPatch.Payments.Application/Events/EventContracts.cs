using System.Text.Json;
using System.Text.Json.Serialization;

using QuickPatch.Payments.Application.Abstractions;
using QuickPatch.Payments.Domain.Payments;

namespace QuickPatch.Payments.Application.Events;

/// <summary>Sobre común de los eventos (DD 8.2.1; <c>quickpatch-kafka/events</c>).</summary>
public sealed record EventEnvelope<TData>(
    Guid EventId,
    string EventType,
    int EventVersion,
    DateTimeOffset OccurredAt,
    Guid CorrelationId,
    Guid TenantId,
    string Producer,
    TData Data);

public static class EventTypes
{
    public const string ServiceRequestCompleted = "service-request.completed";
    public const string PaymentApproved = "payment.approved";
    public const string PaymentRejected = "payment.rejected";
}

/// <summary><c>data</c> de <c>service-request.completed</c> v1 (quickpatch-kafka v0.7.0).</summary>
public sealed record ServiceRequestCompletedData(
    Guid ServiceRequestId,
    Guid ClientId,
    Guid? CompanyId,
    Guid TechnicianId,
    Guid QuoteId,
    string TotalAmount,
    DateTimeOffset CompletedAt);

/// <summary><c>data</c> de <c>payment.approved</c> v1 (quickpatch-kafka v0.9.0).</summary>
public sealed record PaymentApprovedData(
    Guid PaymentId,
    Guid ServiceRequestId,
    Guid ClientId,
    Guid? CompanyId,
    Guid TechnicianId,
    string Amount,
    Guid InvoiceId,
    DateTimeOffset ApprovedAt);

/// <summary><c>data</c> de <c>payment.rejected</c> v1 (quickpatch-kafka v0.9.0).</summary>
public sealed record PaymentRejectedData(
    Guid PaymentId,
    Guid ServiceRequestId,
    Guid ClientId,
    Guid? CompanyId,
    Guid TechnicianId,
    string Amount,
    string? ReasonCode,
    DateTimeOffset RejectedAt);

public static class EventSerializer
{
    public const string Producer = "payments-service";

    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}

/// <summary>Eventos del pago listos para el Outbox. La clave del mensaje es la solicitud, como en los topics del contrato.</summary>
public static class PaymentEvents
{
    public static OutboxMessage Approved(Payment payment, Invoice invoice, Guid correlationId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(payment);
        ArgumentNullException.ThrowIfNull(invoice);
        return Build(payment, EventTypes.PaymentApproved, correlationId, now, new PaymentApprovedData(
            payment.Id, payment.ServiceRequestId, payment.ClientId, payment.CompanyId, payment.TechnicianId, payment.AmountText,
            invoice.Id, payment.ProcessedAt ?? now));
    }

    public static OutboxMessage Rejected(Payment payment, Guid correlationId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(payment);
        return Build(payment, EventTypes.PaymentRejected, correlationId, now, new PaymentRejectedData(
            payment.Id, payment.ServiceRequestId, payment.ClientId, payment.CompanyId, payment.TechnicianId, payment.AmountText,
            payment.ReasonCode, payment.ProcessedAt ?? now));
    }

    private static OutboxMessage Build<T>(Payment payment, string type, Guid correlationId, DateTimeOffset now, T data)
    {
        var eventId = Guid.CreateVersion7(now);
        var envelope = new EventEnvelope<T>(eventId, type, 1, now, correlationId, payment.TenantId, EventSerializer.Producer, data);
        return new OutboxMessage(eventId, payment.TenantId, payment.ServiceRequestId, type, EventSerializer.Serialize(envelope));
    }
}
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
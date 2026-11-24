using System.Text.Json;
using System.Text.Json.Serialization;

using QuickPatch.Payments.Application.Abstractions;
using QuickPatch.Payments.Domain.Payments;

namespace QuickPatch.Payments.Application.Events;

/// <summary><c>data</c> de <c>service-request.completed</c> v1 (quickpatch-kafka v0.7.0).</summary>
public sealed record ServiceRequestCompletedData(
    Guid ServiceRequestId,
    Guid ClientId,
    Guid? CompanyId,
    Guid TechnicianId,
    Guid QuoteId,
    string TotalAmount,
    DateTimeOffset CompletedAt);
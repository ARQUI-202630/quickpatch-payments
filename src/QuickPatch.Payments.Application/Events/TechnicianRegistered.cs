using System.Text.Json;
using System.Text.Json.Serialization;

using QuickPatch.Payments.Application.Abstractions;
using QuickPatch.Payments.Domain.Payments;

namespace QuickPatch.Payments.Application.Events;

/// <summary>
/// <c>data</c> de <c>technician.registered</c> v1 (<c>quickpatch-kafka</c> v0.10.0, P-02): solo se usan el técnico, su proveedor y
/// <c>updatedAt</c> como versión (ADR-017).
/// </summary>
public sealed record TechnicianRegisteredData(
    Guid TechnicianId,
    Guid SpecialtyId,
    Guid? ProviderId,
    string VerificationStatus,
    DateTimeOffset UpdatedAt);
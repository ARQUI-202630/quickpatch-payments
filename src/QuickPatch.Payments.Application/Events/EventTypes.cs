using System.Text.Json;
using System.Text.Json.Serialization;

using QuickPatch.Payments.Application.Abstractions;
using QuickPatch.Payments.Domain.Payments;

namespace QuickPatch.Payments.Application.Events;

public static class EventTypes
{
    public const string ServiceRequestCompleted = "service-request.completed";
    public const string TechnicianRegistered = "technician.registered";
    public const string PaymentApproved = "payment.approved";
    public const string PaymentRejected = "payment.rejected";
}
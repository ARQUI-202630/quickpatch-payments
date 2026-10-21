using QuickPatch.Payments.Api.Http;
using QuickPatch.Payments.Api.Security;
using QuickPatch.Payments.Application.Payments;
using QuickPatch.Payments.Domain.Common;
using QuickPatch.Payments.Domain.Payments;

namespace QuickPatch.Payments.Api.Endpoints;

/// <summary>Esquema <c>Payment</c> del contrato.</summary>
public sealed record PaymentResponse(
    Guid PaymentId,
    Guid ServiceRequestId,
    string Status,
    string Amount,
    string? ReasonCode,
    Guid? InvoiceId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ProcessedAt)
{
    public static PaymentResponse From(PaymentView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        var p = view.Payment;
        return new(p.Id, p.ServiceRequestId, p.Status, p.AmountText, p.ReasonCode, view.InvoiceId, p.CreatedAt, p.ProcessedAt);
    }
}

/// <summary>Esquema <c>Billing</c> del contrato.</summary>
public sealed record BillingRequest(string? Name, string? DocumentType, string? DocumentNumber, string? Email);

/// <summary>Esquema <c>PayRequest</c> del contrato.</summary>
public sealed record PayRequest(string? PaymentToken, BillingRequest? Billing);

/// <summary>Endpoints del contrato <c>payments.v1.yaml</c> 1.0.0 (SCRUM-42).</summary>
public static class PaymentEndpoints
{
    public static IEndpointRouteBuilder MapPaymentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/service-requests/{id:guid}/payment").RequireAuthorization(AuthenticatedUser.PayerPolicy);
        group.MapGet("/", GetAsync).WithName("getServiceRequestPayment");
        group.MapPost("/", PayAsync).WithName("payServiceRequest");
        return app;
    }

    private static async Task<IResult> GetAsync(Guid id, HttpContext context, GetPaymentHandler handler, CancellationToken cancellationToken)
    {
        if (!AuthenticatedUser.TryFrom(context.User, out var user))
        {
            return Unauthorized();
        }

        return await handler.HandleAsync(user.TenantId, user.UserId, id, cancellationToken) switch
        {
            GetPaymentResult.Found found => Results.Ok(PaymentResponse.From(found.View)),
            GetPaymentResult.NotReady => Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                type: Problems.PaymentNotReady,
                title: "La solicitud todavía no tiene pago",
                detail: "El pago se habilita unos segundos después de que el técnico completa el servicio."),
            _ => NotFound(),
        };
    }

    private static async Task<IResult> PayAsync(
        Guid id, PayRequest body, HttpContext context, PayHandler handler, CancellationToken cancellationToken)
    {
        if (!AuthenticatedUser.TryFrom(context.User, out var user))
        {
            return Unauthorized();
        }

        Billing billing;
        try
        {
            billing = Billing.Create(
                body.PaymentToken, body.Billing?.Name, body.Billing?.DocumentType, body.Billing?.DocumentNumber, body.Billing?.Email);
        }
        catch (DomainValidationException ex)
        {
            return Results.ValidationProblem(
                ex.Errors.ToDictionary(e => e.Key, e => e.Value), title: "La solicitud tiene datos inválidos", type: Problems.Validation);
        }

        var command = new PayCommand(user.TenantId, user.UserId, CorrelationId.Get(context), id, body.PaymentToken!.Trim(), billing);
        return await handler.HandleAsync(command, cancellationToken) switch
        {
            PayResult.Completed completed => Results.Ok(PaymentResponse.From(completed.View)),
            PayResult.NotReady => Conflict(Problems.PaymentNotReady, "La solicitud todavía no tiene pago"),
            PayResult.InProgress => Conflict(Problems.PaymentInProgress, "Ya hay un pago en curso para esta solicitud"),
            PayResult.AlreadyPaid => Conflict(Problems.AlreadyPaid, "La solicitud ya está pagada"),
            PayResult.ProviderUnavailable => Results.Problem(
                statusCode: StatusCodes.Status502BadGateway,
                type: Problems.ProviderUnavailable,
                title: "La pasarela no respondió",
                detail: "El pago quedó en proceso. Consulta su estado en unos minutos antes de intentar de nuevo."),
            _ => NotFound(),
        };
    }

    private static IResult Conflict(string type, string title) =>
        Results.Problem(statusCode: StatusCodes.Status409Conflict, type: type, title: title);

    private static IResult NotFound() =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "La solicitud no existe.");

    private static IResult Unauthorized() =>
        Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "El token no trae usuario ni tenant válidos.");
}
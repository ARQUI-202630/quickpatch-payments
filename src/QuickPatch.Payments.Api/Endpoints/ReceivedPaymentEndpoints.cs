using System.Globalization;

using QuickPatch.Payments.Api.Http;
using QuickPatch.Payments.Api.Security;
using QuickPatch.Payments.Application.Payments;
using QuickPatch.Payments.Domain.Payments;

namespace QuickPatch.Payments.Api.Endpoints;

/// <summary>Esquema <c>ReceivedPayment</c> del contrato payments 1.2.0: sin PAN, CVV ni token de la pasarela (RN-P1).</summary>
public sealed record ReceivedPaymentResponse(
    Guid Id,
    Guid ServiceRequestId,
    Guid TechnicianId,
    string Amount,
    string Currency,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ApprovedAt)
{
    public const string Cop = "COP";

    public static ReceivedPaymentResponse From(Payment payment)
    {
        ArgumentNullException.ThrowIfNull(payment);
        return new(
            payment.Id,
            payment.ServiceRequestId,
            payment.TechnicianId,
            payment.AmountText,
            Cop,
            payment.Status,
            payment.CreatedAt,
            payment.Status == PaymentStatus.Approved ? payment.ProcessedAt : null);
    }
}

/// <summary>Esquema <c>ReceivedPaymentPage</c> del contrato payments 1.2.0.</summary>
public sealed record ReceivedPaymentPageResponse(IReadOnlyList<ReceivedPaymentResponse> Items, int Page, int Size, int TotalItems);

/// <summary><c>GET /v1/payments/received</c> del contrato payments 1.2.0 (RF-25, SCRUM-45 y SCRUM-501).</summary>
public static class ReceivedPaymentEndpoints
{
    private const int DefaultSize = 20;

    public static IEndpointRouteBuilder MapReceivedPaymentEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/payments/received", ListAsync)
            .RequireAuthorization(AuthenticatedUser.ReceivedPaymentsReaderPolicy)
            .WithName("listReceivedPayments");
        return app;
    }

    // Los parámetros llegan como texto para responder 400 con el error por campo y el correlationId, igual que el resto
    // de errores de validación del servicio, en vez del 400 vacío del enlazado de Minimal API.
    private static async Task<IResult> ListAsync(
        string? technicianId,
        string? status,
        string? page,
        string? size,
        HttpContext context,
        ReceivedPaymentsHandler handler,
        CancellationToken cancellationToken)
    {
        if (!AuthenticatedUser.TryFrom(context.User, out var user))
        {
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "El token no trae usuario ni tenant válidos.");
        }

        var errors = new Dictionary<string, string[]>();
        if (technicianId is not null)
        {
            // El técnico solo ve sus pagos: el filtro es del proveedor (contrato payments 1.2.0).
            errors["technicianId"] = ["El técnico no usa este filtro: ve solo sus pagos."];
        }

        if (status is not null && !ReceivedPaymentsHandler.ListedStatuses.Contains(status))
        {
            errors["status"] = ["Debe ser pendiente, procesando o aprobado."];
        }

        var pageNumber = ParseOrDefault(page, 1, int.MaxValue, "page", "Debe ser un entero desde 1.", errors);
        var pageSize = ParseOrDefault(size, DefaultSize, ReceivedPaymentsHandler.MaxSize, "size", "Debe ser un entero entre 1 y 100.", errors);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors, title: "La solicitud tiene datos inválidos", type: Problems.Validation);
        }

        var result = await handler.HandleForTechnicianAsync(user.TenantId, user.UserId, status, pageNumber, pageSize, cancellationToken);
        return Results.Ok(new ReceivedPaymentPageResponse(
            [.. result.Items.Select(ReceivedPaymentResponse.From)], result.Page, result.Size, result.TotalItems));
    }

    private static int ParseOrDefault(
        string? value, int fallback, int max, string field, string message, Dictionary<string, string[]> errors)
    {
        if (value is null)
        {
            return fallback;
        }

        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed >= 1 && parsed <= max)
        {
            return parsed;
        }

        errors[field] = [message];
        return fallback;
    }
}
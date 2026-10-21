using Microsoft.Net.Http.Headers;

using QuickPatch.Payments.Api.Security;
using QuickPatch.Payments.Application.Abstractions;
using QuickPatch.Payments.Application.Invoices;
using QuickPatch.Payments.Domain.Payments;

namespace QuickPatch.Payments.Api.Endpoints;

/// <summary>Esquema <c>Invoice</c> del contrato payments 1.1.0.</summary>
public sealed record InvoiceResponse(
    Guid InvoiceId,
    string InvoiceNumber,
    Guid PaymentId,
    Guid ServiceRequestId,
    Billing Payer,
    string Amount,
    string Currency,
    string Concept,
    DateTimeOffset IssuedAt,
    bool FiscalValidity)
{
    public static InvoiceResponse From(InvoiceDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var invoice = document.Invoice;
        return new(
            invoice.Id,
            invoice.InvoiceNumber,
            invoice.PaymentId,
            document.Payment.ServiceRequestId,
            document.Payer,
            document.Payment.AmountText,
            InvoiceDocument.Currency,
            document.Concept,
            invoice.IssuedAt,
            FiscalValidity: false);
    }
}

/// <summary><c>GET /v1/payments/{id}/invoice</c> del contrato payments 1.1.0 (SCRUM-44): JSON o PDF según <c>Accept</c>.</summary>
public static class InvoiceEndpoints
{
    private static readonly MediaTypeHeaderValue Json = new("application/json");
    private static readonly MediaTypeHeaderValue Pdf = new("application/pdf");

    public static IEndpointRouteBuilder MapInvoiceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/payments/{id:guid}/invoice", GetAsync)
            .RequireAuthorization(AuthenticatedUser.InvoiceReaderPolicy)
            .WithName("getPaymentInvoice");
        return app;
    }

    private static async Task<IResult> GetAsync(
        Guid id, HttpContext context, GetInvoiceHandler handler, IInvoicePdfRenderer renderer, CancellationToken cancellationToken)
    {
        if (!AuthenticatedUser.TryFrom(context.User, out var user))
        {
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "El token no trae usuario ni tenant válidos.");
        }

        var format = Negotiate(context.Request.GetTypedHeaders().Accept);
        if (format is null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status406NotAcceptable,
                title: "Formato no disponible",
                detail: "El comprobante se entrega como application/json o application/pdf.");
        }

        var document = await handler.HandleAsync(user.TenantId, user.UserId, user.IsTenantAdmin, id, cancellationToken);
        if (document is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "El comprobante no existe.");
        }

        return format == Pdf
            ? Results.File(renderer.Render(document), "application/pdf", $"{document.Invoice.InvoiceNumber}.pdf")
            : Results.Ok(InvoiceResponse.From(document));
    }

    /// <summary>El primer formato aceptable por calidad; sin <c>Accept</c> o con comodines, JSON.</summary>
    private static MediaTypeHeaderValue? Negotiate(IList<MediaTypeHeaderValue> accept)
    {
        if (accept.Count == 0)
        {
            return Json;
        }

        foreach (var range in accept.Where(a => a.Quality is not 0).OrderByDescending(a => a.Quality ?? 1))
        {
            if (Json.IsSubsetOf(range))
            {
                return Json;
            }

            if (Pdf.IsSubsetOf(range))
            {
                return Pdf;
            }
        }

        return null;
    }
}
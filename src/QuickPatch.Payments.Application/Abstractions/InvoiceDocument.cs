using QuickPatch.Payments.Domain.Payments;

namespace QuickPatch.Payments.Application.Abstractions;

/// <summary>
/// Comprobante de un pago aprobado con lo que se muestra de él (esquema <c>Invoice</c> del contrato payments 1.1.0): los
/// datos de facturación que el pagador envió al pagar y el servicio que se pagó.
/// </summary>
public sealed record InvoiceDocument(Invoice Invoice, Payment Payment, Billing Payer)
{
    public const string Currency = "COP";

    public string Concept => $"Servicio técnico de la solicitud {Payment.ServiceRequestId}";
}
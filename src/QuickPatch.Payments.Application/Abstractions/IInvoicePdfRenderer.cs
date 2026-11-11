using QuickPatch.Payments.Application.Invoices;
using QuickPatch.Payments.Domain.Payments;

namespace QuickPatch.Payments.Application.Abstractions;

/// <summary>
/// Genera el PDF del comprobante (RF-24) al pedirlo, con los mismos datos del JSON; no se guarda en ningún almacenamiento.
/// </summary>
public interface IInvoicePdfRenderer
{
    byte[] Render(InvoiceDocument document);
}
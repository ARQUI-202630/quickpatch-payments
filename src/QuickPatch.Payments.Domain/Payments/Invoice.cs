namespace QuickPatch.Payments.Domain.Payments;

/// <summary>
/// Comprobante de un pago aprobado (tabla <c>invoices</c>, DD 5.13, RF-24). Se emite en la misma transacción que aprueba
/// el pago. La consulta y el PDF llegan con SCRUM-44; aquí queda el registro con su número único.
/// </summary>
public sealed class Invoice
{
    private Invoice()
    {
    }

    public Guid Id { get; private set; }

    public Guid PaymentId { get; private set; }

    public Guid TenantId { get; private set; }

    public string InvoiceNumber { get; private set; } = string.Empty;

    public string PayerName { get; private set; } = string.Empty;

    public string? PayerNit { get; private set; }

    public decimal Amount { get; private set; }

    public DateTimeOffset IssuedAt { get; private set; }

    public string? PdfUrl { get; private set; }

    /// <summary>
    /// Número <c>QP-AAAAMMDD-XXXXXXXXXX</c>: la fecha de emisión y los primeros 10 caracteres del id del pago, que es único.
    /// </summary>
    public static Invoice Issue(Payment payment, Billing billing, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(payment);
        ArgumentNullException.ThrowIfNull(billing);
        if (payment.Status != PaymentStatus.Approved)
        {
            throw new InvalidOperationException("Solo un pago aprobado tiene comprobante.");
        }

        return new Invoice
        {
            Id = Guid.CreateVersion7(now),
            PaymentId = payment.Id,
            TenantId = payment.TenantId,
            InvoiceNumber = $"QP-{now:yyyyMMdd}-{payment.Id.ToString("N")[..10].ToUpperInvariant()}",
            PayerName = billing.Name,
            PayerNit = billing.DocumentType == "NIT" ? billing.DocumentNumber : null,
            Amount = payment.Amount,
            IssuedAt = now,
        };
    }
}
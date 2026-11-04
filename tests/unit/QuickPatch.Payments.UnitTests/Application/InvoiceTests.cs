using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using QuickPatch.Payments.Application.Invoices;
using QuickPatch.Payments.Domain.Payments;
using QuickPatch.Payments.Infrastructure.Documents;
using QuickPatch.Payments.UnitTests.Support;

namespace QuickPatch.Payments.UnitTests.Application;

/// <summary>Comprobante del pago aprobado (RF-24, SCRUM-44): quién lo ve y su PDF.</summary>
public partial class InvoiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 15, 2, 10, TimeSpan.Zero);

    private readonly InMemoryPayments store = new();

    private GetInvoiceHandler Handler() => new(store, store);

    [Fact]
    public async Task ElPagadorVeSuComprobante_ConLosDatosDeFacturacion()
    {
        var (tenant, client) = (Guid.NewGuid(), Guid.NewGuid());
        var (payment, invoice) = store.SeedApproved(tenant, client, at: Now);

        var document = await Handler().HandleAsync(tenant, client, isTenantAdmin: false, payment.Id, CancellationToken.None);

        Assert.NotNull(document);
        Assert.Equal(invoice.Id, document.Invoice.Id);
        Assert.Equal(new Billing("Ana Gómez", "CC", "52123456", "ana@correo.com"), document.Payer);
        Assert.Equal($"Servicio técnico de la solicitud {payment.ServiceRequestId}", document.Concept);
    }

    [Fact]
    public async Task ElAdminDelTenantLoVe_OtroUsuarioOtroTenantYUnPagoSinComprobanteNo()
    {
        var (tenant, client) = (Guid.NewGuid(), Guid.NewGuid());
        var (payment, _) = store.SeedApproved(tenant, client, at: Now);
        var pending = store.SeedPending(tenant, client, at: Now.AddMinutes(-10));
        var handler = Handler();

        Assert.NotNull(await handler.HandleAsync(tenant, Guid.NewGuid(), isTenantAdmin: true, payment.Id, CancellationToken.None));
        Assert.Null(await handler.HandleAsync(tenant, Guid.NewGuid(), isTenantAdmin: false, payment.Id, CancellationToken.None));
        Assert.Null(await handler.HandleAsync(Guid.NewGuid(), client, isTenantAdmin: true, payment.Id, CancellationToken.None));
        Assert.Null(await handler.HandleAsync(tenant, client, isTenantAdmin: false, pending.Id, CancellationToken.None));
        Assert.Null(await handler.HandleAsync(tenant, client, isTenantAdmin: false, Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public void Pdf_ValidoConNumeroNitTotalYRotuloFiscal()
    {
        var (tenant, client) = (Guid.NewGuid(), Guid.NewGuid());
        var billing = new Billing("Inversiones (Andinas) S.A.S. \\ sede Bogotá", "NIT", "900123456-7", "pagos@andinas.co");
        var (payment, invoice) = store.SeedApproved(tenant, client, billing, Now);

        var pdf = new InvoicePdfRenderer().Render(new InvoiceDocument(invoice, payment, billing));
        var text = Encoding.Latin1.GetString(pdf);

        Assert.StartsWith("%PDF-1.4\n", text, StringComparison.Ordinal);
        Assert.EndsWith("%%EOF\n", text, StringComparison.Ordinal);
        Assert.Contains($"({invoice.InvoiceNumber})", text, StringComparison.Ordinal);
        Assert.Contains("(NIT 900123456-7)", text, StringComparison.Ordinal);
        Assert.Contains(@"(Inversiones \(Andinas\) S.A.S. \\ sede Bogotá)", text, StringComparison.Ordinal);
        Assert.Contains("($ 185.000 COP)", text, StringComparison.Ordinal);
        Assert.Contains("(Documento sin validez fiscal.)", text, StringComparison.Ordinal);
        Assert.Contains("(Emitido el 21/10/2026 10:02)", text, StringComparison.Ordinal);
        Assert.Equal(0xE1, pdf[text.IndexOf("Bogotá", StringComparison.Ordinal) + 5]);

        // Cada entrada de la tabla xref apunta al inicio de su objeto, y startxref a la tabla.
        var start = int.Parse(StartXref().Match(text).Groups[1].Value, CultureInfo.InvariantCulture);
        Assert.StartsWith("xref", text[start..], StringComparison.Ordinal);
        var offsets = XrefEntry().Matches(text).Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
        Assert.Equal(7, offsets.Count);
        for (var i = 0; i < offsets.Count; i++)
        {
            Assert.StartsWith($"{i + 1} 0 obj", text[offsets[i]..], StringComparison.Ordinal);
        }

        var length = int.Parse(StreamLength().Match(text).Groups[1].Value, CultureInfo.InvariantCulture);
        var body = text.IndexOf("stream\n", StringComparison.Ordinal) + "stream\n".Length;
        Assert.StartsWith("\nendstream", text[(body + length)..], StringComparison.Ordinal);
    }

    [Fact]
    public void Pdf_ConNombreLargoYCaracteresFueraDeLatin1()
    {
        var (tenant, client) = (Guid.NewGuid(), Guid.NewGuid());
        var billing = new Billing($"{new string('x', 70)} Nombre muy largo ✓ {new string('y', 140)}", "CC", "52123456", "ana@correo.com");
        var (payment, invoice) = store.SeedApproved(tenant, client, billing, Now);

        var text = Encoding.Latin1.GetString(new InvoicePdfRenderer().Render(new InvoiceDocument(invoice, payment, billing)));

        Assert.Contains($"({new string('x', 62)})", text, StringComparison.Ordinal);
        Assert.Contains("(xxxxxxxx Nombre muy largo ?)", text, StringComparison.Ordinal);
        Assert.Contains($"({new string('y', 62)})", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("185000", "$ 185.000 COP")]
    [InlineData("1234567.5", "$ 1.234.567,50 COP")]
    [InlineData("900", "$ 900 COP")]
    public void Money_ConPuntoDeMiles(string amount, string expected) =>
        Assert.Equal(expected, InvoicePdfRenderer.Money(decimal.Parse(amount, CultureInfo.InvariantCulture)));

    [GeneratedRegex(@"startxref\n(\d+)\n")]
    private static partial Regex StartXref();

    [GeneratedRegex(@"(\d{10}) 00000 n \n")]
    private static partial Regex XrefEntry();

    [GeneratedRegex(@"<< /Length (\d+) >>")]
    private static partial Regex StreamLength();
}
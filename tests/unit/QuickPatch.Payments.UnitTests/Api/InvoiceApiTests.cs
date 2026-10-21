using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

using QuickPatch.Payments.Domain.Payments;
using QuickPatch.Payments.UnitTests.Support;

namespace QuickPatch.Payments.UnitTests.Api;

/// <summary><c>GET /v1/payments/{id}/invoice</c> del contrato payments 1.1.0 (SCRUM-44).</summary>
public class InvoiceApiTests(PaymentsApiFactory factory) : IClassFixture<PaymentsApiFactory>
{
    private static Uri Route(Guid payment) => new($"/v1/payments/{payment}/invoice", UriKind.Relative);

    private HttpClient Client(Guid user, Guid tenant, string role = "cliente", string? accept = null)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Create(user, tenant, role));
        if (accept is not null)
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", accept);
        }

        return client;
    }

    private (Guid Tenant, Guid Client, Payment Payment, Invoice Invoice) Seed(Billing? billing = null)
    {
        var (tenant, client) = (Guid.NewGuid(), Guid.NewGuid());
        lock (factory.Store)
        {
            var (payment, invoice) = factory.Store.SeedApproved(tenant, client, billing);
            return (tenant, client, payment, invoice);
        }
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("application/json")]
    [InlineData("*/*")]
    [InlineData("application/pdf;q=0.5, application/json")]
    public async Task Json_PorDefecto_ConElContratoInvoice(string? accept)
    {
        var (tenant, client, payment, invoice) = Seed(new Billing("Inversiones Andinas S.A.S.", "NIT", "900123456-7", "pagos@andinas.co"));
        using var http = Client(client, tenant, accept: accept);

        using var response = await http.GetAsync(Route(payment.Id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = await Json(response);
        Assert.Equal(
            ["invoiceId", "invoiceNumber", "paymentId", "serviceRequestId", "payer", "amount", "currency", "concept", "issuedAt", "fiscalValidity"],
            body.EnumerateObject().Select(p => p.Name));
        Assert.Equal(invoice.Id, body.GetProperty("invoiceId").GetGuid());
        Assert.Matches("^QP-[0-9]{8}-[0-9A-F]{10}$", body.GetProperty("invoiceNumber").GetString());
        Assert.Equal(payment.ServiceRequestId, body.GetProperty("serviceRequestId").GetGuid());
        Assert.Equal("185000.00", body.GetProperty("amount").GetString());
        Assert.Equal("COP", body.GetProperty("currency").GetString());
        Assert.False(body.GetProperty("fiscalValidity").GetBoolean());
        var payer = body.GetProperty("payer");
        Assert.Equal(["name", "documentType", "documentNumber", "email"], payer.EnumerateObject().Select(p => p.Name));
        Assert.Equal("NIT", payer.GetProperty("documentType").GetString());
        Assert.Equal("900123456-7", payer.GetProperty("documentNumber").GetString());
    }

    [Theory]
    [InlineData("application/pdf")]
    [InlineData("application/json;q=0.2, application/pdf")]
    public async Task Pdf_ConAcceptComoAdjunto(string accept)
    {
        var (tenant, client, payment, invoice) = Seed();
        using var http = Client(client, tenant, accept: accept);

        using var response = await http.GetAsync(Route(payment.Id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal($"{invoice.InvoiceNumber}.pdf", response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal("%PDF-"u8.ToArray(), bytes[..5]);
    }

    [Fact]
    public async Task OtroFormato_406()
    {
        var (tenant, client, payment, _) = Seed();
        using var http = Client(client, tenant, accept: "text/html");

        using var response = await http.GetAsync(Route(payment.Id));

        Assert.Equal(HttpStatusCode.NotAcceptable, response.StatusCode);
    }

    [Fact]
    public async Task AdminDelTenant_LoVe_OtroClienteOtroTenantYPagoSinComprobante_404()
    {
        var (tenant, client, payment, _) = Seed();
        Payment pending;
        lock (factory.Store)
        {
            pending = factory.Store.SeedPending(tenant, client);
        }

        using var admin = Client(Guid.NewGuid(), tenant, "admin_tenant");
        using var otherClient = Client(Guid.NewGuid(), tenant);
        using var otherTenant = Client(client, Guid.NewGuid());
        using var owner = Client(client, tenant);

        using var a = await admin.GetAsync(Route(payment.Id));
        using var b = await otherClient.GetAsync(Route(payment.Id));
        using var c = await otherTenant.GetAsync(Route(payment.Id));
        using var d = await owner.GetAsync(Route(pending.Id));

        Assert.Equal(HttpStatusCode.OK, a.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, b.StatusCode);
        Assert.EndsWith("/no-encontrado", (await Json(b)).GetProperty("type").GetString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, c.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, d.StatusCode);
    }

    [Fact]
    public async Task Tecnico_403_YSinToken_401()
    {
        var (tenant, _, payment, _) = Seed();
        using var technician = Client(Guid.NewGuid(), tenant, "tecnico");
        using var anonymous = factory.CreateClient();

        using var forbidden = await technician.GetAsync(Route(payment.Id));
        using var unauthorized = await anonymous.GetAsync(Route(payment.Id));

        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
    }
}
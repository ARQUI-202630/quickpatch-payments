using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

using QuickPatch.Payments.Domain.Payments;
using QuickPatch.Payments.UnitTests.Support;

namespace QuickPatch.Payments.UnitTests.Api;

/// <summary><c>GET /v1/payments/received</c> del contrato payments 1.2.0 para el técnico (RF-25, SCRUM-501).</summary>
public class ReceivedPaymentApiTests(PaymentsApiFactory factory) : IClassFixture<PaymentsApiFactory>
{
    private static Uri Route(string query = "") => new($"/v1/payments/received{query}", UriKind.Relative);

    private HttpClient Client(Guid user, Guid tenant, string role = "tecnico")
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Create(user, tenant, role));
        return client;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    /// <summary>Un pago pendiente, uno aprobado y uno rechazado con su reintento pendiente, todos del técnico.</summary>
    private (Guid Tenant, Guid Technician, Payment Pending, Payment Approved, Payment Rejected, Payment Retry) Seed()
    {
        var (tenant, technician) = (Guid.NewGuid(), Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;
        lock (factory.Store)
        {
            var approved = factory.Store.SeedPending(tenant, Guid.NewGuid(), at: now.AddHours(-3), technician: technician);
            approved.StartProcessing(approved.ClientId, new Billing("Ana Gómez", "CC", "52123456", "ana@correo.com").ToJson(), now.AddHours(-3));
            approved.Approve("sim_ref", now.AddHours(-2));

            var rejected = factory.Store.SeedPending(tenant, Guid.NewGuid(), at: now.AddHours(-2), technician: technician);
            rejected.StartProcessing(rejected.ClientId, new Billing("Ana Gómez", "CC", "52123456", "ana@correo.com").ToJson(), now.AddHours(-2));
            rejected.Reject("sim_ref", RejectionReasons.InsufficientFunds, now.AddHours(-2));
            var retry = rejected.Retry(now.AddMinutes(-90));
            factory.Store.Payments.Add(retry);

            var pending = factory.Store.SeedPending(tenant, Guid.NewGuid(), at: now.AddHours(-1), technician: technician);
            return (tenant, technician, pending, approved, rejected, retry);
        }
    }

    [Fact]
    public async Task Tecnico_VeSusPagos_SinRechazados_DelMasReciente_ConElContrato()
    {
        var (tenant, technician, pending, approved, _, retry) = Seed();
        using var http = Client(technician, tenant);

        using var response = await http.GetAsync(Route());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        Assert.Equal(["items", "page", "size", "totalItems"], body.EnumerateObject().Select(p => p.Name));
        Assert.Equal(1, body.GetProperty("page").GetInt32());
        Assert.Equal(20, body.GetProperty("size").GetInt32());
        Assert.Equal(3, body.GetProperty("totalItems").GetInt32());
        var items = body.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal([pending.Id, retry.Id, approved.Id], items.Select(i => i.GetProperty("id").GetGuid()));

        var first = items[0];
        Assert.Equal(
            ["id", "serviceRequestId", "technicianId", "amount", "currency", "status", "createdAt", "approvedAt"],
            first.EnumerateObject().Select(p => p.Name));
        Assert.Equal(technician, first.GetProperty("technicianId").GetGuid());
        Assert.Equal("185000.00", first.GetProperty("amount").GetString());
        Assert.Equal("COP", first.GetProperty("currency").GetString());
        Assert.Equal("pendiente", first.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("approvedAt").ValueKind);

        var paid = items[2];
        Assert.Equal("aprobado", paid.GetProperty("status").GetString());
        Assert.Equal(approved.ProcessedAt, paid.GetProperty("approvedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task SinDatosDeTarjeta_NiElTokenDeLaPasarela()
    {
        var (tenant, technician, _, _, _, _) = Seed();
        using var http = Client(technician, tenant);

        using var response = await http.GetAsync(Route());
        var raw = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("sim_ref", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("billing", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("52123456", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("token", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FiltraPorEstado_YPagina()
    {
        var (tenant, technician, _, _, _, retry) = Seed();
        using var http = Client(technician, tenant);

        var approved = await Json(await http.GetAsync(Route("?status=aprobado")));
        Assert.Equal(1, approved.GetProperty("totalItems").GetInt32());
        Assert.Equal("aprobado", approved.GetProperty("items")[0].GetProperty("status").GetString());

        var second = await Json(await http.GetAsync(Route("?status=pendiente&page=2&size=1")));
        Assert.Equal(2, second.GetProperty("totalItems").GetInt32());
        Assert.Equal(2, second.GetProperty("page").GetInt32());
        Assert.Equal(1, second.GetProperty("size").GetInt32());
        Assert.Equal(retry.Id, Assert.Single(second.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());

        var beyond = await Json(await http.GetAsync(Route($"?page={int.MaxValue}&size=100")));
        Assert.Empty(beyond.GetProperty("items").EnumerateArray());
        Assert.Equal(3, beyond.GetProperty("totalItems").GetInt32());
    }

    [Fact]
    public async Task OtroTecnico_YOtroTenant_VenLaListaVacia()
    {
        var (tenant, technician, _, _, _, _) = Seed();

        using var other = Client(Guid.NewGuid(), tenant);
        var otherBody = await Json(await other.GetAsync(Route()));
        Assert.Equal(0, otherBody.GetProperty("totalItems").GetInt32());
        Assert.Empty(otherBody.GetProperty("items").EnumerateArray());

        // Mismo usuario con el token de otro tenant: RLS no deja ver los pagos del primero.
        using var otherTenant = Client(technician, Guid.NewGuid());
        var tenantBody = await Json(await otherTenant.GetAsync(Route()));
        Assert.Equal(0, tenantBody.GetProperty("totalItems").GetInt32());
    }

    [Theory]
    [InlineData("?technicianId=9b1d4c2e-7f3a-4e8b-a5c6-2d0e1f3a4b5c", "technicianId")]
    [InlineData("?status=rechazado", "status")]
    [InlineData("?page=0", "page")]
    [InlineData("?page=abc", "page")]
    [InlineData("?size=0", "size")]
    [InlineData("?size=101", "size")]
    [InlineData("?size=-5", "size")]
    public async Task ParametrosInvalidos_400ConElCampo(string query, string field)
    {
        using var http = Client(Guid.NewGuid(), Guid.NewGuid());

        using var response = await http.GetAsync(Route(query));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await Json(response);
        Assert.Equal("https://quickpatch.internal/problems/validacion", body.GetProperty("type").GetString());
        Assert.True(body.GetProperty("errors").TryGetProperty(field, out _));
        Assert.True(body.TryGetProperty("correlationId", out _));
    }

    [Theory]
    [InlineData("cliente")]
    [InlineData("empresa_contacto")]
    [InlineData("admin_tenant")]
    public async Task OtroRol_403(string role)
    {
        using var http = Client(Guid.NewGuid(), Guid.NewGuid(), role);

        using var response = await http.GetAsync(Route());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SinToken_401()
    {
        using var http = factory.CreateClient();

        using var response = await http.GetAsync(Route());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
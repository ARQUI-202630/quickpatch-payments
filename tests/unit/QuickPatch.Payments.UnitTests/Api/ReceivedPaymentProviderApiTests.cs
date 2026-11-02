using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

using QuickPatch.Payments.Domain.Payments;
using QuickPatch.Payments.UnitTests.Support;

namespace QuickPatch.Payments.UnitTests.Api;

/// <summary>
/// <c>GET /v1/payments/received</c> para el <c>proveedor</c> (payments 1.2.0, RF-25, SCRUM-501): los pagos de los técnicos de su
/// equipo según la proyección <c>payment_technicians</c> (P-02), con el filtro <c>technicianId</c>.
/// </summary>
public class ReceivedPaymentProviderApiTests(PaymentsApiFactory factory) : IClassFixture<PaymentsApiFactory>
{
    private static Uri Route(string query = "") => new($"/v1/payments/received{query}", UriKind.Relative);

    private HttpClient Client(Guid user, Guid tenant, string role = "proveedor")
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

    /// <summary>Un proveedor con dos técnicos de equipo, un técnico independiente y un técnico de otro proveedor, todos con pagos.</summary>
    private (Guid Tenant, Guid Provider, Guid Member1, Guid Member2, Guid Independent, Guid OtherTeam, Guid OtherProvider, Payment[] Payments) Seed()
    {
        var (tenant, provider, otherProvider) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var (member1, member2, independent, otherTeam) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;
        lock (factory.Store)
        {
            factory.Store.Team.Add(new PaymentTechnician(member1, tenant, provider, now));
            factory.Store.Team.Add(new PaymentTechnician(member2, tenant, provider, now));
            factory.Store.Team.Add(new PaymentTechnician(independent, tenant, null, now));
            factory.Store.Team.Add(new PaymentTechnician(otherTeam, tenant, otherProvider, now));
            var payments = new[]
            {
                factory.Store.SeedPending(tenant, Guid.NewGuid(), at: now.AddHours(-4), technician: member1),
                factory.Store.SeedPending(tenant, Guid.NewGuid(), at: now.AddHours(-3), technician: member2),
                factory.Store.SeedPending(tenant, Guid.NewGuid(), at: now.AddHours(-2), technician: member1),
                factory.Store.SeedPending(tenant, Guid.NewGuid(), at: now.AddHours(-1), technician: independent),
                factory.Store.SeedPending(tenant, Guid.NewGuid(), at: now.AddMinutes(-30), technician: otherTeam),
            };
            return (tenant, provider, member1, member2, independent, otherTeam, otherProvider, payments);
        }
    }

    [Fact]
    public async Task Proveedor_VeLosPagosDeSuEquipo_YNoLosDeIndependientesNiDeOtroProveedor()
    {
        var (tenant, provider, member1, member2, _, _, _, payments) = Seed();
        using var http = Client(provider, tenant);

        using var response = await http.GetAsync(Route());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        Assert.Equal(3, body.GetProperty("totalItems").GetInt32());
        var items = body.GetProperty("items").EnumerateArray().ToList();
        // El más reciente primero: member1 (-2 h), member2 (-3 h), member1 (-4 h).
        Assert.Equal([payments[2].Id, payments[1].Id, payments[0].Id], items.Select(i => i.GetProperty("id").GetGuid()));
        Assert.All(items, i => Assert.Contains(i.GetProperty("technicianId").GetGuid(), new[] { member1, member2 }));
    }

    [Fact]
    public async Task Proveedor_ConTechnicianId_VeSoloLosDeEseTecnicoDeSuEquipo()
    {
        var (tenant, provider, member1, _, _, _, _, payments) = Seed();
        using var http = Client(provider, tenant);

        var body = await Json(await http.GetAsync(Route($"?technicianId={member1}")));

        Assert.Equal(2, body.GetProperty("totalItems").GetInt32());
        Assert.Equal([payments[2].Id, payments[0].Id], body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task Proveedor_ConUnTecnicoQueNoEsDeSuEquipo_VeLaListaVacia()
    {
        var (tenant, provider, _, _, independent, otherTeam, _, _) = Seed();
        using var http = Client(provider, tenant);

        foreach (var stranger in new[] { independent, otherTeam, Guid.NewGuid() })
        {
            var body = await Json(await http.GetAsync(Route($"?technicianId={stranger}")));
            Assert.Equal(0, body.GetProperty("totalItems").GetInt32());
            Assert.Empty(body.GetProperty("items").EnumerateArray());
        }
    }

    [Fact]
    public async Task OtroProveedor_NoVeLosPagosDeEsteEquipo_YUnProveedorSinEquipoVeVacio()
    {
        var (tenant, _, _, _, _, _, otherProvider, payments) = Seed();

        using var other = Client(otherProvider, tenant);
        var otherBody = await Json(await other.GetAsync(Route()));
        Assert.Equal(1, otherBody.GetProperty("totalItems").GetInt32());
        Assert.Equal(payments[4].Id, otherBody.GetProperty("items")[0].GetProperty("id").GetGuid());

        using var empty = Client(Guid.NewGuid(), tenant);
        Assert.Equal(0, (await Json(await empty.GetAsync(Route()))).GetProperty("totalItems").GetInt32());
    }

    [Fact]
    public async Task Proveedor_DeOtroTenant_NoVeNada()
    {
        var (_, provider, _, _, _, _, _, _) = Seed();

        using var http = Client(provider, Guid.NewGuid());
        var body = await Json(await http.GetAsync(Route()));

        Assert.Equal(0, body.GetProperty("totalItems").GetInt32());
    }

    [Fact]
    public async Task Proveedor_FiltraPorEstado_YPagina_SinMostrarLosRechazados()
    {
        var (tenant, provider, member1, _, _, _, _, payments) = Seed();
        var now = DateTimeOffset.UtcNow;
        lock (factory.Store)
        {
            var rejected = factory.Store.SeedPending(tenant, Guid.NewGuid(), at: now.AddHours(-5), technician: member1);
            rejected.StartProcessing(rejected.ClientId, new Billing("Ana Gómez", "CC", "52123456", "ana@correo.com").ToJson(), now.AddHours(-5));
            rejected.Reject("sim_ref", RejectionReasons.InsufficientFunds, now.AddHours(-5));
        }

        using var http = Client(provider, tenant);

        var all = await Json(await http.GetAsync(Route()));
        Assert.Equal(3, all.GetProperty("totalItems").GetInt32());
        var page2 = await Json(await http.GetAsync(Route("?page=2&size=2")));
        Assert.Equal(3, page2.GetProperty("totalItems").GetInt32());
        Assert.Equal(payments[0].Id, Assert.Single(page2.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        var approved = await Json(await http.GetAsync(Route("?status=aprobado")));
        Assert.Equal(0, approved.GetProperty("totalItems").GetInt32());
    }

    [Theory]
    [InlineData("?technicianId=no-es-un-uuid")]
    [InlineData("?technicianId=00000000-0000-0000-0000-000000000000")]
    public async Task Proveedor_ConTechnicianIdInvalido_400ConElCampo(string query)
    {
        using var http = Client(Guid.NewGuid(), Guid.NewGuid());

        using var response = await http.GetAsync(Route(query));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await Json(response);
        Assert.True(body.GetProperty("errors").TryGetProperty("technicianId", out _));
    }

    [Fact]
    public async Task Tecnico_SigueSinPoderUsarTechnicianId_Ni_VerPagosDeOtros()
    {
        var (tenant, _, member1, _, _, _, _, _) = Seed();
        using var http = Client(member1, tenant, "tecnico");

        using var withFilter = await http.GetAsync(Route($"?technicianId={Guid.NewGuid()}"));
        var own = await Json(await http.GetAsync(Route()));

        Assert.Equal(HttpStatusCode.BadRequest, withFilter.StatusCode);
        Assert.Equal(2, own.GetProperty("totalItems").GetInt32());
        Assert.All(own.GetProperty("items").EnumerateArray(), i => Assert.Equal(member1, i.GetProperty("technicianId").GetGuid()));
    }
}
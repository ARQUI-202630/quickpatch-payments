using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

using QuickPatch.Payments.Application.Abstractions;
using QuickPatch.Payments.UnitTests.Support;

namespace QuickPatch.Payments.UnitTests.Api;

public sealed class PaymentsApiFactory : WebApplicationFactory<Program>
{
    public InMemoryPayments Store { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Jwt:PublicKeyPem", TestTokens.PublicKeyPem);
        builder.UseSetting("ConnectionStrings:Payments", "Host=sin-base-en-pruebas-unitarias");
        builder.UseSetting("Reconciliation:Enabled", "false");
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<ITenantUnitOfWork>(Store);
            services.AddSingleton<IPaymentRepository>(Store);
            services.AddSingleton<IProcessedEventRepository>(Store);
            services.AddSingleton<IOutbox>(Store);
            services.AddSingleton<IReconciliationSource>(Store);
            services.Configure<HealthCheckServiceOptions>(o => o.Registrations.Clear());
        });
    }
}

public class PaymentApiTests(PaymentsApiFactory factory) : IClassFixture<PaymentsApiFactory>
{
    private static Uri Route(Guid serviceRequest) => new($"/v1/service-requests/{serviceRequest}/payment", UriKind.Relative);

    private HttpClient Client(Guid user, Guid tenant, string role = "cliente")
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Create(user, tenant, role));
        return client;
    }

    private static object Body(string token = "tok_sim_aprobada_abc") => new
    {
        paymentToken = token,
        billing = new { name = "Ana Gómez", documentType = "CC", documentNumber = "52123456", email = "ana@correo.com" },
    };

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private (Guid Tenant, Guid Client, Guid Request) Seed()
    {
        var (tenant, client) = (Guid.NewGuid(), Guid.NewGuid());
        lock (factory.Store)
        {
            return (tenant, client, factory.Store.SeedPending(tenant, client).ServiceRequestId);
        }
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task Salud_RespondeOk(string ruta)
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri(ruta, UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Consultar_Pendiente_YLuegoPagarAprobado_ConComprobante()
    {
        var (tenant, user, request) = Seed();
        using var http = Client(user, tenant);

        using var pending = await http.GetAsync(Route(request));
        Assert.Equal(HttpStatusCode.OK, pending.StatusCode);
        Assert.Equal("pendiente", (await Json(pending)).GetProperty("status").GetString());

        using var paid = await http.PostAsJsonAsync(Route(request), Body());
        Assert.Equal(HttpStatusCode.OK, paid.StatusCode);
        var body = await Json(paid);
        Assert.Equal("aprobado", body.GetProperty("status").GetString());
        Assert.Equal("185000.00", body.GetProperty("amount").GetString());
        Assert.Equal(JsonValueKind.String, body.GetProperty("invoiceId").ValueKind);
        Assert.Equal(
            ["paymentId", "serviceRequestId", "status", "amount", "reasonCode", "invoiceId", "createdAt", "processedAt"],
            body.EnumerateObject().Select(p => p.Name));

        using var again = await http.PostAsJsonAsync(Route(request), Body());
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.EndsWith("/ya-pagado", (await Json(again)).GetProperty("type").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rechazado_200ConMotivo_YSinRespuesta_502_YLuegoEnCurso()
    {
        var (tenant, user, request) = Seed();
        using var http = Client(user, tenant);

        using var rejected = await http.PostAsJsonAsync(Route(request), Body("tok_sim_rechazada_abc"));
        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
        Assert.Equal("fondos_insuficientes", (await Json(rejected)).GetProperty("reasonCode").GetString());

        using var unavailable = await http.PostAsJsonAsync(Route(request), Body("tok_sim_error_abc"));
        Assert.Equal(HttpStatusCode.BadGateway, unavailable.StatusCode);
        Assert.EndsWith("/pasarela-sin-respuesta", (await Json(unavailable)).GetProperty("type").GetString(), StringComparison.Ordinal);

        using var inProgress = await http.PostAsJsonAsync(Route(request), Body());
        Assert.Equal(HttpStatusCode.Conflict, inProgress.StatusCode);
        Assert.EndsWith("/pago-en-curso", (await Json(inProgress)).GetProperty("type").GetString(), StringComparison.Ordinal);

        using var current = await http.GetAsync(Route(request));
        Assert.Equal("procesando", (await Json(current)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task SinPago_404AlConsultar_Y409AlPagar()
    {
        using var http = Client(Guid.NewGuid(), Guid.NewGuid());

        using var get = await http.GetAsync(Route(Guid.NewGuid()));
        using var post = await http.PostAsJsonAsync(Route(Guid.NewGuid()), Body());

        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.EndsWith("/pago-no-disponible", (await Json(get)).GetProperty("type").GetString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Conflict, post.StatusCode);
        Assert.EndsWith("/pago-no-disponible", (await Json(post)).GetProperty("type").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OtroCliente_404_OtroTenant_404_YConEmpresaContacto_TambienSoloElDueno()
    {
        var (tenant, _, request) = Seed();
        using var otherClient = Client(Guid.NewGuid(), tenant);
        using var otherTenant = Client(Guid.NewGuid(), Guid.NewGuid());
        using var companyContact = Client(Guid.NewGuid(), tenant, "empresa_contacto");

        using var a = await otherClient.GetAsync(Route(request));
        using var b = await otherClient.PostAsJsonAsync(Route(request), Body());
        using var c = await otherTenant.GetAsync(Route(request));
        using var d = await companyContact.PostAsJsonAsync(Route(request), Body());

        Assert.Equal(HttpStatusCode.NotFound, a.StatusCode);
        Assert.EndsWith("/no-encontrado", (await Json(a)).GetProperty("type").GetString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, b.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, c.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, d.StatusCode);
    }

    [Fact]
    public async Task Tecnico_403_YSinToken_401()
    {
        var (tenant, _, request) = Seed();
        using var technician = Client(Guid.NewGuid(), tenant, "tecnico");
        using var anonymous = factory.CreateClient();

        using var forbidden = await technician.PostAsJsonAsync(Route(request), Body());
        using var unauthorized = await anonymous.GetAsync(Route(request));

        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
    }

    [Fact]
    public async Task DatosInvalidos_400PorCampo_YCamposDesconocidos_400()
    {
        var (tenant, user, request) = Seed();
        using var http = Client(user, tenant);

        using var invalid = await http.PostAsJsonAsync(Route(request), new { paymentToken = "4242424242424242", billing = new { name = "A" } });
        using var extra = await http.PostAsync(
            Route(request), new StringContent("""{"paymentToken":"tok_sim_aprobada_1","billing":null,"cardNumber":"4242"}""", Encoding.UTF8, "application/json"));
        using var noBilling = await http.PostAsJsonAsync(Route(request), new { paymentToken = "tok_sim_aprobada_1" });

        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var errors = (await Json(invalid)).GetProperty("errors");
        Assert.True(errors.TryGetProperty("paymentToken", out _));
        Assert.True(errors.TryGetProperty("billing.email", out _));
        Assert.Equal(HttpStatusCode.BadRequest, extra.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noBilling.StatusCode);
        lock (factory.Store)
        {
            Assert.DoesNotContain(factory.Store.Payments, p => p.ServiceRequestId == request && p.Status != "pendiente");
        }
    }

    [Fact]
    public async Task TokenSinTenant_401()
    {
        using var http = factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Create(Guid.Empty, Guid.NewGuid(), "cliente"));

        using var get = await http.GetAsync(Route(Guid.NewGuid()));
        using var post = await http.PostAsJsonAsync(Route(Guid.NewGuid()), Body());

        Assert.Equal(HttpStatusCode.Unauthorized, get.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, post.StatusCode);
    }
}
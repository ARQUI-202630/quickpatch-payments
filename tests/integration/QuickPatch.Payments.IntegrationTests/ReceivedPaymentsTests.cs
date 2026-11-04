using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace QuickPatch.Payments.IntegrationTests;

/// <summary>
/// <c>GET /v1/payments/received</c> (payments 1.2.0, RF-25, SCRUM-501) contra PostgreSQL con RLS y el servicio como
/// <c>payments_app</c>: el técnico ve sus pagos y nada de otro técnico ni de otro tenant.
/// </summary>
[Collection(PaymentsDefinition.Name)]
public class ReceivedPaymentsTests(PaymentsEnvironment env)
{
    private static readonly Uri Route = new("/v1/payments/received", UriKind.Relative);

    private async Task CompletedAsync(Guid tenant, Guid technician, Guid request, string amount)
    {
        await env.PublishCompletedAsync(tenant, request, Guid.NewGuid(), amount, technician);
        await PaymentsEnvironment.WaitUntilAsync(
            async () => await env.ScalarAsync<long>("SELECT count(*) FROM payments WHERE service_request_id = @sr", request) == 1,
            "Payments crea el pago pendiente al consumir service-request.completed");
    }

    [Fact]
    public async Task Tecnico_VeSusPagos_YNoLosDeOtroTecnicoNiDeOtroTenant()
    {
        var (tenant, otherTenant) = (Guid.NewGuid(), Guid.NewGuid());
        var (technician, otherTechnician) = (Guid.NewGuid(), Guid.NewGuid());
        var (first, second) = (Guid.NewGuid(), Guid.NewGuid());
        await CompletedAsync(tenant, technician, first, "120000.00");
        await CompletedAsync(tenant, technician, second, "80000.00");
        await CompletedAsync(tenant, otherTechnician, Guid.NewGuid(), "50000.00");

        // El mismo técnico en otro tenant, con su propio pago: no debe mezclarse con los del primero.
        await CompletedAsync(otherTenant, technician, Guid.NewGuid(), "99000.00");

        using var http = env.Client(technician, tenant, "tecnico");
        using var response = await http.GetAsync(Route);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, body.GetProperty("totalItems").GetInt32());
        var items = body.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal([second, first], items.Select(i => i.GetProperty("serviceRequestId").GetGuid()));
        Assert.All(items, i =>
        {
            Assert.Equal(technician, i.GetProperty("technicianId").GetGuid());
            Assert.Equal("pendiente", i.GetProperty("status").GetString());
        });
        Assert.Equal("80000.00", items[0].GetProperty("amount").GetString());

        using var leak = env.Client(technician, otherTenant, "tecnico");
        var leakBody = await (await leak.GetAsync(Route)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, leakBody.GetProperty("totalItems").GetInt32());
        Assert.Equal("99000.00", leakBody.GetProperty("items")[0].GetProperty("amount").GetString());

        using var stranger = env.Client(Guid.NewGuid(), tenant, "tecnico");
        var strangerBody = await (await stranger.GetAsync(Route)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, strangerBody.GetProperty("totalItems").GetInt32());

        // Sin tenant en la sesión, payments_app no ve ningún pago (RLS).
        Assert.Equal(0L, await env.ScalarAsync<long>("SELECT count(*) FROM payments WHERE service_request_id = @sr", first, asApp: true));
    }

    [Fact]
    public async Task Cliente_403()
    {
        using var http = env.Client(Guid.NewGuid(), Guid.NewGuid(), "cliente");

        using var response = await http.GetAsync(Route);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
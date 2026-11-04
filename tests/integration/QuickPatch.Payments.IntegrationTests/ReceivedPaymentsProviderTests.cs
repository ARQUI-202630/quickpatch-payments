using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace QuickPatch.Payments.IntegrationTests;

/// <summary>
/// <c>GET /v1/payments/received</c> para el <c>proveedor</c> (payments 1.2.0, RF-25, SCRUM-501) contra PostgreSQL con RLS y Kafka
/// reales: Identity publica <c>technician.registered</c>, Payments guarda el equipo de cada proveedor (P-02) y el proveedor ve los
/// pagos de su equipo y nada de otro proveedor, de técnicos independientes ni de otro tenant.
/// </summary>
[Collection(PaymentsDefinition.Name)]
public class ReceivedPaymentsProviderTests(PaymentsEnvironment env)
{
    private static readonly Uri Route = new("/v1/payments/received", UriKind.Relative);

    private async Task RegisteredAsync(Guid tenant, Guid technician, Guid? provider, DateTimeOffset updatedAt, Guid? eventId = null)
    {
        await env.PublishRegisteredAsync(tenant, technician, provider, updatedAt, eventId);
        await PaymentsEnvironment.WaitUntilAsync(
            async () => await env.ScalarAsync<long>("SELECT count(*) FROM payment_technicians WHERE technician_id = @sr", technician) == 1,
            "Payments guarda al técnico al consumir technician.registered");
    }

    private async Task CompletedAsync(Guid tenant, Guid technician, Guid request, string amount)
    {
        await env.PublishCompletedAsync(tenant, request, Guid.NewGuid(), amount, technician);
        await PaymentsEnvironment.WaitUntilAsync(
            async () => await env.ScalarAsync<long>("SELECT count(*) FROM payments WHERE service_request_id = @sr", request) == 1,
            "Payments crea el pago pendiente al consumir service-request.completed");
    }

    private static async Task<JsonElement> GetAsync(HttpClient http, string query = "")
    {
        using var response = await http.GetAsync(new Uri($"/v1/payments/received{query}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Proveedor_VeLosPagosDeSuEquipo_YNoLosDeOtroProveedorNiDeIndependientesNiDeOtroTenant()
    {
        var (tenant, otherTenant) = (Guid.NewGuid(), Guid.NewGuid());
        var (provider, otherProvider) = (Guid.NewGuid(), Guid.NewGuid());
        var (member1, member2, otherMember, independent) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var t0 = DateTimeOffset.UtcNow;
        await RegisteredAsync(tenant, member1, provider, t0);
        await RegisteredAsync(tenant, member2, provider, t0);
        await RegisteredAsync(tenant, otherMember, otherProvider, t0);
        await RegisteredAsync(tenant, independent, null, t0);
        var (r1, r2, r3) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await CompletedAsync(tenant, member1, r1, "120000.00");
        await CompletedAsync(tenant, member2, r2, "80000.00");
        await CompletedAsync(tenant, member1, r3, "50000.00");
        await CompletedAsync(tenant, otherMember, Guid.NewGuid(), "70000.00");
        await CompletedAsync(tenant, independent, Guid.NewGuid(), "60000.00");

        // El mismo proveedor en otro tenant, con su propio equipo y pago: no se mezcla con el primero.
        var foreignMember = Guid.NewGuid();
        await RegisteredAsync(otherTenant, foreignMember, provider, t0);
        await CompletedAsync(otherTenant, foreignMember, Guid.NewGuid(), "99000.00");

        using var http = env.Client(provider, tenant, "proveedor");
        var all = await GetAsync(http);
        Assert.Equal(3, all.GetProperty("totalItems").GetInt32());
        Assert.Equal([r3, r2, r1], all.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("serviceRequestId").GetGuid()));
        Assert.All(all.GetProperty("items").EnumerateArray(), i => Assert.Contains(i.GetProperty("technicianId").GetGuid(), new[] { member1, member2 }));

        var one = await GetAsync(http, $"?technicianId={member1}");
        Assert.Equal(2, one.GetProperty("totalItems").GetInt32());
        foreach (var notMine in new[] { otherMember, independent, foreignMember })
        {
            Assert.Equal(0, (await GetAsync(http, $"?technicianId={notMine}")).GetProperty("totalItems").GetInt32());
        }

        using var other = env.Client(otherProvider, tenant, "proveedor");
        Assert.Equal(1, (await GetAsync(other)).GetProperty("totalItems").GetInt32());

        using var foreign = env.Client(provider, otherTenant, "proveedor");
        var foreignBody = await GetAsync(foreign);
        Assert.Equal(1, foreignBody.GetProperty("totalItems").GetInt32());
        Assert.Equal("99000.00", foreignBody.GetProperty("items")[0].GetProperty("amount").GetString());

        // El técnico independiente sigue viendo solo lo suyo, y sin tenant en la sesión payments_app no ve la proyección (RLS).
        using var own = env.Client(independent, tenant, "tecnico");
        Assert.Equal(1, (await GetAsync(own)).GetProperty("totalItems").GetInt32());
        Assert.Equal(0L, await env.ScalarAsync<long>("SELECT count(*) FROM payment_technicians WHERE technician_id = @sr", member1, asApp: true));
    }

    [Fact]
    public async Task EventoAnticuado_NoCambiaElProveedor_YUnoMasRecienteSi()
    {
        var tenant = Guid.NewGuid();
        var (first, second) = (Guid.NewGuid(), Guid.NewGuid());
        var technician = Guid.NewGuid();
        var t0 = DateTimeOffset.UtcNow;
        await RegisteredAsync(tenant, technician, first, t0);
        await CompletedAsync(tenant, technician, Guid.NewGuid(), "10000.00");

        // Llega tarde un evento más viejo con otro proveedor: se descarta, pero queda registrado.
        var old = Guid.NewGuid();
        await env.PublishRegisteredAsync(tenant, technician, second, t0.AddMinutes(-5), old);
        await PaymentsEnvironment.WaitUntilAsync(
            async () => await env.ScalarAsync<long>("SELECT count(*) FROM processed_events WHERE event_id = @sr", old) == 1,
            "el evento anticuado queda registrado");

        using var stillFirst = env.Client(first, tenant, "proveedor");
        using var notSecond = env.Client(second, tenant, "proveedor");
        Assert.Equal(1, (await GetAsync(stillFirst)).GetProperty("totalItems").GetInt32());
        Assert.Equal(0, (await GetAsync(notSecond)).GetProperty("totalItems").GetInt32());

        var newer = Guid.NewGuid();
        await env.PublishRegisteredAsync(tenant, technician, second, t0.AddMinutes(5), newer);
        await PaymentsEnvironment.WaitUntilAsync(
            async () => await env.ScalarAsync<Guid?>("SELECT provider_id FROM payment_technicians WHERE technician_id = @sr", technician) == second,
            "una versión más reciente cambia el proveedor");
        Assert.Equal(1, (await GetAsync(notSecond)).GetProperty("totalItems").GetInt32());
    }

    [Fact]
    public async Task EventoRepetido_SeAplicaUnaSolaVez_YMensajesInvalidosNoBloqueanLosSiguientes()
    {
        var tenant = Guid.NewGuid();
        var (provider, technician) = (Guid.NewGuid(), Guid.NewGuid());
        var eventId = Guid.NewGuid();
        var t0 = DateTimeOffset.UtcNow;

        await env.PublishRawAsync("technician.registered", technician.ToString(), "esto no es json");
        await env.PublishRegisteredAsync(tenant, technician, Guid.Empty, t0);          // proveedor vacío: fuera del contrato
        await env.PublishRegisteredAsync(tenant, technician, provider, t0, eventId);
        await env.PublishRegisteredAsync(tenant, technician, provider, t0, eventId);   // el mismo evento otra vez
        await PaymentsEnvironment.WaitUntilAsync(
            async () => await env.ScalarAsync<Guid?>("SELECT provider_id FROM payment_technicians WHERE technician_id = @sr", technician) == provider,
            "el mensaje válido se aplica después de los inválidos");

        // Un último mensaje distinto, posterior, prueba que el consumidor ya pasó por el duplicado.
        var sentinel = Guid.NewGuid();
        await env.PublishRegisteredAsync(tenant, technician, provider, t0.AddSeconds(5), sentinel);
        await PaymentsEnvironment.WaitUntilAsync(
            async () => await env.ScalarAsync<long>("SELECT count(*) FROM processed_events WHERE event_id = @sr", sentinel) == 1,
            "el consumidor sigue avanzando");

        Assert.Equal(1L, await env.ScalarAsync<long>("SELECT count(*) FROM processed_events WHERE event_id = @sr", eventId));
        Assert.Equal(1L, await env.ScalarAsync<long>("SELECT count(*) FROM payment_technicians WHERE technician_id = @sr", technician));
    }

    [Fact]
    public async Task Cliente_Sigue_403()
    {
        using var http = env.Client(Guid.NewGuid(), Guid.NewGuid(), "cliente");

        using var response = await http.GetAsync(Route);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
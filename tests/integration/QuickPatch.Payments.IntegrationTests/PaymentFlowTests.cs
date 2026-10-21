using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Confluent.Kafka;
using Confluent.Kafka.Admin;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Npgsql;

using QuickPatch.Payments.Infrastructure.Persistence;
using QuickPatch.Payments.UnitTests.Support;

using Testcontainers.Kafka;
using Testcontainers.PostgreSql;

namespace QuickPatch.Payments.IntegrationTests;

/// <summary>
/// PostgreSQL 16 y Kafka reales, con migraciones, roles del DD 10.2 y el servicio como <c>payments_app</c> (sin BYPASSRLS).
/// La conciliación corre cada 200 ms y toma los pagos sin respuesta enseguida.
/// </summary>
public sealed class PaymentsEnvironment : IAsyncLifetime
{
    public static readonly string[] Topics = ["service-request.completed", "payment.approved", "payment.rejected"];

    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:16-alpine").WithDatabase("db_payments").Build();
    private readonly KafkaContainer kafka = new KafkaBuilder("confluentinc/cp-kafka:7.7.1").Build();

    public string AdminConnectionString => postgres.GetConnectionString();

    public string AppConnectionString => new NpgsqlConnectionStringBuilder(AdminConnectionString)
    {
        Username = "payments_app",
        Password = "app-pruebas",
    }.ConnectionString;

    public string KafkaServers => kafka.GetBootstrapAddress();

    public WebApplicationFactory<Program> App { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(postgres.StartAsync(), kafka.StartAsync());
        await using (var db = new PaymentsDbContext(new DbContextOptionsBuilder<PaymentsDbContext>().UseNpgsql(AdminConnectionString).Options))
        {
            await db.Database.MigrateAsync();
        }

        await ExecuteAdminAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "db", "roles.sql")));
        await ExecuteAdminAsync("ALTER ROLE payments_app LOGIN PASSWORD 'app-pruebas';");
        using (var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = KafkaServers }).Build())
        {
            await admin.CreateTopicsAsync(Topics.Select(t => new TopicSpecification { Name = t, NumPartitions = 3, ReplicationFactor = 1 }));
        }

        App = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Payments", AppConnectionString);
            b.UseSetting("Kafka:BootstrapServers", KafkaServers);
            b.UseSetting("Kafka:ConsumerGroupId", "payments-pruebas");
            b.UseSetting("Outbox:PublisherRole", "payments_outbox");
            b.UseSetting("Outbox:PollInterval", "00:00:00.200");
            b.UseSetting("Reconciliation:SchedulerRole", "payments_scheduler");
            b.UseSetting("Reconciliation:Interval", "00:00:00.200");
            b.UseSetting("Reconciliation:MinimumAge", "00:00:00");
            b.UseSetting("Jwt:PublicKeyPem", TestTokens.PublicKeyPem);
        });
        _ = App.Server;
    }

    public async Task DisposeAsync()
    {
        if (App is not null)
        {
            await App.DisposeAsync();
        }

        await Task.WhenAll(postgres.DisposeAsync().AsTask(), kafka.DisposeAsync().AsTask());
    }

    public async Task ExecuteAdminAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Consulta como administrador o, con <paramref name="asApp"/>, como <c>payments_app</c> sin tenant en la sesión.</summary>
    public async Task<T?> ScalarAsync<T>(string sql, Guid serviceRequest, bool asApp = false)
    {
        await using var connection = new NpgsqlConnection(asApp ? AppConnectionString : AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("sr", serviceRequest);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)value;
    }

    public HttpClient Client(Guid user, Guid tenant, string role = "cliente")
    {
        var client = App.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Create(user, tenant, role));
        return client;
    }

    /// <summary>Publica <c>service-request.completed</c> como lo haría ServiceRequest.</summary>
    public async Task PublishCompletedAsync(Guid tenant, Guid serviceRequest, Guid client, string amount = "185000.00")
    {
        using var producer = new ProducerBuilder<string, string>(new ProducerConfig { BootstrapServers = KafkaServers }).Build();
        var value = JsonSerializer.Serialize(new
        {
            eventId = Guid.NewGuid(),
            eventType = "service-request.completed",
            eventVersion = 1,
            occurredAt = DateTimeOffset.UtcNow,
            correlationId = Guid.NewGuid(),
            tenantId = tenant,
            producer = "service-request-service",
            data = new
            {
                serviceRequestId = serviceRequest,
                clientId = client,
                companyId = (Guid?)null,
                technicianId = Guid.NewGuid(),
                quoteId = Guid.NewGuid(),
                totalAmount = amount,
                completedAt = DateTimeOffset.UtcNow,
            },
        });
        await producer.ProduceAsync("service-request.completed", new Message<string, string> { Key = serviceRequest.ToString(), Value = value });
    }

    /// <summary>Lee de Kafka el primer mensaje del topic con la clave dada.</summary>
    public JsonElement? ConsumeOne(string topic, Guid key)
    {
        using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = KafkaServers,
            GroupId = $"pruebas-{Guid.NewGuid()}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
        }).Build();
        consumer.Subscribe(topic);
        var limit = DateTime.UtcNow.AddSeconds(30);
        try
        {
            while (DateTime.UtcNow < limit)
            {
                var result = consumer.Consume(TimeSpan.FromSeconds(1));
                if (result?.Message.Key == key.ToString())
                {
                    using var doc = JsonDocument.Parse(result.Message.Value);
                    return doc.RootElement.Clone();
                }
            }

            return null;
        }
        finally
        {
            consumer.Close();
        }
    }

    public static async Task WaitUntilAsync(Func<Task<bool>> condition, string what)
    {
        var limit = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < limit)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"No se cumplió: {what}.");
    }
}

[CollectionDefinition(Name)]
public sealed class PaymentsDefinition : ICollectionFixture<PaymentsEnvironment>
{
    public const string Name = "payments-real";
}

[Collection(PaymentsDefinition.Name)]
public class PaymentFlowTests(PaymentsEnvironment env)
{
    private static Uri Route(Guid serviceRequest) => new($"/v1/service-requests/{serviceRequest}/payment", UriKind.Relative);

    private static object Body(string token) => new
    {
        paymentToken = token,
        billing = new { name = "Ana Gómez", documentType = "CC", documentNumber = "52123456", email = "ana@correo.com" },
    };

    private async Task<(Guid Tenant, Guid Client, Guid Request)> CompletedAsync()
    {
        var (tenant, client, request) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await env.PublishCompletedAsync(tenant, request, client);
        await PaymentsEnvironment.WaitUntilAsync(
            async () => await env.ScalarAsync<long>("SELECT count(*) FROM payments WHERE service_request_id = @sr", request) == 1,
            "Payments crea el pago pendiente al consumir service-request.completed");
        return (tenant, client, request);
    }

    [Fact]
    public async Task Salud_ListaConLaBase()
    {
        using var http = env.App.CreateClient();

        using var ready = await http.GetAsync(new Uri("/health/ready", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    [Fact]
    public async Task Completado_Pagar_Aprobado_ComprobanteYEventoConLaClaveDeLaSolicitud()
    {
        var (tenant, client, request) = await CompletedAsync();
        using var http = env.Client(client, tenant);

        using var pending = await http.GetAsync(Route(request));
        Assert.Equal("pendiente", (await pending.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());

        using var paid = await http.PostAsJsonAsync(Route(request), Body("tok_sim_aprobada_int"));
        Assert.Equal(HttpStatusCode.OK, paid.StatusCode);
        var body = await paid.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("aprobado", body.GetProperty("status").GetString());
        var invoiceId = body.GetProperty("invoiceId").GetGuid();

        var approved = env.ConsumeOne("payment.approved", request);
        Assert.NotNull(approved);
        Assert.Equal(tenant, approved.Value.GetProperty("tenantId").GetGuid());
        Assert.Equal("payments-service", approved.Value.GetProperty("producer").GetString());
        Assert.Equal(invoiceId, approved.Value.GetProperty("data").GetProperty("invoiceId").GetGuid());
        Assert.Equal("185000.00", approved.Value.GetProperty("data").GetProperty("amount").GetString());
        Assert.Equal(1L, await env.ScalarAsync<long>(
            "SELECT count(*) FROM invoices i JOIN payments p ON p.id = i.payment_id WHERE p.service_request_id = @sr", request));
    }

    [Fact]
    public async Task Rls_ElServicioSinTenantNoVePagos_YOtroTenantNoEncuentraLaSolicitud()
    {
        var (_, client, request) = await CompletedAsync();
        using var otherTenant = env.Client(client, Guid.NewGuid());

        using var response = await otherTenant.GetAsync(Route(request));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0L, await env.ScalarAsync<long>("SELECT count(*) FROM payments WHERE service_request_id = @sr", request, asApp: true));
    }

    [Fact]
    public async Task SinRespuesta_502_YLaConciliacionLoCierraComoRechazado_YElReintentoApruebaUnPagoNuevo()
    {
        var (tenant, client, request) = await CompletedAsync();
        using var http = env.Client(client, tenant);

        using var unavailable = await http.PostAsJsonAsync(Route(request), Body("tok_sim_error_int"));
        Assert.Equal(HttpStatusCode.BadGateway, unavailable.StatusCode);

        await PaymentsEnvironment.WaitUntilAsync(
            async () => await env.ScalarAsync<string>(
                "SELECT status FROM payments WHERE service_request_id = @sr ORDER BY created_at DESC LIMIT 1", request) == "rechazado",
            "la conciliación cierra el pago sin respuesta");
        var rejected = env.ConsumeOne("payment.rejected", request);
        Assert.Equal("pasarela_sin_respuesta", rejected!.Value.GetProperty("data").GetProperty("reasonCode").GetString());

        using var retry = await http.PostAsJsonAsync(Route(request), Body("tok_sim_aprobada_int"));
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(2L, await env.ScalarAsync<long>("SELECT count(*) FROM payments WHERE service_request_id = @sr", request));
    }

    [Fact]
    public async Task DosPagosALaVez_UnSoloCobro()
    {
        var (tenant, client, request) = await CompletedAsync();
        using var a = env.Client(client, tenant);
        using var b = env.Client(client, tenant);

        var responses = await Task.WhenAll(
            a.PostAsJsonAsync(Route(request), Body("tok_sim_aprobada_a")),
            b.PostAsJsonAsync(Route(request), Body("tok_sim_aprobada_b")));

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], responses.Select(r => r.StatusCode).Order());
        Assert.Equal(1L, await env.ScalarAsync<long>(
            "SELECT count(*) FROM payments WHERE service_request_id = @sr AND status = 'aprobado'", request));
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }
}
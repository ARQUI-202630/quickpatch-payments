using Microsoft.Extensions.Logging.Abstractions;

using QuickPatch.Payments.Application.Events;
using QuickPatch.Payments.Application.Payments;
using QuickPatch.Payments.UnitTests.Support;

namespace QuickPatch.Payments.UnitTests.Application;

public class TechnicianRegisteredHandlerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private readonly InMemoryPayments store = new();
    private readonly Guid tenant = Guid.NewGuid();
    private readonly Guid technician = Guid.NewGuid();
    private readonly Guid provider = Guid.NewGuid();

    private TechnicianRegisteredHandler Handler() =>
        new(store, store, store, new MutableClock(T0), NullLogger<TechnicianRegisteredHandler>.Instance);

    private EventEnvelope<TechnicianRegisteredData> Event(Guid? providerId, DateTimeOffset updatedAt, Guid? eventId = null, Guid? tenantId = null) =>
        new(eventId ?? Guid.NewGuid(), EventTypes.TechnicianRegistered, 1, updatedAt, Guid.NewGuid(), tenantId ?? tenant, "identity-service",
            new TechnicianRegisteredData(technician, Guid.NewGuid(), providerId, "pendiente", updatedAt));

    [Fact]
    public async Task Handle_TecnicoNuevo_GuardaSuProveedorYRegistraElEvento()
    {
        var outcome = await Handler().HandleAsync(Event(provider, T0), CancellationToken.None);

        Assert.Equal(TechnicianRegisteredOutcome.Applied, outcome);
        var row = Assert.Single(store.Team);
        Assert.Equal((technician, tenant, provider), (row.TechnicianId, row.TenantId, row.ProviderId));
        Assert.Single(store.ProcessedEvents);
    }

    [Fact]
    public async Task Handle_TecnicoIndependiente_QuedaSinProveedor()
    {
        await Handler().HandleAsync(Event(null, T0), CancellationToken.None);

        Assert.Null(Assert.Single(store.Team).ProviderId);
    }

    [Fact]
    public async Task Handle_MismoEventoDosVeces_SoloLoAplicaUnaVez()
    {
        var evento = Event(provider, T0);

        await Handler().HandleAsync(evento, CancellationToken.None);
        var outcome = await Handler().HandleAsync(evento, CancellationToken.None);

        Assert.Equal(TechnicianRegisteredOutcome.Duplicate, outcome);
        Assert.Single(store.Team);
        Assert.Single(store.ProcessedEvents);
    }

    [Fact]
    public async Task Handle_VersionMasReciente_Actualiza_YAtrasada_SeDescarta()
    {
        await Handler().HandleAsync(Event(null, T0), CancellationToken.None);

        Assert.Equal(TechnicianRegisteredOutcome.Applied, await Handler().HandleAsync(Event(provider, T0.AddMinutes(1)), CancellationToken.None));
        Assert.Equal(provider, store.Team.Single().ProviderId);
        Assert.Equal(TechnicianRegisteredOutcome.Stale, await Handler().HandleAsync(Event(null, T0.AddMinutes(-1)), CancellationToken.None));
        Assert.Equal(provider, store.Team.Single().ProviderId);
    }

    [Fact]
    public async Task Handle_SinEvento_LanzaExcepcion()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => Handler().HandleAsync(null!, CancellationToken.None));
    }
}
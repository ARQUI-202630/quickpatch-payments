using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using QuickPatch.Payments.Application.Abstractions;
using QuickPatch.Payments.Application.Events;
using QuickPatch.Payments.Application.Payments;
using QuickPatch.Payments.Domain.Payments;
using QuickPatch.Payments.Infrastructure.Providers;
using QuickPatch.Payments.UnitTests.Support;

namespace QuickPatch.Payments.UnitTests.Application;

public class PaymentHandlersTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 15, 0, 0, TimeSpan.Zero);

    private readonly InMemoryPayments store = new();
    private readonly MutableClock clock = new(Now);

    private PaymentSettlement Settlement() =>
        new(store, store, store, clock, NullLogger<PaymentSettlement>.Instance);

    private PayHandler Pay(IPaymentProvider? provider = null) =>
        new(store, store, provider ?? new SimulatedPaymentProvider(), Settlement(), clock, NullLogger<PayHandler>.Instance);

    private static PayCommand Command(Payment payment, Guid user, string token = "tok_sim_aprobada_abc") =>
        new(payment.TenantId, user, Guid.NewGuid(), payment.ServiceRequestId, token,
            Billing.Create(token, "Ana Gómez", "CC", "52123456", "ana@correo.com"));

    private static EventEnvelope<ServiceRequestCompletedData> Completed(Guid tenant, Guid serviceRequest, string amount = "185000.00", Guid? eventId = null) =>
        new(eventId ?? Guid.NewGuid(), EventTypes.ServiceRequestCompleted, 1, Now, Guid.NewGuid(), tenant, "service-request-service",
            new ServiceRequestCompletedData(serviceRequest, Guid.NewGuid(), null, Guid.NewGuid(), Guid.NewGuid(), amount, Now));

    private static JsonElement Data(OutboxMessage message) => JsonDocument.Parse(message.Payload).RootElement.GetProperty("data").Clone();

    [Fact]
    public async Task Completado_CreaElPagoPendiente_UnaSolaVez()
    {
        var handler = new ServiceRequestCompletedHandler(store, store, store, clock, NullLogger<ServiceRequestCompletedHandler>.Instance);
        var (tenant, request, eventId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        Assert.Equal(CompletedHandlingOutcome.Created, await handler.HandleAsync(Completed(tenant, request, eventId: eventId), CancellationToken.None));
        Assert.Equal(CompletedHandlingOutcome.Duplicate, await handler.HandleAsync(Completed(tenant, request, eventId: eventId), CancellationToken.None));
        Assert.Equal(CompletedHandlingOutcome.AlreadyExists, await handler.HandleAsync(Completed(tenant, request), CancellationToken.None));
        Assert.Equal(CompletedHandlingOutcome.Invalid, await handler.HandleAsync(Completed(tenant, Guid.NewGuid(), "-5"), CancellationToken.None));
        Assert.Equal(CompletedHandlingOutcome.Invalid, await handler.HandleAsync(Completed(tenant, Guid.NewGuid(), "abc"), CancellationToken.None));

        var payment = Assert.Single(store.Payments);
        Assert.Equal((tenant, request, PaymentStatus.Pending, 185000m), (payment.TenantId, payment.ServiceRequestId, payment.Status, payment.Amount));
    }

    [Fact]
    public async Task Consultar_PagoPropio_SinPago_YPagoAjeno()
    {
        var (tenant, client) = (Guid.NewGuid(), Guid.NewGuid());
        var payment = store.SeedPending(tenant, client);
        var handler = new GetPaymentHandler(store, store);

        var found = Assert.IsType<GetPaymentResult.Found>(await handler.HandleAsync(tenant, client, payment.ServiceRequestId, CancellationToken.None));
        Assert.Null(found.View.InvoiceId);
        Assert.IsType<GetPaymentResult.NotFound>(await handler.HandleAsync(tenant, Guid.NewGuid(), payment.ServiceRequestId, CancellationToken.None));
        Assert.IsType<GetPaymentResult.NotReady>(await handler.HandleAsync(tenant, client, Guid.NewGuid(), CancellationToken.None));

        // Otro tenant no ve el pago (RLS).
        Assert.IsType<GetPaymentResult.NotReady>(await handler.HandleAsync(Guid.NewGuid(), client, payment.ServiceRequestId, CancellationToken.None));
    }

    [Fact]
    public async Task PagarAprobado_EmiteComprobante_YPublicaApproved()
    {
        var (tenant, client) = (Guid.NewGuid(), Guid.NewGuid());
        var payment = store.SeedPending(tenant, client);

        var result = Assert.IsType<PayResult.Completed>(await Pay().HandleAsync(Command(payment, client), CancellationToken.None));

        Assert.Equal(PaymentStatus.Approved, result.View.Payment.Status);
        var invoice = Assert.Single(store.Invoices);
        Assert.Equal(invoice.Id, result.View.InvoiceId);
        Assert.Equal("Ana Gómez", invoice.PayerName);
        Assert.Equal(client, payment.AuthorizedByUserId);
        Assert.StartsWith("sim_txn_", payment.ProviderTokenRef, StringComparison.Ordinal);
        var message = Assert.Single(store.Outbox);
        Assert.Equal((EventTypes.PaymentApproved, payment.ServiceRequestId, tenant), (message.EventType, message.AggregateId, message.TenantId));
        var data = Data(message);
        Assert.Equal("185000.00", data.GetProperty("amount").GetString());
        Assert.Equal(invoice.Id, data.GetProperty("invoiceId").GetGuid());
        Assert.Equal(client, data.GetProperty("clientId").GetGuid());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("companyId").ValueKind);
        Assert.DoesNotContain("tok_sim", message.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PagarRechazado_PublicaRejected_YElReintentoAbreUnPagoNuevo()
    {
        var (tenant, client) = (Guid.NewGuid(), Guid.NewGuid());
        var payment = store.SeedPending(tenant, client);

        var rejected = Assert.IsType<PayResult.Completed>(
            await Pay().HandleAsync(Command(payment, client, "tok_sim_rechazada_x"), CancellationToken.None));
        Assert.Equal(PaymentStatus.Rejected, rejected.View.Payment.Status);
        Assert.Equal(RejectionReasons.InsufficientFunds, rejected.View.Payment.ReasonCode);
        Assert.Equal(EventTypes.PaymentRejected, Assert.Single(store.Outbox).EventType);
        Assert.Equal(RejectionReasons.InsufficientFunds, Data(store.Outbox[0]).GetProperty("reasonCode").GetString());
        Assert.Empty(store.Invoices);

        clock.Now = Now.AddMinutes(1);
        var retry = Assert.IsType<PayResult.Completed>(await Pay().HandleAsync(Command(payment, client), CancellationToken.None));

        Assert.NotEqual(payment.Id, retry.View.Payment.Id);
        Assert.Equal(PaymentStatus.Approved, retry.View.Payment.Status);
        Assert.Equal(2, store.Payments.Count);
        Assert.IsType<PayResult.AlreadyPaid>(await Pay().HandleAsync(Command(payment, client), CancellationToken.None));
    }

    [Fact]
    public async Task SinRespuesta_QuedaProcesando_YUnSegundoIntentoEstaEnCurso()
    {
        var (tenant, client) = (Guid.NewGuid(), Guid.NewGuid());
        var payment = store.SeedPending(tenant, client);

        Assert.IsType<PayResult.ProviderUnavailable>(await Pay().HandleAsync(Command(payment, client, "tok_sim_error_x"), CancellationToken.None));
        Assert.Equal(PaymentStatus.Processing, payment.Status);
        Assert.NotNull(payment.ProviderTokenRef);
        Assert.Empty(store.Outbox);

        Assert.IsType<PayResult.InProgress>(await Pay().HandleAsync(Command(payment, client), CancellationToken.None));
    }

    [Fact]
    public async Task LaPasarelaLanza_SeTrataComoSinRespuesta()
    {
        var (tenant, client) = (Guid.NewGuid(), Guid.NewGuid());
        var payment = store.SeedPending(tenant, client);

        Assert.IsType<PayResult.ProviderUnavailable>(await Pay(new ScriptedProvider()).HandleAsync(Command(payment, client), CancellationToken.None));
        Assert.Equal(PaymentStatus.Processing, payment.Status);
        Assert.Null(payment.ProviderTokenRef);
    }

    [Fact]
    public async Task SinPago_NoDisponible_YDeOtroCliente_NoEncontrado()
    {
        var (tenant, client) = (Guid.NewGuid(), Guid.NewGuid());
        var payment = store.SeedPending(tenant, client);
        var provider = new ScriptedProvider();

        Assert.IsType<PayResult.NotFound>(await Pay(provider).HandleAsync(Command(payment, Guid.NewGuid()), CancellationToken.None));
        var other = Command(payment, client) with { ServiceRequestId = Guid.NewGuid() };
        Assert.IsType<PayResult.NotReady>(await Pay(provider).HandleAsync(other, CancellationToken.None));
        Assert.Equal(0, provider.Charges);
    }

    [Fact]
    public async Task DosReintentosALaVez_ElIndiceDePagosAbiertosDejaUnoSolo()
    {
        var (tenant, client) = (Guid.NewGuid(), Guid.NewGuid());
        var payment = store.SeedPending(tenant, client);
        await Pay().HandleAsync(Command(payment, client, "tok_sim_rechazada_x"), CancellationToken.None);

        // Otra petición ya abrió el reintento y sigue procesando: este pago también es pendiente de la misma solicitud.
        store.Payments.Add(Payment.CreatePending(tenant, payment.ServiceRequestId, client, null, payment.TechnicianId, 1m, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));

        Assert.IsType<PayResult.InProgress>(await Pay().HandleAsync(Command(payment, client), CancellationToken.None));
    }

    [Fact]
    public async Task Liquidar_PagoInexistenteOYaCerrado_NoCambiaNada()
    {
        var (tenant, client) = (Guid.NewGuid(), Guid.NewGuid());
        var payment = store.SeedPending(tenant, client);
        await Pay().HandleAsync(Command(payment, client), CancellationToken.None);

        Assert.Null(await Settlement().ApplyAsync(tenant, Guid.NewGuid(), new ProviderResult.Approved("x"), Guid.NewGuid(), CancellationToken.None));
        var again = await Settlement().ApplyAsync(tenant, payment.Id, new ProviderResult.Rejected("x", "y"), Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(PaymentStatus.Approved, again!.Payment.Status);
        Assert.NotNull(again.InvoiceId);
        Assert.Single(store.Outbox);
    }

    [Fact]
    public async Task Conciliar_CierraComoRechazado_SaltaLosQueSiguenSinRespuesta_YLosQueFallan()
    {
        var (tenant, client) = (Guid.NewGuid(), Guid.NewGuid());
        var stuck = store.SeedPending(tenant, client);
        await Pay().HandleAsync(Command(stuck, client, "tok_sim_error_x"), CancellationToken.None);
        store.Pending.Add(new PendingReconciliation(tenant, stuck.Id));
        var provider = new ScriptedProvider { OnQuery = () => new ProviderResult.Unavailable(null) };
        ReconcilePaymentsHandler Reconcile(IPaymentProvider p) =>
            new(store, store, store, p, Settlement(), clock, NullLogger<ReconcilePaymentsHandler>.Instance);

        Assert.Equal(0, await Reconcile(provider).HandleAsync(CancellationToken.None));
        Assert.Equal(0, await Reconcile(new ScriptedProvider()).HandleAsync(CancellationToken.None));
        Assert.Equal(PaymentStatus.Processing, stuck.Status);

        Assert.Equal(1, await Reconcile(new SimulatedPaymentProvider()).HandleAsync(CancellationToken.None));
        Assert.Equal(PaymentStatus.Rejected, stuck.Status);
        Assert.Equal(RejectionReasons.ProviderUnavailable, stuck.ReasonCode);
        Assert.Equal(EventTypes.PaymentRejected, Assert.Single(store.Outbox).EventType);
    }

    [Fact]
    public async Task Conciliar_Aprobado_EmiteElComprobanteConLosDatosDeFacturacionGuardados()
    {
        var (tenant, client) = (Guid.NewGuid(), Guid.NewGuid());
        var stuck = store.SeedPending(tenant, client);
        await Pay().HandleAsync(Command(stuck, client, "tok_sim_error_x"), CancellationToken.None);
        store.Pending.Add(new PendingReconciliation(tenant, stuck.Id));
        var provider = new ScriptedProvider { OnQuery = () => new ProviderResult.Approved("ref") };

        var handler = new ReconcilePaymentsHandler(store, store, store, provider, Settlement(), clock, NullLogger<ReconcilePaymentsHandler>.Instance);

        Assert.Equal(1, await handler.HandleAsync(CancellationToken.None));
        Assert.Equal("Ana Gómez", Assert.Single(store.Invoices).PayerName);
    }

    [Fact]
    public async Task PasarelaSimulada_TokenDesconocido_Rechazado()
    {
        var result = await new SimulatedPaymentProvider().ChargeAsync(Guid.NewGuid(), "tok_otro", 1m, CancellationToken.None);

        var rejected = Assert.IsType<ProviderResult.Rejected>(result);
        Assert.Equal(SimulatedPaymentProvider.UnknownTokenReason, rejected.ReasonCode);
    }
}
using QuickPatch.Payments.Domain.Common;
using QuickPatch.Payments.Domain.Payments;

namespace QuickPatch.Payments.UnitTests.Domain;

public class PaymentDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 15, 0, 0, TimeSpan.Zero);

    private static Billing ValidBilling() => Billing.Create("tok_sim_aprobada_1234", "Ana Gómez", "CC", "52123456", "ana@correo.com");

    [Fact]
    public void CreatePending_PagoDeCliente_ConElMontoRedondeadoYSinDatosDeTarjeta()
    {
        var payment = Payment.CreatePending(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid(), 185000.004m, Now);

        Assert.Equal(PaymentStatus.Pending, payment.Status);
        Assert.Equal(Payment.PayerClient, payment.PayerType);
        Assert.Equal("185000.00", payment.AmountText);
        Assert.Null(payment.ProviderTokenRef);
        Assert.Null(payment.ProcessingStartedAt);
    }

    [Fact]
    public void CreatePending_ConEmpresa_EsPagoDeEmpresa()
    {
        var payment = Payment.CreatePending(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 10m, Now);

        Assert.Equal(Payment.PayerCompany, payment.PayerType);
    }

    [Fact]
    public void CreatePending_DatosInvalidos_Lanza()
    {
        Assert.Throws<ArgumentException>(() => Payment.CreatePending(Guid.Empty, Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid(), 1m, Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => Payment.CreatePending(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid(), 0m, Now));
    }

    [Fact]
    public void SoloElClienteDeLaSolicitudPuedePagar()
    {
        var client = Guid.NewGuid();
        var payment = Payment.CreatePending(Guid.NewGuid(), Guid.NewGuid(), client, null, Guid.NewGuid(), 10m, Now);

        Assert.True(payment.CanBePaidBy(client));
        Assert.False(payment.CanBePaidBy(Guid.NewGuid()));
        Assert.False(payment.CanBePaidBy(Guid.Empty));
    }

    [Fact]
    public void Procesar_Aprobar_YNoSePuedeCerrarDosVeces()
    {
        var user = Guid.NewGuid();
        var payment = Payment.CreatePending(Guid.NewGuid(), Guid.NewGuid(), user, null, Guid.NewGuid(), 10m, Now);

        payment.StartProcessing(user, ValidBilling().ToJson(), Now);
        Assert.Equal(PaymentStatus.Processing, payment.Status);
        Assert.Equal(user, payment.AuthorizedByUserId);
        Assert.Equal(Now, payment.ProcessingStartedAt);
        Assert.Throws<InvalidOperationException>(() => payment.StartProcessing(user, "{}", Now));

        Assert.True(payment.Approve("sim_txn_1", Now.AddSeconds(2)));
        Assert.Equal(PaymentStatus.Approved, payment.Status);
        Assert.Equal("sim_txn_1", payment.ProviderTokenRef);
        Assert.Equal(Now.AddSeconds(2), payment.ProcessedAt);
        Assert.False(payment.Approve("otra", Now));
        Assert.False(payment.Reject("otra", RejectionReasons.InsufficientFunds, Now));
    }

    [Fact]
    public void SinRespuesta_GuardaLaReferencia_YElRechazoLaConserva()
    {
        var user = Guid.NewGuid();
        var payment = Payment.CreatePending(Guid.NewGuid(), Guid.NewGuid(), user, null, Guid.NewGuid(), 10m, Now);
        payment.RecordProviderReference("ignorada-si-no-procesa");
        Assert.Null(payment.ProviderTokenRef);

        payment.StartProcessing(user, "{}", Now);
        payment.RecordProviderReference("sim_txn_2");
        payment.RecordProviderReference(null);
        Assert.True(payment.Reject(null, RejectionReasons.ProviderUnavailable, Now));

        Assert.Equal("sim_txn_2", payment.ProviderTokenRef);
        Assert.Equal(RejectionReasons.ProviderUnavailable, payment.ReasonCode);
    }

    [Fact]
    public void Reintento_SoloTrasUnRechazo_ConElMismoMontoClienteYTecnico()
    {
        var user = Guid.NewGuid();
        var payment = Payment.CreatePending(Guid.NewGuid(), Guid.NewGuid(), user, Guid.NewGuid(), Guid.NewGuid(), 99.5m, Now);
        Assert.Throws<InvalidOperationException>(() => payment.Retry(Now));
        payment.StartProcessing(user, "{}", Now);
        payment.Reject("r", RejectionReasons.InsufficientFunds, Now);

        var retry = payment.Retry(Now.AddMinutes(1));

        Assert.NotEqual(payment.Id, retry.Id);
        Assert.Equal(PaymentStatus.Pending, retry.Status);
        Assert.Equal((payment.ServiceRequestId, payment.ClientId, payment.CompanyId, payment.TechnicianId, payment.Amount),
            (retry.ServiceRequestId, retry.ClientId, retry.CompanyId, retry.TechnicianId, retry.Amount));
    }

    [Fact]
    public void Billing_Valido_SeNormaliza_YVaYVieneEnJson()
    {
        var billing = Billing.Create(" tok_sim_aprobada_1 ", "  Ana Gómez ", "NIT", "900123456-7", "ana@correo.com");

        Assert.Equal("Ana Gómez", billing.Name);
        Assert.Equal(billing, Billing.FromJson(billing.ToJson()));
        Assert.Null(Billing.FromJson(null));
        Assert.Null(Billing.FromJson(" "));
    }

    [Fact]
    public void Billing_Invalido_ErroresPorCampo()
    {
        var ex = Assert.Throws<DomainValidationException>(() => Billing.Create("4242424242424242", "A", "TI", "12", "sin-arroba"));

        Assert.Equal(
            ["billing.documentNumber", "billing.documentType", "billing.email", "billing.name", "paymentToken"],
            ex.Errors.Keys.Order());
    }

    [Fact]
    public void Comprobante_SoloDeUnPagoAprobado_ConNumeroUnicoYNitSoloParaEmpresas()
    {
        var user = Guid.NewGuid();
        var payment = Payment.CreatePending(Guid.NewGuid(), Guid.NewGuid(), user, null, Guid.NewGuid(), 185000m, Now);
        Assert.Throws<InvalidOperationException>(() => Invoice.Issue(payment, ValidBilling(), Now));
        payment.StartProcessing(user, "{}", Now);
        payment.Approve("r", Now);

        var persona = Invoice.Issue(payment, ValidBilling(), Now);
        var empresa = Invoice.Issue(payment, Billing.Create("tok_sim_aprobada_1", "Hogar SAS", "NIT", "900123456", "a@b.co"), Now);

        Assert.StartsWith("QP-20261021-", persona.InvoiceNumber, StringComparison.Ordinal);
        Assert.Equal(22, persona.InvoiceNumber.Length);
        Assert.Null(persona.PayerNit);
        Assert.Equal("900123456", empresa.PayerNit);
        Assert.Equal(185000m, persona.Amount);
        Assert.Equal(payment.TenantId, persona.TenantId);
        Assert.Null(persona.PdfUrl);
    }
}
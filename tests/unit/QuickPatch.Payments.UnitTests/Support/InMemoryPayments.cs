using QuickPatch.Payments.Application.Abstractions;
using QuickPatch.Payments.Domain.Payments;

namespace QuickPatch.Payments.UnitTests.Support;

/// <summary>
/// Puertos en memoria. Emula RLS (solo se ve el tenant de la transacción en curso) y el índice único de pagos abiertos
/// (RN-P5): si al confirmar hay dos pagos abiertos para una solicitud, descarta los nuevos y lanza el conflicto.
/// </summary>
public sealed class InMemoryPayments : ITenantUnitOfWork, IPaymentRepository, IProcessedEventRepository, IOutbox, IReconciliationSource
{
    private readonly Lock gate = new();
    private Guid? currentTenant;
    private readonly List<Payment> added = [];

    public List<Payment> Payments { get; } = [];

    public List<Invoice> Invoices { get; } = [];

    public List<OutboxMessage> Outbox { get; } = [];

    public HashSet<Guid> ProcessedEvents { get; } = [];

    public List<PendingReconciliation> Pending { get; } = [];

    public async Task<T> ExecuteAsync<T>(Guid tenantId, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("El tenant es obligatorio.", nameof(tenantId));
        }

        currentTenant = tenantId;
        try
        {
            var result = await work(cancellationToken);
            lock (gate)
            {
                var conflict = Payments.Concat(added)
                    .Where(p => p.Status is PaymentStatus.Pending or PaymentStatus.Processing)
                    .GroupBy(p => p.ServiceRequestId)
                    .Any(g => g.Count() > 1);
                if (conflict)
                {
                    added.Clear();
                    throw new OpenPaymentConflictException();
                }

                Payments.AddRange(added);
                added.Clear();
            }

            return result;
        }
        finally
        {
            currentTenant = null;
        }
    }

    private IEnumerable<Payment> Visible => Payments.Where(p => p.TenantId == currentTenant);

    public Task<Payment?> FindLatestForUpdateAsync(Guid serviceRequestId, CancellationToken cancellationToken) =>
        FindLatestAsync(serviceRequestId, cancellationToken);

    public Task<Payment?> FindLatestAsync(Guid serviceRequestId, CancellationToken cancellationToken) =>
        Task.FromResult(Visible.Where(p => p.ServiceRequestId == serviceRequestId).OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id).FirstOrDefault());

    public Task<Payment?> FindForUpdateAsync(Guid paymentId, CancellationToken cancellationToken) =>
        Task.FromResult(Visible.SingleOrDefault(p => p.Id == paymentId));

    public Task<bool> ExistsForServiceRequestAsync(Guid serviceRequestId, CancellationToken cancellationToken) =>
        Task.FromResult(Visible.Any(p => p.ServiceRequestId == serviceRequestId));

    public Task<Guid?> FindInvoiceIdAsync(Guid paymentId, CancellationToken cancellationToken) =>
        Task.FromResult(Invoices.Where(i => i.PaymentId == paymentId && i.TenantId == currentTenant).Select(i => (Guid?)i.Id).SingleOrDefault());

    public Task<(Invoice Invoice, Payment Payment)?> FindInvoiceAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        var invoice = Invoices.SingleOrDefault(i => i.PaymentId == paymentId && i.TenantId == currentTenant);
        var payment = Visible.SingleOrDefault(p => p.Id == paymentId);
        return Task.FromResult<(Invoice, Payment)?>(invoice is null || payment is null ? null : (invoice, payment));
    }

    public Task<(IReadOnlyList<Payment> Items, int TotalItems)> ListReceivedAsync(
        Guid technicianId, string? status, int page, int size, CancellationToken cancellationToken)
    {
        var matching = Visible
            .Where(p => p.TechnicianId == technicianId && p.Status != PaymentStatus.Rejected && (status is null || p.Status == status))
            .OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id)
            .ToList();
        var skip = (long)(page - 1) * size;
        IReadOnlyList<Payment> items = skip >= matching.Count ? [] : [.. matching.Skip((int)skip).Take(size)];
        return Task.FromResult((items, matching.Count));
    }

    public void Add(Payment payment) => added.Add(payment);

    public void AddInvoice(Invoice invoice) => Invoices.Add(invoice);

    public Task<bool> ExistsAsync(Guid eventId, CancellationToken cancellationToken) => Task.FromResult(ProcessedEvents.Contains(eventId));

    public void Add(Guid eventId, Guid tenantId, string eventType, DateTimeOffset processedAt) => ProcessedEvents.Add(eventId);

    public void Enqueue(OutboxMessage message) => Outbox.Add(message);

    public Task<IReadOnlyList<PendingReconciliation>> ListAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PendingReconciliation>>([.. Pending]);

    /// <summary>Crea un pago pendiente de 185.000 como si hubiera llegado <c>service-request.completed</c>.</summary>
    public Payment SeedPending(
        Guid tenant, Guid client, Guid? serviceRequest = null, DateTimeOffset? at = null, Guid? company = null, Guid? technician = null)
    {
        var payment = Payment.CreatePending(
            tenant, serviceRequest ?? Guid.NewGuid(), client, company, technician ?? Guid.NewGuid(), 185000m,
            at ?? DateTimeOffset.UtcNow.AddMinutes(-10));
        Payments.Add(payment);
        return payment;
    }

    /// <summary>Crea un pago aprobado con su comprobante, como lo deja el pago con la pasarela.</summary>
    public (Payment Payment, Invoice Invoice) SeedApproved(Guid tenant, Guid client, Billing? billing = null, DateTimeOffset? at = null)
    {
        var now = at ?? DateTimeOffset.UtcNow;
        var payer = billing ?? new Billing("Ana Gómez", "CC", "52123456", "ana@correo.com");
        var payment = SeedPending(tenant, client, at: now.AddMinutes(-5));
        payment.StartProcessing(client, payer.ToJson(), now.AddMinutes(-1));
        payment.Approve("sim_ref", now);
        var invoice = Invoice.Issue(payment, payer, now);
        Invoices.Add(invoice);
        return (payment, invoice);
    }
}

public sealed class MutableClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>Pasarela que falla con una excepción (red caída) o responde lo que se le indique.</summary>
public sealed class ScriptedProvider : IPaymentProvider
{
    public Func<ProviderResult>? OnCharge { get; set; }

    public Func<ProviderResult>? OnQuery { get; set; }

    public int Charges { get; private set; }

    public Task<ProviderResult> ChargeAsync(Guid paymentId, string paymentToken, decimal amount, CancellationToken cancellationToken)
    {
        Charges++;
        return Task.FromResult((OnCharge ?? throw new HttpRequestException("La pasarela no responde.")).Invoke());
    }

    public Task<ProviderResult> QueryAsync(Guid paymentId, string? providerReference, CancellationToken cancellationToken) =>
        Task.FromResult((OnQuery ?? throw new HttpRequestException("La pasarela no responde.")).Invoke());
}
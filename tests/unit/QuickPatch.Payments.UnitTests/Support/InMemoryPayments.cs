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

    public void Add(Payment payment) => added.Add(payment);

    public void AddInvoice(Invoice invoice) => Invoices.Add(invoice);

    public Task<bool> ExistsAsync(Guid eventId, CancellationToken cancellationToken) => Task.FromResult(ProcessedEvents.Contains(eventId));

    public void Add(Guid eventId, Guid tenantId, string eventType, DateTimeOffset processedAt) => ProcessedEvents.Add(eventId);

    public void Enqueue(OutboxMessage message) => Outbox.Add(message);

    public Task<IReadOnlyList<PendingReconciliation>> ListAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PendingReconciliation>>([.. Pending]);

    /// <summary>Crea un pago pendiente de 185.000 como si hubiera llegado <c>service-request.completed</c>.</summary>
    public Payment SeedPending(Guid tenant, Guid client, Guid? serviceRequest = null, DateTimeOffset? at = null)
    {
        var payment = Payment.CreatePending(
            tenant, serviceRequest ?? Guid.NewGuid(), client, null, Guid.NewGuid(), 185000m, at ?? DateTimeOffset.UtcNow.AddMinutes(-10));
        Payments.Add(payment);
        return payment;
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
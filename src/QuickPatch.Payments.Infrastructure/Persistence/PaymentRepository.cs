using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Npgsql;

using QuickPatch.Payments.Application.Abstractions;
using QuickPatch.Payments.Domain.Payments;
using QuickPatch.Payments.Infrastructure.Options;

namespace QuickPatch.Payments.Infrastructure.Persistence;

public sealed class PaymentRepository(PaymentsDbContext db) : IPaymentRepository
{
    public Task<Payment?> FindLatestForUpdateAsync(Guid serviceRequestId, CancellationToken cancellationToken) =>
        db.Payments
            .FromSql($"SELECT * FROM payments WHERE service_request_id = {serviceRequestId} ORDER BY created_at DESC, id DESC LIMIT 1 FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

    public Task<Payment?> FindLatestAsync(Guid serviceRequestId, CancellationToken cancellationToken) =>
        db.Payments.AsNoTracking()
            .Where(x => x.ServiceRequestId == serviceRequestId)
            .OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<Payment?> FindForUpdateAsync(Guid paymentId, CancellationToken cancellationToken) =>
        db.Payments.FromSql($"SELECT * FROM payments WHERE id = {paymentId} FOR UPDATE").SingleOrDefaultAsync(cancellationToken);

    public Task<bool> ExistsForServiceRequestAsync(Guid serviceRequestId, CancellationToken cancellationToken) =>
        db.Payments.AnyAsync(x => x.ServiceRequestId == serviceRequestId, cancellationToken);

    public Task<Guid?> FindInvoiceIdAsync(Guid paymentId, CancellationToken cancellationToken) =>
        db.Invoices.Where(x => x.PaymentId == paymentId).Select(x => (Guid?)x.Id).SingleOrDefaultAsync(cancellationToken);

    public async Task<(Invoice Invoice, Payment Payment)?> FindInvoiceAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        var invoice = await db.Invoices.AsNoTracking().SingleOrDefaultAsync(x => x.PaymentId == paymentId, cancellationToken);
        if (invoice is null)
        {
            return null;
        }

        return (invoice, await db.Payments.AsNoTracking().SingleAsync(x => x.Id == paymentId, cancellationToken));
    }

    public async Task<(IReadOnlyList<Payment> Items, int TotalItems)> ListReceivedAsync(
        Guid technicianId, string? status, int page, int size, CancellationToken cancellationToken)
    {
        // Usa ix_payments_technician_status (DD 5.12). RLS ya limita la consulta al tenant de la transacción.
        return await PageReceivedAsync(db.Payments.AsNoTracking().Where(p => p.TechnicianId == technicianId), status, page, size, cancellationToken);
    }

    public async Task<(IReadOnlyList<Payment> Items, int TotalItems)> ListReceivedForProviderAsync(
        Guid providerId, Guid? technicianId, string? status, int page, int size, CancellationToken cancellationToken)
    {
        // El equipo sale de la proyección payment_technicians (P-02); con technicianId, un técnico que no es del equipo no
        // coincide con ninguna fila y la lista queda vacía. RLS limita las dos tablas al tenant de la transacción.
        var team = db.PaymentTechnicians.AsNoTracking().Where(t => t.ProviderId == providerId);
        if (technicianId is { } id)
        {
            team = team.Where(t => t.TechnicianId == id);
        }

        return await PageReceivedAsync(
            db.Payments.AsNoTracking().Where(p => team.Any(t => t.TechnicianId == p.TechnicianId)), status, page, size, cancellationToken);
    }

    private static async Task<(IReadOnlyList<Payment> Items, int TotalItems)> PageReceivedAsync(
        IQueryable<Payment> query, string? status, int page, int size, CancellationToken cancellationToken)
    {
        query = status is null
            ? query.Where(p => p.Status == PaymentStatus.Pending || p.Status == PaymentStatus.Processing || p.Status == PaymentStatus.Approved)
            : query.Where(p => p.Status == status);

        var total = await query.CountAsync(cancellationToken);
        if (page - 1 > (total - 1) / size)
        {
            // Página más allá del final (también evita el desbordamiento de Skip con páginas enormes).
            return ([], total);
        }

        var items = await query
            .OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id)
            .Skip((page - 1) * size)
            .Take(size)
            .ToListAsync(cancellationToken);
        return (items, total);
    }

    public void Add(Payment payment) => db.Payments.Add(payment);

    public void AddInvoice(Invoice invoice) => db.Invoices.Add(invoice);
}
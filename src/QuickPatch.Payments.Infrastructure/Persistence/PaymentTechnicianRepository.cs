using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Npgsql;

using QuickPatch.Payments.Application.Abstractions;
using QuickPatch.Payments.Domain.Payments;
using QuickPatch.Payments.Infrastructure.Options;

namespace QuickPatch.Payments.Infrastructure.Persistence;

public sealed class PaymentTechnicianRepository(PaymentsDbContext db) : IPaymentTechnicianRepository
{
    public Task<PaymentTechnician?> FindAsync(Guid technicianId, CancellationToken cancellationToken) =>
        db.PaymentTechnicians.SingleOrDefaultAsync(x => x.TechnicianId == technicianId, cancellationToken);

    public void Add(PaymentTechnician technician) => db.PaymentTechnicians.Add(technician);
}
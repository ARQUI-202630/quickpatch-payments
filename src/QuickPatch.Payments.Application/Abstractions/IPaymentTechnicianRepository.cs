using QuickPatch.Payments.Application.Invoices;
using QuickPatch.Payments.Domain.Payments;

namespace QuickPatch.Payments.Application.Abstractions;

/// <summary>Proyección del equipo de cada proveedor (tabla <c>payment_technicians</c>, P-02, SCRUM-501).</summary>
public interface IPaymentTechnicianRepository
{
    Task<PaymentTechnician?> FindAsync(Guid technicianId, CancellationToken cancellationToken);

    void Add(PaymentTechnician technician);
}
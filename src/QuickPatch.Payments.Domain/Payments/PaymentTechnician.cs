namespace QuickPatch.Payments.Domain.Payments;

/// <summary>
/// Proyección local del equipo de un proveedor (tabla <c>payment_technicians</c>, P-02, SCRUM-501): de cada técnico guarda el
/// proveedor al que pertenece, si lo tiene. La alimenta el evento <c>technician.registered</c> (ADR-017) y sirve para que el
/// <c>proveedor</c> vea los pagos de su equipo en <c>GET /v1/payments/received</c> sin consultar a Identity (ADR-003).
/// </summary>
public sealed class PaymentTechnician
{
    public PaymentTechnician(Guid technicianId, Guid tenantId, Guid? providerId, DateTimeOffset updatedAt)
    {
        TechnicianId = technicianId;
        TenantId = tenantId;
        ProviderId = providerId;
        UpdatedAt = updatedAt;
    }

    public Guid TechnicianId { get; }

    public Guid TenantId { get; }

    /// <summary>El proveedor del técnico; <c>null</c> si es independiente.</summary>
    public Guid? ProviderId { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Aplica el estado recibido solo si es más reciente que el guardado; un evento atrasado o repetido se descarta (ADR-017).
    /// </summary>
    /// <returns><c>true</c> si la proyección cambió.</returns>
    public bool ApplyChange(Guid? providerId, DateTimeOffset updatedAt)
    {
        if (updatedAt <= UpdatedAt)
        {
            return false;
        }

        ProviderId = providerId;
        UpdatedAt = updatedAt;
        return true;
    }
}
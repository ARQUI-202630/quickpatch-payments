using QuickPatch.Payments.Domain.Payments;

namespace QuickPatch.Payments.UnitTests.Domain;

public class PaymentTechnicianTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ApplyChange_ConUnaVersionMasReciente_CambiaElProveedor()
    {
        var technician = new PaymentTechnician(Guid.NewGuid(), Guid.NewGuid(), null, T0);
        var provider = Guid.NewGuid();

        var changed = technician.ApplyChange(provider, T0.AddSeconds(1));

        Assert.True(changed);
        Assert.Equal(provider, technician.ProviderId);
        Assert.Equal(T0.AddSeconds(1), technician.UpdatedAt);
    }

    [Theory]
    [InlineData(-60)]
    [InlineData(0)]
    public void ApplyChange_ConUnaVersionAnticuadaOIgual_SeDescarta(int seconds)
    {
        var provider = Guid.NewGuid();
        var technician = new PaymentTechnician(Guid.NewGuid(), Guid.NewGuid(), provider, T0);

        var changed = technician.ApplyChange(null, T0.AddSeconds(seconds));

        Assert.False(changed);
        Assert.Equal(provider, technician.ProviderId);
        Assert.Equal(T0, technician.UpdatedAt);
    }
}
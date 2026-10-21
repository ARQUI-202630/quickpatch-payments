using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace QuickPatch.Payments.Infrastructure.Persistence;

/// <summary>Fábrica para <c>dotnet ef</c> (generar migraciones). No se conecta a ninguna base.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<PaymentsDbContext>
{
    public PaymentsDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<PaymentsDbContext>().UseNpgsql("Host=localhost;Database=db_payments").Options);
}
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;

namespace QuickPatch.Payments.Infrastructure.Persistence;

/// <summary>
/// Adopta un rol de la base dentro de la transacción en curso (<c>SET LOCAL ROLE</c>, DD 10.2). Vacío no cambia de rol
/// (desarrollo local con el dueño de las tablas).
/// </summary>
public static partial class SessionRole
{
    public static async Task SetAsync(DbContext db, string role, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (string.IsNullOrWhiteSpace(role))
        {
            return;
        }

        if (!RoleName().IsMatch(role))
        {
            throw new InvalidOperationException("El nombre del rol de la base no es válido.");
        }

#pragma warning disable EF1002 // El nombre del rol se validó con una expresión regular estricta; no admite parámetros.
        await db.Database.ExecuteSqlRawAsync($"SET LOCAL ROLE \"{role}\"", cancellationToken);
#pragma warning restore EF1002
    }

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$")]
    private static partial Regex RoleName();
}
using System.Security.Claims;

using Microsoft.AspNetCore.Authentication.JwtBearer;

using QuickPatch.Transversal.Security;

namespace QuickPatch.Payments.Api.Security;

/// <summary>
/// Contexto autenticado (SDD: <c>AuthenticatedContext</c>) leído de los claims del JWT emitido por Identity:
/// <c>sub</c> (userId), <c>tenant_id</c> (tenantId), <c>role</c> y, para <c>empresa_contacto</c>, <c>company_id</c>.
/// El tenant y la empresa nunca se toman del cuerpo ni de cabeceras.
/// </summary>
public sealed record AuthenticatedUser(Guid UserId, Guid TenantId, string Role, Guid? CompanyId = null)
{
    public const string SubjectClaim = JwtClaims.Subject;
    public const string TenantClaim = JwtClaims.Tenant;
    public const string RoleClaim = JwtClaims.Role;
    public const string CompanyClaim = JwtClaims.Company;
    public const string ClientRole = "cliente";
    public const string CompanyContactRole = "empresa_contacto";
    public const string TenantAdminRole = "admin_tenant";
    public const string TechnicianRole = "tecnico";
    public const string ProviderRole = "proveedor";

    /// <summary>Pago de la solicitud (contrato payments 1.0.0): el cliente y los usuarios de la empresa cliente.</summary>
    public const string PayerPolicy = "Pagador";

    /// <summary>Comprobante (contrato payments 1.1.0): el pagador y el administrador del tenant.</summary>
    public const string InvoiceReaderPolicy = "LectorComprobante";

    /// <summary>
    /// Pagos recibidos (contrato payments 1.2.0, RF-25): el técnico, los suyos, y el proveedor, los de su equipo (proyección
    /// <c>payment_technicians</c>, P-02).
    /// </summary>
    public const string ReceivedPaymentsReaderPolicy = "LectorPagosRecibidos";

    public bool IsTenantAdmin => Role == TenantAdminRole;

    public static bool TryFrom(ClaimsPrincipal principal, out AuthenticatedUser user)
    {
        ArgumentNullException.ThrowIfNull(principal);
        user = null!;
        if (!JwtClaims.TryRead(principal, out var userId, out var tenantId, out var role))
        {
            return false;
        }

        // La empresa solo cuenta para empresa_contacto (RN-P6); en otro rol se ignora aunque venga en el token.
        Guid? companyId = role == CompanyContactRole
            && Guid.TryParse(principal.FindFirstValue(CompanyClaim), out var company) && company != Guid.Empty
            ? company
            : null;
        user = new AuthenticatedUser(userId, tenantId, role, companyId);
        return true;
    }
}

public static class AuthenticationSetup
{
    public static IServiceCollection AddQuickPatchAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var settings = configuration.GetSection(JwtSettings.Section).Get<JwtSettings>() ?? new JwtSettings();

        services.AddQuickPatchJwtBearer(settings, TimeSpan.FromSeconds(30));

        services.AddAuthorizationBuilder()
            .AddPolicy(AuthenticatedUser.PayerPolicy, policy => policy.RequireRole(AuthenticatedUser.ClientRole, AuthenticatedUser.CompanyContactRole))
            .AddPolicy(
                AuthenticatedUser.InvoiceReaderPolicy,
                policy => policy.RequireRole(AuthenticatedUser.ClientRole, AuthenticatedUser.CompanyContactRole, AuthenticatedUser.TenantAdminRole))
            .AddPolicy(
                AuthenticatedUser.ReceivedPaymentsReaderPolicy,
                policy => policy.RequireRole(AuthenticatedUser.TechnicianRole, AuthenticatedUser.ProviderRole));
        services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationMiddlewareResultHandler, ForbiddenAuditHandler>();

        return services;
    }
}
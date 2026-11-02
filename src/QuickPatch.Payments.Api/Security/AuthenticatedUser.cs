using System.Security.Claims;
using System.Security.Cryptography;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace QuickPatch.Payments.Api.Security;

/// <summary>
/// Contexto autenticado (SDD: <c>AuthenticatedContext</c>) leído de los claims del JWT emitido por Identity:
/// <c>sub</c> (userId), <c>tenant_id</c> (tenantId), <c>role</c> y, para <c>empresa_contacto</c>, <c>company_id</c>.
/// El tenant y la empresa nunca se toman del cuerpo ni de cabeceras.
/// </summary>
public sealed record AuthenticatedUser(Guid UserId, Guid TenantId, string Role, Guid? CompanyId = null)
{
    public const string SubjectClaim = "sub";
    public const string TenantClaim = "tenant_id";
    public const string RoleClaim = "role";
    public const string CompanyClaim = "company_id";
    public const string ClientRole = "cliente";
    public const string CompanyContactRole = "empresa_contacto";
    public const string TenantAdminRole = "admin_tenant";
    public const string TechnicianRole = "tecnico";

    /// <summary>Pago de la solicitud (contrato payments 1.0.0): el cliente y los usuarios de la empresa cliente.</summary>
    public const string PayerPolicy = "Pagador";

    /// <summary>Comprobante (contrato payments 1.1.0): el pagador y el administrador del tenant.</summary>
    public const string InvoiceReaderPolicy = "LectorComprobante";

    /// <summary>
    /// Pagos recibidos (contrato payments 1.2.0, RF-25): el técnico. El proveedor queda pendiente de la proyección del
    /// equipo (P-02), que el DD todavía no define en Payments.
    /// </summary>
    public const string ReceivedPaymentsReaderPolicy = "LectorPagosRecibidos";

    public bool IsTenantAdmin => Role == TenantAdminRole;

    public static bool TryFrom(ClaimsPrincipal principal, out AuthenticatedUser user)
    {
        ArgumentNullException.ThrowIfNull(principal);
        user = null!;
        if (!Guid.TryParse(principal.FindFirstValue(SubjectClaim), out var userId) || userId == Guid.Empty
            || !Guid.TryParse(principal.FindFirstValue(TenantClaim), out var tenantId) || tenantId == Guid.Empty)
        {
            return false;
        }

        var role = principal.FindFirstValue(RoleClaim) ?? string.Empty;

        // La empresa solo cuenta para empresa_contacto (RN-P6); en otro rol se ignora aunque venga en el token.
        Guid? companyId = role == CompanyContactRole
            && Guid.TryParse(principal.FindFirstValue(CompanyClaim), out var company) && company != Guid.Empty
            ? company
            : null;
        user = new AuthenticatedUser(userId, tenantId, role, companyId);
        return true;
    }
}

/// <summary>Configuración del JWT (sección <c>Jwt</c>). Identity firma con RS256; este servicio solo tiene la llave pública.</summary>
public sealed class JwtSettings
{
    public const string Section = "Jwt";

    public string Issuer { get; set; } = "quickpatch-identity";

    public string Audience { get; set; } = "quickpatch";

    /// <summary>Llave pública RSA en formato PEM.</summary>
    public string PublicKeyPem { get; set; } = string.Empty;
}

public static class AuthenticationSetup
{
    public static IServiceCollection AddQuickPatchAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var settings = configuration.GetSection(JwtSettings.Section).Get<JwtSettings>() ?? new JwtSettings();

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = settings.Issuer,
                    ValidAudience = settings.Audience,
                    IssuerSigningKey = LoadPublicKey(settings.PublicKeyPem),
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                    NameClaimType = AuthenticatedUser.SubjectClaim,
                    RoleClaimType = AuthenticatedUser.RoleClaim,
                    ClockSkew = TimeSpan.FromSeconds(30),
                };
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(AuthenticatedUser.PayerPolicy, policy => policy.RequireRole(AuthenticatedUser.ClientRole, AuthenticatedUser.CompanyContactRole))
            .AddPolicy(
                AuthenticatedUser.InvoiceReaderPolicy,
                policy => policy.RequireRole(AuthenticatedUser.ClientRole, AuthenticatedUser.CompanyContactRole, AuthenticatedUser.TenantAdminRole))
            .AddPolicy(AuthenticatedUser.ReceivedPaymentsReaderPolicy, policy => policy.RequireRole(AuthenticatedUser.TechnicianRole));
        services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationMiddlewareResultHandler, ForbiddenAuditHandler>();

        return services;
    }

    private static RsaSecurityKey? LoadPublicKey(string pem)
    {
        if (string.IsNullOrWhiteSpace(pem))
        {
            // Sin llave no se valida ningún token: todas las peticiones protegidas responden 401 (falla cerrado).
            return null;
        }

#pragma warning disable CA2000 // La llave vive lo que vive la aplicación.
        var rsa = RSA.Create();
#pragma warning restore CA2000
        rsa.ImportFromPem(pem);
        return new RsaSecurityKey(rsa);
    }
}
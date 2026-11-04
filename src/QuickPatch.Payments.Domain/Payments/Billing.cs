using System.Text.Json;
using System.Text.RegularExpressions;

using QuickPatch.Payments.Domain.Common;

namespace QuickPatch.Payments.Domain.Payments;

/// <summary>Datos del pagador para el comprobante (RF-24, esquema <c>Billing</c> del contrato payments 1.0.0).</summary>
public sealed partial record Billing(string Name, string DocumentType, string DocumentNumber, string Email)
{
    public static readonly IReadOnlyList<string> DocumentTypes = ["CC", "CE", "NIT", "PP"];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Valida los datos y el token de la pasarela; los errores van por campo, como en el contrato.</summary>
    public static Billing Create(string? paymentToken, string? name, string? documentType, string? documentNumber, string? email)
    {
        var errors = new Dictionary<string, string[]>();
        var token = paymentToken?.Trim() ?? string.Empty;
        if (token.Length is < 8 or > 255 || !token.StartsWith("tok_", StringComparison.Ordinal))
        {
            errors["paymentToken"] = ["Debe ser el token de la pasarela (empieza por 'tok_')."];
        }

        var cleanName = name?.Trim() ?? string.Empty;
        if (cleanName.Length is < 2 or > 150)
        {
            errors["billing.name"] = ["Debe tener entre 2 y 150 caracteres."];
        }

        var type = documentType?.Trim() ?? string.Empty;
        if (!DocumentTypes.Contains(type))
        {
            errors["billing.documentType"] = ["Debe ser CC, CE, NIT o PP."];
        }

        var number = documentNumber?.Trim() ?? string.Empty;
        if (!DocumentNumberPattern().IsMatch(number))
        {
            errors["billing.documentNumber"] = ["Debe tener entre 4 y 20 letras, números o guiones."];
        }

        var cleanEmail = email?.Trim() ?? string.Empty;
        if (cleanEmail.Length > 254 || !EmailPattern().IsMatch(cleanEmail))
        {
            errors["billing.email"] = ["Debe ser un correo válido."];
        }

        if (errors.Count > 0)
        {
            throw new DomainValidationException(errors);
        }

        return new Billing(cleanName, type, number, cleanEmail);
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static Billing? FromJson(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<Billing>(json, Json);

    [GeneratedRegex("^[0-9A-Za-z-]{4,20}$")]
    private static partial Regex DocumentNumberPattern();

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailPattern();
}
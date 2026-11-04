using System.Globalization;
using System.Text;

using QuickPatch.Payments.Application.Abstractions;
using QuickPatch.Payments.Application.Invoices;

namespace QuickPatch.Payments.Infrastructure.Documents;

/// <summary>
/// PDF del comprobante (RF-24): una página A4 con el número, el pagador (con el NIT si facturó como empresa), el servicio y
/// el total, rotulada como comprobante sin validez fiscal (SRS P3, opción a). Usa las fuentes estándar del PDF (Helvetica)
/// con WinAnsiEncoding, así que no depende de fuentes del contenedor ni de librerías externas.
/// </summary>
public sealed class InvoicePdfRenderer : IInvoicePdfRenderer
{
    private const float Left = 56;
    private const float ValueColumn = 200;
    private const int ValueWidth = 62;
    private static readonly TimeSpan ColombiaOffset = TimeSpan.FromHours(-5);

    public byte[] Render(InvoiceDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var invoice = document.Invoice;
        var payment = document.Payment;
        var page = new PdfPage();

        // Banda superior con la identidad de QUICKPATCH.
        page.Fill(0.114f, 0.306f, 0.847f, 0, 762, 595, 80);
        page.Text("F2", 22, Left, 806, "QUICKPATCH", white: true);
        page.Text("F1", 12, Left, 786, "Comprobante de pago", white: true);
        page.Text("F2", 11, 340, 806, invoice.InvoiceNumber, white: true);
        page.Text("F1", 9, 340, 790, $"Emitido el {LocalTime(invoice.IssuedAt)}", white: true);
        page.Text("F1", 9, 340, 777, "Hora de Colombia (UTC-5)", white: true);

        // Rótulo fiscal.
        page.Fill(1f, 0.957f, 0.839f, Left, 712, 595 - (2 * Left), 34);
        page.Text("F2", 9, Left + 10, 732, "Documento sin validez fiscal.");
        page.Text("F1", 9, Left + 10, 720, "Es el comprobante del pago, no una factura electrónica validada por la DIAN.");

        var y = 680f;
        y = page.Section(y, "Pagador");
        y = page.Row(y, "Nombre o razón social", document.Payer.Name);
        y = page.Row(y, "Documento", $"{document.Payer.DocumentType} {document.Payer.DocumentNumber}");
        y = page.Row(y, "Correo", document.Payer.Email);

        y = page.Section(y - 10, "Servicio");
        y = page.Row(y, "Concepto", document.Concept);
        y = page.Row(y, "Pago", payment.Id.ToString());
        y = page.Row(y, "Medio de pago", "Tarjeta (token de la pasarela)");
        y = page.Row(y, "Fecha del pago", LocalTime(payment.ProcessedAt ?? invoice.IssuedAt));

        y -= 24;
        page.Fill(0.945f, 0.961f, 0.976f, Left, y - 18, 595 - (2 * Left), 44);
        page.Text("F2", 12, Left + 12, y, "Total pagado");
        page.Text("F2", 16, 340, y - 2, Money(invoice.Amount));

        page.Text("F1", 8, Left, 70, "QUICKPATCH emite este comprobante como plataforma por cada pago aprobado.", gray: true);
        page.Text("F1", 8, Left, 58, "En el MVP el cobro se hace con la pasarela simulada (ADR-009): no se mueve dinero real.", gray: true);
        return PdfFile.Write(page.Content, $"Comprobante {invoice.InvoiceNumber}");
    }

    /// <summary>Pesos colombianos con punto de miles: <c>$ 185.000 COP</c> (o con centavos si los hay).</summary>
    public static string Money(decimal amount)
    {
        var format = new NumberFormatInfo { NumberGroupSeparator = ".", NumberDecimalSeparator = ",", NumberGroupSizes = [3] };
        var text = amount == decimal.Truncate(amount) ? amount.ToString("#,0", format) : amount.ToString("#,0.00", format);
        return $"$ {text} {InvoiceDocument.Currency}";
    }

    private static string LocalTime(DateTimeOffset value) =>
        value.ToOffset(ColombiaOffset).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Operadores de una página: texto, rectángulos y filas de etiqueta y valor.</summary>
    private sealed class PdfPage
    {
        private readonly StringBuilder content = new();

        public string Content => content.ToString();

        public void Fill(float r, float g, float b, float x, float y, float width, float height) =>
            content.Append(CultureInfo.InvariantCulture, $"{r:0.###} {g:0.###} {b:0.###} rg {x:0.##} {y:0.##} {width:0.##} {height:0.##} re f\n");

        public void Text(string font, int size, float x, float y, string text, bool white = false, bool gray = false)
        {
            var color = white ? "1 1 1" : gray ? "0.42 0.45 0.5" : "0.07 0.09 0.15";
            content.Append(CultureInfo.InvariantCulture, $"BT {color} rg /{font} {size} Tf {x:0.##} {y:0.##} Td ({PdfFile.Escape(text)}) Tj ET\n");
        }

        public float Section(float y, string title)
        {
            Text("F2", 11, Left, y, title);
            content.Append(CultureInfo.InvariantCulture, $"0.82 0.84 0.87 RG 0.6 w {Left} {y - 6:0.##} m {595 - Left} {y - 6:0.##} l S\n");
            return y - 22;
        }

        public float Row(float y, string label, string value)
        {
            Text("F1", 9, Left, y, label, gray: true);
            foreach (var line in Wrap(value, ValueWidth))
            {
                Text("F1", 10, ValueColumn, y, line);
                y -= 14;
            }

            return y - 4;
        }

        private static IEnumerable<string> Wrap(string text, int width)
        {
            var line = new StringBuilder();
            foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                for (var rest = word; rest.Length > 0;)
                {
                    var room = line.Length == 0 ? width : width - line.Length - 1;
                    if (rest.Length <= room)
                    {
                        line.Append(line.Length == 0 ? string.Empty : " ").Append(rest);
                        rest = string.Empty;
                    }
                    else if (line.Length > 0)
                    {
                        yield return line.ToString();
                        line.Clear();
                    }
                    else
                    {
                        yield return rest[..width];
                        rest = rest[width..];
                    }
                }
            }

            yield return line.ToString();
        }
    }
}

/// <summary>Escribe un PDF 1.4 de una página con Helvetica y Helvetica-Bold (sin incrustar) y su tabla <c>xref</c>.</summary>
internal static class PdfFile
{
    public static byte[] Write(string pageContent, string title)
    {
        var stream = Encode(pageContent);
        byte[] contents = [.. Encode($"<< /Length {stream.Length} >>\nstream\n"), .. stream, .. Encode("\nendstream")];
        var objects = new List<byte[]>
        {
            Encode("<< /Type /Catalog /Pages 2 0 R >>"),
            Encode("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Encode("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 4 0 R /F2 5 0 R >> >> /Contents 6 0 R >>"),
            Encode("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"),
            Encode("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>"),
            contents,
            Encode($"<< /Title ({Escape(title)}) /Producer (QUICKPATCH Payments) >>"),
        };

        using var output = new MemoryStream();
        Append(output, "%PDF-1.4\n");
        output.Write([0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A]);
        var offsets = new List<long>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(output.Position);
            Append(output, $"{i + 1} 0 obj\n");
            output.Write(objects[i]);
            Append(output, "\nendobj\n");
        }

        var xref = output.Position;
        var table = new StringBuilder($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            table.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        }

        table.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R /Info {objects.Count} 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        Append(output, table.ToString());
        return output.ToArray();
    }

    /// <summary>Escapa un texto para un literal <c>( )</c>: barras y paréntesis, y quita los saltos de línea.</summary>
    public static string Escape(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("(", "\\(", StringComparison.Ordinal)
            .Replace(")", "\\)", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ');

    /// <summary>Latin-1, que coincide con WinAnsiEncoding en las letras del español; lo que no cabe se escribe como '?'.</summary>
    private static byte[] Encode(string text) =>
        [.. text.Select(c => c <= 'ÿ' && (c >= ' ' || c == '\n') ? (byte)c : (byte)'?')];

    private static void Append(MemoryStream output, string text) => output.Write(Encode(text));
}
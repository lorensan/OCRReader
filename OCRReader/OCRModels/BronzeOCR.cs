using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

/// <summary>
/// Bronze-level OCR: basic multi-pass Tesseract with line-by-line parsing.
/// </summary>
public class BronzeOCR : OcrBase
{
    public BronzeOCR(string tessDataPath = @"./tessdata") : base(tessDataPath) { }

    public override List<ReceiptItem> ProcessTicket(string imagePath)
    {
        var (text, _) = ExtractRawTextMultiPass(imagePath);
        string supermarket = ExtractSupermarket(text);
        return EnhancedBasicParse(text, supermarket);
    }

    private static List<ReceiptItem> EnhancedBasicParse(string text, string supermarket)
    {
        var items = new List<ReceiptItem>();
        var rawLines = text.Split('\n');
        string? pending = null;

        for (int i = 0; i < rawLines.Length; i++)
        {
            var line = rawLines[i].Trim();
            if (line.Length < 2) continue;

            if (IsSectionHeader(line)) { pending = null; continue; }

            // Pattern A: "N x ( PRICE )   TOTAL" — multi-quantity with total
            var qtyTotalMatch = Regex.Match(line,
                @"^(\d+)\s*[xX×]\s*\(?\s*([\d]+[,\.][\d]{2})\s*\)?\s+(-?[\d]+[,\.][\d]{2})\s*€?\s*$");
            if (qtyTotalMatch.Success)
            {
                if (decimal.TryParse(qtyTotalMatch.Groups[3].Value.Replace(',', '.'),
                    NumberStyles.Number, CultureInfo.InvariantCulture, out decimal total) && total > 0)
                {
                    string name = pending ?? $"Producto {qtyTotalMatch.Groups[1].Value}x";
                    items.Add(new ReceiptItem(supermarket, CleanProductName(name), total));
                    pending = null;
                }
                continue;
            }

            // Pattern B: "N x ( PRICE )" — multi-quantity without total
            var qtyMatch = Regex.Match(line,
                @"^(\d+)\s*[xX×]\s*\(?\s*([\d]+[,\.][\d]{2})\s*\)?");
            if (qtyMatch.Success && pending != null)
            {
                if (int.TryParse(qtyMatch.Groups[1].Value, out int qty) &&
                    decimal.TryParse(qtyMatch.Groups[2].Value.Replace(',', '.'),
                        NumberStyles.Number, CultureInfo.InvariantCulture, out decimal unit))
                {
                    items.Add(new ReceiptItem(supermarket, CleanProductName(pending), qty * unit));
                    pending = null;
                }
                continue;
            }

            // Pattern C: "PRODUCT   PRICE" (2+ spaces)
            var colMatch = Regex.Match(line,
                @"^(.+?)\s{2,}(-?[\d]+[,\.][\d]{2})\s*€?\s*$");
            if (colMatch.Success)
            {
                string name = colMatch.Groups[1].Value.Trim();
                if (!IsSkippable(name) &&
                    decimal.TryParse(colMatch.Groups[2].Value.Replace(',', '.'),
                        NumberStyles.Number, CultureInfo.InvariantCulture, out decimal price) &&
                    Math.Abs(price) > 0)
                {
                    items.Add(new ReceiptItem(supermarket, CleanProductName(name), price));
                    pending = null;
                    continue;
                }
            }

            // Pattern D: "PRODUCT PRICE" (single space, loose match)
            var looseMatch = Regex.Match(line,
                @"^(.+?)\s+([\d]{1,5}[,\.][\d]{2})\s*€?\s*$");
            if (looseMatch.Success)
            {
                string name = looseMatch.Groups[1].Value.Trim();
                if (!IsSkippable(name) && name.Length >= 2 &&
                    decimal.TryParse(looseMatch.Groups[2].Value.Replace(',', '.'),
                        NumberStyles.Number, CultureInfo.InvariantCulture, out decimal price) &&
                    price > 0 && price < 10000)
                {
                    items.Add(new ReceiptItem(supermarket, CleanProductName(name), price));
                    pending = null;
                    continue;
                }
            }

            // Pattern E: Standalone price (negative = discount)
            var standaloneMatch = Regex.Match(line, @"^(-?[\d]+[,\.][\d]{2})\s*€?\s*$");
            if (standaloneMatch.Success && pending != null)
            {
                if (decimal.TryParse(standaloneMatch.Groups[1].Value.Replace(',', '.'),
                        NumberStyles.Number, CultureInfo.InvariantCulture, out decimal price) &&
                    Math.Abs(price) > 0)
                {
                    items.Add(new ReceiptItem(supermarket, CleanProductName(pending), price));
                    pending = null;
                }
                continue;
            }

            // No price — keep as pending product
            pending = IsSkippable(line) ? null : line;
        }

        return items;
    }

    private static bool IsSectionHeader(string line)
    {
        var upper = line.ToUpperInvariant().Trim();
        if (SectionHeaders.Contains(upper)) return true;
        if (Regex.IsMatch(upper, @"^[\-=\*]{5,}$")) return true;
        if (Regex.IsMatch(upper, @"^\d{8,}$")) return true;
        // Detect category headers (uppercase, no price, short)
        if (upper.Length >= 3 && upper.Length <= 20 && !upper.Contains('€') &&
            !Regex.IsMatch(upper, @"[\d]+[,\.][\d]{2}"))
        {
            var words = upper.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length <= 2 && words.All(w => w.Length <= 15))
            {
                return CategoryKeywords.Any(kw => upper.Contains(kw));
            }
        }
        return false;
    }

    private static readonly HashSet<string> SectionHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "SUBTOTAL", "TOTAL", "TOTAL A PAGAR", "IMPORTE", "A PAGAR",
        "BASE IMPONIBLE", "IVA", "DESCUENTO", "DTO.", "AHORRO",
        "EFECTIVO", "TARJETA", "CAMBIO", "PAGO", "FACTURA",
        "DESCRIPCIÓN", "P. UNIT", "IMPORTE"
    };

    private static readonly string[] CategoryKeywords =
    [
        "FRESCOS", "ALIMENTACION", "PERFUMERIA", "HOGAR", "BEBIDAS",
        "LACTEOS", "CHARCUTERIA", "FRUTAS", "VERDURAS", "CARNES",
        "PESCADOS", "PANADERIA", "CONGELADOS", "LIMPIEZA", "DROGUERIA"
    ];

    private static string CleanProductName(string name)
    {
        name = Regex.Replace(name, @"^[^a-zA-Z0-9áéíóúñÁÉÍÓÚÑüÜ]+", "");
        name = Regex.Replace(name, @"[^a-zA-Z0-9áéíóúñÁÉÍÓÚÑüÜ%\-\(\)\.\/]+", " ");
        name = Regex.Replace(name, @"\s{2,}", " ");
        return name.Trim();
    }
}

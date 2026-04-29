using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Silver-level OCR: Tesseract with text cleaning, column detection,
/// and enhanced product-price parsing.
/// </summary>
public class SilverOCR : OcrBase
{
    public SilverOCR(string tessDataPath = @"./tessdata") : base(tessDataPath) { }

    public override List<ReceiptItem> ProcessTicket(string imagePath)
    {
        var (text, _) = ExtractRawTextMultiPass(imagePath);
        text = CleanOcrText(text);
        string supermarket = NormalizeSupermarket(text);
        return EnhancedParse(text, supermarket);
    }

    private static string CleanOcrText(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (c == '\n' || c == '\r') { sb.Append(c); continue; }
            if (c >= '\x20' && c <= '\x7E' || c >= '\xA0' && c <= '\xFF' || c == '€')
                sb.Append(c);
        }
        text = sb.ToString();
        text = text.Replace("}", ")").Replace("{", "(").Replace("[", "(").Replace("]", ")");
        // Replace '?' with '€' when it appears after a price number
        text = Regex.Replace(text, @"([\d]+[,\.][\d]{2})\s*\?", "$1€");
        text = Regex.Replace(text, @"[ \t]{3,}", "  ");
        text = Regex.Replace(text, @"(?<=\s)[^\w\d€\n](?=\s)", " ");
        return text;
    }

    private static string FixPriceNumerics(string line)
    {
        return Regex.Replace(line,
            @"([\dOlISBZ]+[,\.][\dOlISBZ]{2})\s*€?\s*$",
            m => m.Groups[1].Value.Replace('O', '0').Replace('o', '0')
                .Replace('l', '1').Replace('I', '1')
                .Replace('S', '5').Replace('s', '5')
                .Replace('B', '8').Replace('b', '8')
                .Replace('Z', '2').Replace('z', '2')
                + (m.Value.Contains('€') ? "€" : ""));
    }

    private static readonly Dictionary<string, string> SupermarketAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MERCAD0NA"] = "MERCADONA",
        ["MERCADON A"] = "MERCADONA",
        ["MERCAOONA"] = "MERCADONA",
        ["CARRE FOUR"] = "CARREFOUR",
        ["CARREF0UR"] = "CARREFOUR",
        ["CARREFQUR"] = "CARREFOUR",
        ["L1DL"] = "LIDL",
        ["LlDL"] = "LIDL",
        ["ALD1"] = "ALDI",
        ["D1A"] = "DIA",
        ["DlA"] = "DIA",
        ["ER0SKI"] = "EROSKI",
        ["EROSK1"] = "EROSKI",
        ["ALCAMP0"] = "ALCAMPO",
        ["EL CORTE INGLES"] = "EL CORTE INGLÉS",
        ["HIPERCQR"] = "HIPERCOR",
        ["HIPERC0R"] = "HIPERCOR",
        ["FAM1LY CASH"] = "FAMILY CASH",
    };

    private string NormalizeSupermarket(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines.Take(6))
        {
            string upper = line.Trim().ToUpperInvariant();
            foreach (string market in KnownSupermarkets)
                if (upper.Contains(market)) return market;
            foreach (var (alias, canonical) in SupermarketAliases)
                if (upper.Contains(alias.ToUpperInvariant())) return canonical;
        }
        foreach (var line in lines.Take(6))
        {
            string upper = line.Trim().ToUpperInvariant();
            if (upper.Length < 3) continue;
            foreach (string market in KnownSupermarkets)
            {
                if (upper.Length >= market.Length)
                    for (int i = 0; i <= upper.Length - market.Length; i++)
                        if (LevenshteinDistance(upper.Substring(i, market.Length), market) <= 2) return market;
                else if (LevenshteinDistance(upper, market) <= 3) return market;
            }
        }
        return lines.FirstOrDefault(l => l.Trim().Length > 3)?.Trim() ?? "DESCONOCIDO";
    }

    private static List<ReceiptItem> EnhancedParse(string text, string supermarket)
    {
        var items = new List<ReceiptItem>();
        var rawLines = text.Split('\n');
        string? pending = null;

        for (int i = 0; i < rawLines.Length; i++)
        {
            var raw = rawLines[i];
            var line = FixPriceNumerics(raw.Trim());
            if (line.Length < 2) continue;

            if (IsSectionHeader(line)) { pending = null; continue; }

            // Check for weight-based item pattern (current line is weight/kg info)
            var weightMatch = Regex.Match(line,
                @"^([\d]+[,\.]\d{3})\s*kg\s+([\d]+[,\.][\d]{2})\s*€/kg\s+(-?[\d]+[,\.][\d]{2})\s*€?\s*$");
            if (weightMatch.Success && pending != null)
            {
                if (decimal.TryParse(weightMatch.Groups[3].Value.Replace(',', '.'),
                    NumberStyles.Number, CultureInfo.InvariantCulture, out decimal totalPrice) &&
                    Math.Abs(totalPrice) > 0)
                {
                    items.Add(new ReceiptItem(supermarket, CleanProductName(pending), totalPrice));
                    pending = null;
                }
                continue;
            }

            // "N x ( PRICE )   TOTAL" pattern (quantity with unit price and total)
            var qtyTotalMatch = Regex.Match(line,
                @"^(\d+)\s*[xX×]\s*\(?\s*([\d]+[,\.][\d]{2})\s*\)?\s+(-?[\d]+[,\.][\d]{2})\s*€?\s*$");
            if (qtyTotalMatch.Success)
            {
                string name = pending ?? $"Producto {qtyTotalMatch.Groups[1].Value}x";
                if (decimal.TryParse(qtyTotalMatch.Groups[3].Value.Replace(',', '.'),
                    NumberStyles.Number, CultureInfo.InvariantCulture, out decimal total) &&
                    Math.Abs(total) > 0)
                {
                    items.Add(new ReceiptItem(supermarket, CleanProductName(name), total));
                    pending = null;
                }
                continue;
            }

            // "N x ( PRICE )" pattern
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

            // "N x PRICE   TOTAL" pattern (alternative format without parentheses)
            var qtyAltMatch = Regex.Match(line,
                @"^(\d+)\s*[xX×]\s+([\d]+[,\.][\d]{2})\s+(-?[\d]+[,\.][\d]{2})\s*€?\s*$");
            if (qtyAltMatch.Success)
            {
                string name = pending ?? $"Producto {qtyAltMatch.Groups[1].Value}x";
                if (decimal.TryParse(qtyAltMatch.Groups[3].Value.Replace(',', '.'),
                    NumberStyles.Number, CultureInfo.InvariantCulture, out decimal total) &&
                    Math.Abs(total) > 0)
                {
                    items.Add(new ReceiptItem(supermarket, CleanProductName(name), total));
                    pending = null;
                }
                continue;
            }

            // "PRODUCT   PRICE" (2+ spaces) - main column pattern
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

            // "PRODUCT PRICE" (single space, loose match)
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

            // Standalone price (can be negative for discounts)
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

            // No price — keep as pending product name
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

    private static readonly string[] CategoryKeywords =
    [
        "FRESCOS", "ALIMENTACION", "PERFUMERIA", "HOGAR", "BEBIDAS",
        "LACTEOS", "CHARCUTERIA", "FRUTAS", "VERDURAS", "CARNES",
        "PESCADOS", "PANADERIA", "CONGELADOS", "LIMPIEZA", "DROGUERIA"
    ];

    private static readonly HashSet<string> SectionHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "SUBTOTAL", "TOTAL", "TOTAL A PAGAR", "IMPORTE", "A PAGAR",
        "BASE IMPONIBLE", "IVA", "DESCUENTO", "DTO.", "AHORRO",
        "EFECTIVO", "TARJETA", "CAMBIO", "PAGO", "FACTURA"
    };

    private static string CleanProductName(string name)
    {
        name = Regex.Replace(name, @"^[^a-zA-Z0-9áéíóúñÁÉÍÓÚÑüÜ]+", "");
        name = Regex.Replace(name, @"[^a-zA-Z0-9áéíóúñÁÉÍÓÚÑüÜ%\-\(\)\.\/]+", " ");
        name = Regex.Replace(name, @"\s{2,}", " ");
        return name.Trim();
    }
}

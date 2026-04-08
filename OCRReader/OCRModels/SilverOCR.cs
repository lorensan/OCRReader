using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Silver-level OCR: improves Bronze output using character cleaning,
/// pattern recognition for product/price pairs, supermarket name
/// normalization, and heuristics to align prices with products
/// even when columns are misaligned.
/// </summary>
class SilverOCR : OcrBase
{
    public SilverOCR(string tessDataPath = @"./tessdata") : base(tessDataPath) { }

    public override List<ReceiptItem> ProcessTicket(string imagePath)
    {
        var (text, _) = ExtractRawText(imagePath);
        text = CleanOcrText(text);
        string supermarket = NormalizeSupermarket(text);
        return EnhancedParse(text, supermarket);
    }

    // ── Silver heuristic: OCR text cleaning ──────────────────────────────────

    private static string CleanOcrText(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (c == '\n' || c == '\r')
            {
                sb.Append(c);
                continue;
            }
            // Keep printable ASCII and extended Latin characters
            if (c >= '\x20' && c <= '\x7E' || c >= '\xA0' && c <= '\xFF' || c == '€')
                sb.Append(c);
        }
        text = sb.ToString();

        // Fix common OCR artifacts for punctuation
        text = text.Replace("}", ")");
        text = text.Replace("{", "(");
        text = text.Replace("[", "(");
        text = text.Replace("]", ")");

        // Collapse runs of 3+ spaces/tabs into exactly 2 (preserves column alignment intent)
        text = Regex.Replace(text, @"[ \t]{3,}", "  ");

        // Remove isolated single non-alphanumeric characters (OCR noise)
        text = Regex.Replace(text, @"(?<=\s)[^\w\d€\n](?=\s)", " ");

        return text;
    }

    // ── Silver heuristic: fix letter→digit misreads in price context ─────────

    private static string FixPriceNumerics(string line)
    {
        // Match trailing price-like tokens and fix common OCR letter→digit swaps
        return Regex.Replace(line,
            @"([\dOlISB]+[,\.][\dOlISB]{2})\s*€?\s*$",
            m =>
            {
                var s = m.Groups[1].Value
                    .Replace('O', '0')
                    .Replace('l', '1')
                    .Replace('I', '1')
                    .Replace('S', '5')
                    .Replace('B', '8');
                return s + (m.Value.Contains('€') ? "€" : "");
            });
    }

    // ── Silver heuristic: supermarket name normalization ─────────────────────

    private static readonly Dictionary<string, string> SupermarketAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MERCAD0NA"]      = "MERCADONA",
        ["MERCADON A"]     = "MERCADONA",
        ["MERCAOONA"]      = "MERCADONA",
        ["CARRE FOUR"]     = "CARREFOUR",
        ["CARREF0UR"]      = "CARREFOUR",
        ["CARREFQUR"]      = "CARREFOUR",
        ["L1DL"]           = "LIDL",
        ["LlDL"]           = "LIDL",
        ["ALD1"]           = "ALDI",
        ["D1A"]            = "DIA",
        ["ER0SKI"]         = "EROSKI",
        ["ALCAMP0"]        = "ALCAMPO",
        ["EL CORTE INGLES"] = "EL CORTE INGLÉS",
        ["HIPERCQR"]       = "HIPERCOR",
        ["FAM1LY CASH"]    = "FAMILY CASH",
        ["FAMILY  CASH"]   = "FAMILY CASH",
    };

    private string NormalizeSupermarket(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // Check first 6 lines for exact or alias matches
        foreach (var line in lines.Take(6))
        {
            string upper = line.Trim().ToUpperInvariant();

            // Exact match against known supermarkets
            foreach (string market in KnownSupermarkets)
                if (upper.Contains(market))
                    return market;

            // Alias match (common OCR misspellings)
            foreach (var (alias, canonical) in SupermarketAliases)
                if (upper.Contains(alias.ToUpperInvariant()))
                    return canonical;
        }

        // Fuzzy match: find the closest known supermarket within edit distance 3
        foreach (var line in lines.Take(6))
        {
            string upper = line.Trim().ToUpperInvariant();
            if (upper.Length < 3) continue;

            foreach (string market in KnownSupermarkets)
            {
                // Check if the line contains a substring close to the supermarket name
                if (upper.Length >= market.Length)
                {
                    for (int i = 0; i <= upper.Length - market.Length; i++)
                    {
                        string segment = upper.Substring(i, market.Length);
                        if (LevenshteinDistance(segment, market) <= 2)
                            return market;
                    }
                }
                else if (LevenshteinDistance(upper, market) <= 3)
                {
                    return market;
                }
            }
        }

        return lines.FirstOrDefault(l => l.Trim().Length > 3)?.Trim() ?? "DESCONOCIDO";
    }

    // ── Silver heuristic: enhanced parsing with price alignment ──────────────

    private static List<ReceiptItem> EnhancedParse(string text, string supermarket)
    {
        var items = new List<ReceiptItem>();
        var rawLines = text.Split('\n');
        string? pending = null;

        for (int idx = 0; idx < rawLines.Length; idx++)
        {
            var line = FixPriceNumerics(rawLines[idx].Trim());
            if (line.Length < 2) continue;

            // Pattern A: "N x ( PRICE )" — multi-quantity sub-line
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

            // Pattern B: "PRODUCT   PRICE" — same line with column separation
            var sameLineMatch = Regex.Match(line,
                @"^(?:\d+\s+)?(.+?)\s{2,}(-?[\d]+[,\.][\d]{2})\s*€?\s*$");
            if (sameLineMatch.Success)
            {
                string name = sameLineMatch.Groups[1].Value.Trim();
                string priceStr = sameLineMatch.Groups[2].Value.Replace(',', '.');
                if (!IsSkippable(name) &&
                    decimal.TryParse(priceStr,
                        NumberStyles.Number, CultureInfo.InvariantCulture, out decimal price) &&
                    price > 0)
                {
                    items.Add(new ReceiptItem(supermarket, CleanProductName(name), price));
                    pending = null;
                    continue;
                }
            }

            // Pattern B2: "PRODUCT PRICE" — single space before trailing price (misaligned columns)
            var looseMatch = Regex.Match(line,
                @"^(.+?)\s+([\d]+[,\.][\d]{2})\s*€?\s*$");
            if (looseMatch.Success)
            {
                string name = looseMatch.Groups[1].Value.Trim();
                string priceStr = looseMatch.Groups[2].Value.Replace(',', '.');
                if (!IsSkippable(name) && name.Length >= 3 &&
                    decimal.TryParse(priceStr,
                        NumberStyles.Number, CultureInfo.InvariantCulture, out decimal price) &&
                    price > 0 && price < 1000)
                {
                    items.Add(new ReceiptItem(supermarket, CleanProductName(name), price));
                    pending = null;
                    continue;
                }
            }

            // Pattern C: standalone price — associate with pending product
            var standaloneMatch = Regex.Match(line, @"^(-?[\d]+[,\.][\d]{2})\s*€?\s*$");
            if (standaloneMatch.Success && pending != null)
            {
                if (decimal.TryParse(standaloneMatch.Groups[1].Value.Replace(',', '.'),
                        NumberStyles.Number, CultureInfo.InvariantCulture, out decimal price) &&
                    price > 0)
                {
                    items.Add(new ReceiptItem(supermarket, CleanProductName(pending), price));
                    pending = null;
                }
                continue;
            }

            // Pattern D: price appears at end after product name with mixed separators
            var mixedMatch = Regex.Match(line,
                @"^(.+?)\s*[-–—]\s*([\d]+[,\.][\d]{2})\s*€?\s*$");
            if (mixedMatch.Success)
            {
                string name = mixedMatch.Groups[1].Value.Trim();
                string priceStr = mixedMatch.Groups[2].Value.Replace(',', '.');
                if (!IsSkippable(name) &&
                    decimal.TryParse(priceStr,
                        NumberStyles.Number, CultureInfo.InvariantCulture, out decimal price) &&
                    price > 0)
                {
                    items.Add(new ReceiptItem(supermarket, CleanProductName(name), price));
                    pending = null;
                    continue;
                }
            }

            // No price found — keep as pending product name
            pending = IsSkippable(line) ? null : line;
        }

        return items;
    }

    // ── Silver heuristic: clean product name artifacts ───────────────────────

    private static string CleanProductName(string name)
    {
        // Remove leading/trailing non-alphanumeric artifacts
        name = Regex.Replace(name, @"^[^a-zA-Z0-9áéíóúñÁÉÍÓÚÑ]+", "");
        name = Regex.Replace(name, @"[^a-zA-Z0-9áéíóúñÁÉÍÓÚÑ%\)]+$", "");

        // Collapse multiple spaces
        name = Regex.Replace(name, @"\s{2,}", " ");

        return name.Trim();
    }
}

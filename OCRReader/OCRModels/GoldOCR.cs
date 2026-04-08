using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Gold-level OCR: builds on Silver heuristics and adds deduplication of
/// repeated products, cross-validation of totals, fuzzy matching against
/// a known product catalog, and systematic OCR misread correction.
/// </summary>
class GoldOCR : OcrBase
{
    public GoldOCR(string tessDataPath = @"./tessdata") : base(tessDataPath) { }

    public override List<ReceiptItem> ProcessTicket(string imagePath)
    {
        var (text, _) = ExtractRawText(imagePath);

        // Silver-level cleaning
        text = CleanOcrText(text);
        text = CorrectMisreads(text);

        string supermarket = NormalizeSupermarket(text);
        var items = AdvancedParse(text, supermarket);

        // Gold-level post-processing
        items = DeduplicateItems(items);
        items = ValidateAgainstTotal(items, text);
        items = FuzzyMatchProducts(items);

        return items;
    }

    // ── Silver-level: OCR text cleaning ──────────────────────────────────────

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
            if (c >= '\x20' && c <= '\x7E' || c >= '\xA0' && c <= '\xFF' || c == '€')
                sb.Append(c);
        }
        text = sb.ToString();

        text = text.Replace("}", ")");
        text = text.Replace("{", "(");
        text = text.Replace("[", "(");
        text = text.Replace("]", ")");

        text = Regex.Replace(text, @"[ \t]{3,}", "  ");
        text = Regex.Replace(text, @"(?<=\s)[^\w\d€\n](?=\s)", " ");

        return text;
    }

    // ── Gold-level: systematic OCR misread correction ────────────────────────

    private static string CorrectMisreads(string text)
    {
        var lines = text.Split('\n');
        var corrected = new StringBuilder();

        foreach (var raw in lines)
        {
            var line = raw;

            // Fix letter→digit in price-like trailing tokens
            line = Regex.Replace(line,
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

            // Fix common OCR digraph misreads in product names (before the price)
            var priceIdx = Regex.Match(line, @"\s+[\d]+[,\.][\d]{2}\s*€?\s*$");
            if (priceIdx.Success)
            {
                string namePart = line[..priceIdx.Index];
                // "rn" → "m" only when it creates a known word fragment
                namePart = Regex.Replace(namePart, @"\brn(?=\w)", "m");
                // "0" → "O" in product name context (uppercase letters)
                namePart = Regex.Replace(namePart, @"(?<=[A-Z])0(?=[A-Z])", "O");
                // "1" → "I" or "l" in product name context
                namePart = Regex.Replace(namePart, @"(?<=[A-Z])1(?=[A-Z])", "I");
                line = namePart + line[priceIdx.Index..];
            }

            corrected.AppendLine(line);
        }

        return corrected.ToString();
    }

    // ── Silver-level: supermarket normalization ──────────────────────────────

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

        foreach (var line in lines.Take(6))
        {
            string upper = line.Trim().ToUpperInvariant();

            foreach (string market in KnownSupermarkets)
                if (upper.Contains(market))
                    return market;

            foreach (var (alias, canonical) in SupermarketAliases)
                if (upper.Contains(alias.ToUpperInvariant()))
                    return canonical;
        }

        foreach (var line in lines.Take(6))
        {
            string upper = line.Trim().ToUpperInvariant();
            if (upper.Length < 3) continue;

            foreach (string market in KnownSupermarkets)
            {
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

    // ── Gold-level: advanced parsing ─────────────────────────────────────────

    private static List<ReceiptItem> AdvancedParse(string text, string supermarket)
    {
        var items = new List<ReceiptItem>();
        var rawLines = text.Split('\n');
        string? pending = null;

        for (int idx = 0; idx < rawLines.Length; idx++)
        {
            var line = rawLines[idx].Trim();
            if (line.Length < 2) continue;

            // Pattern A: "N x ( PRICE )"
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

            // Pattern B: "PRODUCT   PRICE"
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

            // Pattern B2: loose single-space separation
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

            // Pattern C: standalone price
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

            // Pattern D: dash-separated "PRODUCT - PRICE"
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

            pending = IsSkippable(line) ? null : line;
        }

        return items;
    }

    // ── Gold heuristic: deduplicate repeated products ────────────────────────

    private static List<ReceiptItem> DeduplicateItems(List<ReceiptItem> items)
    {
        var result = new List<ReceiptItem>();
        var seenKeys = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            string normalized = item.Product.Trim().ToUpperInvariant();

            // Find an existing item with a very similar name (edit distance ≤ 2)
            string? matchKey = null;
            foreach (var key in seenKeys.Keys)
            {
                int maxDist = Math.Max(2, key.Length / 5);
                if (LevenshteinDistance(key, normalized) <= maxDist)
                {
                    matchKey = key;
                    break;
                }
            }

            if (matchKey != null)
            {
                // Merge: sum the prices (duplicate scan of same product)
                int idx = seenKeys[matchKey];
                result[idx] = result[idx] with { Price = result[idx].Price + item.Price };
            }
            else
            {
                seenKeys[normalized] = result.Count;
                result.Add(item);
            }
        }

        return result;
    }

    // ── Gold heuristic: validate item prices against detected total ──────────

    private static List<ReceiptItem> ValidateAgainstTotal(List<ReceiptItem> items, string text)
    {
        var (totalStr, _) = ExtractMetadata(text);
        if (totalStr == null || items.Count == 0)
            return items;

        if (!decimal.TryParse(totalStr.Replace(',', '.'),
                NumberStyles.Number, CultureInfo.InvariantCulture, out decimal total) || total <= 0)
            return items;

        decimal sum = items.Sum(i => i.Price);

        // If sum matches total within 5%, items are validated
        if (total > 0 && Math.Abs(sum - total) / total < 0.05m)
            return items;

        // If exactly one item is off by a factor of 10, correct it
        if (sum > total * 1.5m)
        {
            for (int i = 0; i < items.Count; i++)
            {
                decimal corrected = items[i].Price / 10m;
                decimal newSum = sum - items[i].Price + corrected;
                if (total > 0 && Math.Abs(newSum - total) / total < 0.05m)
                {
                    items[i] = items[i] with { Price = corrected };
                    return items;
                }
            }
        }

        return items;
    }

    // ── Gold heuristic: fuzzy match products against known catalog ───────────

    private static readonly string[] ProductCatalog =
    [
        "LECHE ENTERA", "LECHE DESNATADA", "LECHE SEMIDESNATADA",
        "PAN DE MOLDE", "PAN INTEGRAL", "PAN BARRA",
        "ACEITE OLIVA", "ACEITE GIRASOL",
        "ARROZ", "PASTA", "MACARRONES", "ESPAGUETIS",
        "TOMATE FRITO", "TOMATE TRITURADO", "TOMATE NATURAL",
        "JAMON SERRANO", "JAMON COCIDO", "JAMON YORK",
        "QUESO", "YOGUR", "YOGUR NATURAL", "YOGUR GRIEGO",
        "HUEVOS", "MANTEQUILLA", "MARGARINA",
        "POLLO", "TERNERA", "CERDO", "SALMON", "ATUN",
        "LECHUGA", "TOMATE", "CEBOLLA", "PATATA", "ZANAHORIA",
        "MANZANA", "PLATANO", "NARANJA", "LIMON", "PERA",
        "AGUA MINERAL", "REFRESCO", "ZUMO", "CERVEZA", "VINO",
        "CAFE", "AZUCAR", "SAL", "HARINA",
        "DETERGENTE", "SUAVIZANTE", "LEJIA",
        "PAPEL HIGIENICO", "SERVILLETAS",
        "GALLETAS", "CHOCOLATE", "CEREALES",
    ];

    private static List<ReceiptItem> FuzzyMatchProducts(List<ReceiptItem> items)
    {
        var result = new List<ReceiptItem>(items.Count);

        foreach (var item in items)
        {
            string upper = item.Product.ToUpperInvariant();
            string bestMatch = item.Product;
            int bestDist = int.MaxValue;

            foreach (var catalogEntry in ProductCatalog)
            {
                int dist = LevenshteinDistance(upper, catalogEntry);
                // Accept fuzzy match only if edit distance is small relative to the name length
                int threshold = Math.Max(2, catalogEntry.Length / 4);
                if (dist < bestDist && dist <= threshold)
                {
                    bestDist = dist;
                    bestMatch = catalogEntry;
                }
            }

            // Only replace if we found a close match (not the original)
            if (bestDist <= 3 && bestMatch != item.Product)
                result.Add(item with { Product = bestMatch });
            else
                result.Add(item);
        }

        return result;
    }

    // ── Shared: product name cleaning ────────────────────────────────────────

    private static string CleanProductName(string name)
    {
        name = Regex.Replace(name, @"^[^a-zA-Z0-9áéíóúñÁÉÍÓÚÑ]+", "");
        name = Regex.Replace(name, @"[^a-zA-Z0-9áéíóúñÁÉÍÓÚÑ%\)]+$", "");
        name = Regex.Replace(name, @"\s{2,}", " ");
        return name.Trim();
    }
}

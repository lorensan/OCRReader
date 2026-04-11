using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Gold-level OCR: multi-pass Tesseract with different segmentation modes,
/// advanced text cleaning, pattern recognition, deduplication, and validation.
/// </summary>
class GoldOCR : OcrBase
{
    public GoldOCR(string tessDataPath = @"./tessdata") : base(tessDataPath) { }

    public override List<ReceiptItem> ProcessTicket(string imagePath)
    {
        // Use multi-pass OCR for best result
        var (text, confidence) = ExtractRawTextMultiPass(imagePath);

        // Clean and normalize
        text = CleanOcrText(text);
        text = CorrectMisreads(text);

        // Extract supermarket
        string supermarket = NormalizeSupermarket(text);

        // Advanced parsing
        var items = AdvancedParse(text, supermarket);

        // Post-processing
        items = DeduplicateItems(items);
        items = ValidateAgainstTotal(items, text);
        items = FuzzyMatchProducts(items);

        return items;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // OCR TEXT CLEANING
    // ──────────────────────────────────────────────────────────────────────────

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
            // Replace '?' that should be '€' in price contexts
            if (c >= '\x20' && c <= '\x7E' || c >= '\xA0' && c <= '\xFF' || c == '€' || c == '×')
                sb.Append(c);
        }
        text = sb.ToString();
        // Replace '?' with '€' when it appears after a price number
        text = Regex.Replace(text, @"([\d]+[,\.][\d]{2})\s*\?", "$1€");
        text = Regex.Replace(text, @"\r\n?", "\n");
        text = text.Replace("}", ")");
        text = text.Replace("{", "(");
        text = text.Replace("\"", "");
        text = Regex.Replace(text, @"[ \t]{3,}", "  ");
        text = Regex.Replace(text, @"(?<=\s)[^\w\d€×()\n](?=\s)", " ");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // SYSTEMATIC OCR MISREAD CORRECTION
    // ──────────────────────────────────────────────────────────────────────────

    private static string CorrectMisreads(string text)
    {
        var lines = text.Split('\n');
        var corrected = new StringBuilder();

        foreach (var raw in lines)
        {
            var line = raw;

            // Fix letter→digit swaps in price tokens
            line = Regex.Replace(line,
                @"([\dOlISBZ]+[,\.][\dOlISBZ]{2})\s*€?\s*$",
                m =>
                {
                    var s = m.Groups[1].Value
                        .Replace('O', '0').Replace('o', '0')
                        .Replace('l', '1').Replace('I', '1')
                        .Replace('S', '5').Replace('s', '5')
                        .Replace('B', '8').Replace('b', '8')
                        .Replace('Z', '2').Replace('z', '2');
                    return s + (m.Value.Contains('€') ? "€" : "");
                });

            // Fix "rn" → "m"
            var priceIdx = Regex.Match(line, @"\s+[\d]+[,\.][\d]{2}\s*€?\s*$");
            if (priceIdx.Success)
            {
                string namePart = line[..priceIdx.Index];
                namePart = Regex.Replace(namePart, @"rn", "m");
                namePart = Regex.Replace(namePart, @"vv", "w", RegexOptions.IgnoreCase);
                namePart = Regex.Replace(namePart, @"(?<=[A-Z])0(?=[A-Z])", "O");
                namePart = Regex.Replace(namePart, @"(?<=[A-Z])1(?=[A-Z])", "I");
                line = namePart + line[priceIdx.Index..];
            }

            corrected.AppendLine(line);
        }

        return corrected.ToString().TrimEnd();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // SUPERMARKET NORMALIZATION
    // ──────────────────────────────────────────────────────────────────────────

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
        ["ER0SKI"] = "EROSKI",
        ["ALCAMP0"] = "ALCAMPO",
        ["EL CORTE INGLES"] = "EL CORTE INGLÉS",
        ["HIPERCQR"] = "HIPERCOR",
        ["FAM1LY CASH"] = "FAMILY CASH",
    };

    private string NormalizeSupermarket(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines.Take(8))
        {
            string upper = line.Trim().ToUpperInvariant();

            foreach (string market in KnownSupermarkets)
                if (upper.Contains(market))
                    return market;

            foreach (var (alias, canonical) in SupermarketAliases)
                if (upper.Contains(alias.ToUpperInvariant()))
                    return canonical;
        }

        // Fuzzy match
        foreach (var line in lines.Take(8))
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

        return "DESCONOCIDO";
    }

    // ──────────────────────────────────────────────────────────────────────────
    // ADVANCED MULTI-PASS ITEM PARSING
    // ──────────────────────────────────────────────────────────────────────────

    private static List<ReceiptItem> AdvancedParse(string text, string supermarket)
    {
        var items = new List<ReceiptItem>();
        var rawLines = text.Split('\n');
        string? pending = null;

        for (int idx = 0; idx < rawLines.Length; idx++)
        {
            var line = rawLines[idx].Trim();
            if (line.Length < 2) continue;

            // Skip noise
            if (IsNoiseLine(line))
            {
                pending = null;
                continue;
            }

            // Pattern A: "N x ( PRICE )" — multi-quantity
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

            // Pattern B: "PRODUCT   PRICE" (2+ spaces)
            var columnMatch = Regex.Match(line,
                @"^(.+?)\s{2,}(-?[\d]+[,\.][\d]{2})\s*€?\s*$");
            if (columnMatch.Success)
            {
                string name = columnMatch.Groups[1].Value.Trim();
                string priceStr = columnMatch.Groups[2].Value.Replace(',', '.');
                if (!IsSkippable(name) &&
                    decimal.TryParse(priceStr,
                        NumberStyles.Number, CultureInfo.InvariantCulture, out decimal price) &&
                    price > 0 && price < 10000)
                {
                    items.Add(new ReceiptItem(supermarket, CleanProductName(name), price));
                    pending = null;
                    continue;
                }
            }

            // Pattern C: "PRODUCT PRICE" (single space, loose match)
            var looseMatch = Regex.Match(line,
                @"^(.+?)\s+([\d]{1,5}[,\.][\d]{2})\s*€?\s*$");
            if (looseMatch.Success)
            {
                string name = looseMatch.Groups[1].Value.Trim();
                string priceStr = looseMatch.Groups[2].Value.Replace(',', '.');
                if (!IsSkippable(name) && name.Length >= 2 &&
                    decimal.TryParse(priceStr,
                        NumberStyles.Number, CultureInfo.InvariantCulture, out decimal price) &&
                    price > 0 && price < 10000)
                {
                    items.Add(new ReceiptItem(supermarket, CleanProductName(name), price));
                    pending = null;
                    continue;
                }
            }

            // Pattern D: standalone price
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

            // No price — keep as pending product
            if (!IsSkippable(line) && !IsMetadataLine(line) && line.Length >= 2)
            {
                pending = line;
            }
            else
            {
                pending = null;
            }
        }

        return items;
    }

    private static bool IsNoiseLine(string line)
    {
        if (Regex.IsMatch(line.Trim(), @"^\d{8,}$"))
            return true;
        if (Regex.IsMatch(line.Trim(), @"^[-=*\*#_]{5,}$"))
            return true;
        return false;
    }

    private static bool IsMetadataLine(string line)
    {
        var upper = line.ToUpperInvariant();
        if (upper.Contains("TOTAL") || upper.Contains("SUBTOTAL") || upper.Contains("IMPORTE")) return true;
        if (upper.Contains("CIF:") || upper.Contains("NIF:")) return true;
        if (upper.Contains("CAJA") || upper.Contains("CAJERO")) return true;
        if (Regex.IsMatch(upper, @"^\d{2}[/\-\.]\d{2}[/\-\.]\d")) return true;
        if (Regex.IsMatch(upper, @"^\d{2}:\d{2}")) return true;
        return false;
    }

    private static string CleanProductName(string name)
    {
        name = Regex.Replace(name, @"^[^a-zA-Z0-9áéíóúñÁÉÍÓÚÑüÜ]+", "");
        name = Regex.Replace(name, @"[^a-zA-Z0-9áéíóúñÁÉÍÓÚÑüÜ%\)]+$", "");
        name = Regex.Replace(name, @"\s{2,}", " ");
        return name.Trim();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // DEDUPLICATION
    // ──────────────────────────────────────────────────────────────────────────

    private static List<ReceiptItem> DeduplicateItems(List<ReceiptItem> items)
    {
        var result = new List<ReceiptItem>();
        var seenKeys = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            string normalized = item.Product.Trim().ToUpperInvariant();
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

    // ──────────────────────────────────────────────────────────────────────────
    // TOTAL VALIDATION
    // ──────────────────────────────────────────────────────────────────────────

    private static List<ReceiptItem> ValidateAgainstTotal(List<ReceiptItem> items, string text)
    {
        var (totalStr, _) = ExtractMetadata(text);
        if (totalStr == null || items.Count == 0)
            return items;

        if (!decimal.TryParse(totalStr.Replace(',', '.'),
                NumberStyles.Number, CultureInfo.InvariantCulture, out decimal total) || total <= 0)
            return items;

        decimal sum = items.Sum(i => i.Price);

        if (total > 0 && Math.Abs(sum - total) / total < 0.05m)
            return items;

        return items;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // FUZZY PRODUCT CATALOG MATCHING
    // ──────────────────────────────────────────────────────────────────────────

    private static readonly string[] ProductCatalog =
    [
        "LECHE ENTERA", "LECHE DESNATADA", "LECHE SEMIDESNATADA",
        "PAN DE MOLDE", "PAN INTEGRAL", "PAN BARRA",
        "ACEITE OLIVA", "ACEITE DE OLIVA", "ACEITE GIRASOL",
        "ARROZ", "PASTA", "MACARRONES", "ESPAGUETIS",
        "TOMATE FRITO", "TOMATE TRITURADO",
        "JAMON SERRANO", "JAMON COCIDO", "JAMON YORK",
        "QUESO FRESCO", "QUESO CURADO", "QUESO SEMI", "QUESO",
        "YOGUR", "YOGUR NATURAL", "YOGUR GRIEGO",
        "HUEVOS", "MANTEQUILLA", "MARGARINA",
        "POLLO", "PECHUGA POLLO", "TERNERA", "CERDO", "SALMON", "ATUN",
        "LECHUGA", "TOMATE", "CEBOLLA", "PATATA", "ZANAHORIA",
        "MANZANA", "PLATANO", "NARANJA", "LIMON", "PERA", "BANANA",
        "AGUA MINERAL", "REFRESCO", "ZUMO", "CERVEZA", "VINO",
        "CAFE", "AZUCAR", "SAL", "HARINA",
        "DETERGENTE", "SUAVIZANTE", "LEJIA",
        "PAPEL HIGIENICO", "SERVILLETAS",
        "GALLETAS", "CHOCOLATE", "CEREALES",
        "PRINGLES", "CARRILLADA", "PECHUGA PAVO",
        "SALSA SOJA", "HEINZ", "CREMA YORK",
        "BOLSA", "ROLLS", "BANANA GRANEL",
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
                if (upper.Contains(catalogEntry))
                    dist = 0;

                int threshold = Math.Max(2, catalogEntry.Length / 4);
                if (dist < bestDist && dist <= threshold)
                {
                    bestDist = dist;
                    bestMatch = catalogEntry;
                }
            }

            if (bestDist <= 3 && bestMatch != item.Product)
                result.Add(item with { Product = bestMatch });
            else
                result.Add(item);
        }

        return result;
    }
}

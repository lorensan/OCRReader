/// <summary>
/// Bronze-level OCR: basic multi-pass Tesseract with line-by-line parsing.
/// </summary>
public class BronzeOCR : OcrBase
{
    public BronzeOCR(string tessDataPath = @"./tessdata") : base(tessDataPath) { }

    public override List<ReceiptItem> ProcessTicket(string imagePath)
    {
        var (text, confidence) = ExtractRawTextMultiPass(imagePath);
        string supermarket = ExtractSupermarket(text);
        
        // Detect if this is a table-based receipt (like Dia's)
        if (IsTableFormat(text))
        {
            return ParseTableFormat(text, supermarket, confidence);
        }
        
        return BasicParse(text, supermarket, confidence);
    }

    /// <summary>
    /// Detect table-format receipts by looking for table headers
    /// </summary>
    private static bool IsTableFormat(string text)
    {
        var upper = text.ToUpperInvariant();
        return upper.Contains("DESCRIPCIÓN") || 
               upper.Contains("CANTIDAD") || 
               upper.Contains("PRODUCTOS VENDIDOS");
    }

    /// <summary>
    /// Parse table-format receipts with enhanced pattern matching
    /// </summary>
    private static List<ReceiptItem> ParseTableFormat(string text, string supermarket, double confidence = 0.80)
    {
        var items = new List<ReceiptItem>();
        var lines = text.Split('\n');
        string? pendingName = null;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length < 2) continue;

            // Skip headers and metadata
            if (IsTableHeader(line) || IsMetadataLine(line))
            {
                pendingName = null;
                continue;
            }

            // Pattern 1: "PRODUCT  QTY  PRICE  TOTAL  VAT" (full table row)
            // Match the LAST price on the line (the total column)
            var fullRowMatch = System.Text.RegularExpressions.Regex.Match(line,
                @"^([A-ZÁÉÍÓÚÑÜ][A-ZÁÉÍÓÚÑÜ\s\&\.\-\%\d'\(\)]{3,}?)\s+(\d[\d\,\.]*\s*(?:ud|kg|u\.?d\.?)?)\s+[\d]+[,\.]?[\d]{2}\s*€?\s+([\d]+[,\.]?[\d]{2})\s*€",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (fullRowMatch.Success)
            {
                string name = fullRowMatch.Groups[1].Value.Trim();
                string totalStr = fullRowMatch.Groups[3].Value; // Group 3 is now the total (last price)

                // Fix OCR errors: insert comma if missing (e.g., "099" → "0,99")
                if (!totalStr.Contains(',') && !totalStr.Contains('.'))
                {
                    if (totalStr.Length >= 3)
                        totalStr = totalStr.Insert(totalStr.Length - 2, ",");
                }

                if (!IsSkippable(name) && name.Length >= 3 &&
                    decimal.TryParse(totalStr.Replace(',', '.'),
                        System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out decimal price) &&
                    price > 0 && price < 10000)
                {
                    items.Add(new ReceiptItem(supermarket, name, price, confidence * 0.9));
                    pendingName = null;
                    continue;
                }
            }

            // Pattern 1b: Table row with weight-based pricing (e.g., "BANANA  1,550kg  1,11 €/kg  1,72€ A")
            var weightMatch = System.Text.RegularExpressions.Regex.Match(line,
                @"^([A-ZÁÉÍÓÚÑÜ][A-ZÁÉÍÓÚÑÜ\s\&\.\-\%'\(\)]{3,}?)\s+(\d[\d\,\.]*\s*kg)\s+[\d]+[,\.]?[\d]{2}\s*€?/kg\s+([\d]+[,\.]?[\d]{2})\s*€");
            if (weightMatch.Success)
            {
                string name = weightMatch.Groups[1].Value.Trim();
                string totalStr = weightMatch.Groups[3].Value;

                if (!totalStr.Contains(',') && !totalStr.Contains('.'))
                {
                    if (totalStr.Length >= 3)
                        totalStr = totalStr.Insert(totalStr.Length - 2, ",");
                }

                if (!IsSkippable(name) && name.Length >= 3 &&
                    decimal.TryParse(totalStr.Replace(',', '.'),
                        System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out decimal price) &&
                    price > 0 && price < 10000)
                {
                    items.Add(new ReceiptItem(supermarket, name, price, confidence * 0.9));
                    pendingName = null;
                    continue;
                }
            }

            // Pattern 1c: Multi-line product (name on one line, details on next)
            // Matches: "TOSTADAS                1ud      1,15€     115€ A"
            var multiLineMatch = System.Text.RegularExpressions.Regex.Match(line,
                @"^([A-ZÁÉÍÓÚÑÜ][A-ZÁÉÍÓÚÑÜ\s\&\.\-\%'\(\)]{3,}?)\s+(\d[\d\,\.]*\s*(?:ud|kg)?)\s+[\d]+[,\.]?[\d]{2}\s*€?\s+([\d]+[,\.]?[\d]{2})\s*€");
            if (multiLineMatch.Success)
            {
                string name = multiLineMatch.Groups[1].Value.Trim();
                string totalStr = multiLineMatch.Groups[3].Value;

                if (!totalStr.Contains(',') && !totalStr.Contains('.'))
                {
                    if (totalStr.Length >= 3)
                        totalStr = totalStr.Insert(totalStr.Length - 2, ",");
                }

                if (!IsSkippable(name) && name.Length >= 3 &&
                    decimal.TryParse(totalStr.Replace(',', '.'),
                        System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out decimal price) &&
                    price > 0 && price < 10000)
                {
                    items.Add(new ReceiptItem(supermarket, name, price, confidence * 0.85));
                    pendingName = null;
                    continue;
                }
            }

            // Pattern 2: Product name only (multi-line product)
            var productOnlyMatch = System.Text.RegularExpressions.Regex.Match(line,
                @"^[A-ZÁÉÍÓÚÑÜ][A-ZÁÉÍÓÚÑÜ\s\&\.\-\%'\(\)]{3,}$");
            if (productOnlyMatch.Success && !IsSkippable(line))
            {
                pendingName = line;
                continue;
            }

            // Pattern 3: Continuation line with price info
            if (pendingName != null)
            {
                var continuationMatch = System.Text.RegularExpressions.Regex.Match(line,
                    @"(\d[\d\,\.]*\s*(?:ud|kg)?)\s+([\d]+[,\.]?[\d]{2})\s*€");
                if (continuationMatch.Success)
                {
                    string totalStr = continuationMatch.Groups[2].Value;
                    if (!totalStr.Contains(',') && !totalStr.Contains('.'))
                    {
                        if (totalStr.Length >= 3)
                            totalStr = totalStr.Insert(totalStr.Length - 2, ",");
                    }

                    if (decimal.TryParse(totalStr.Replace(',', '.'),
                        System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out decimal price) &&
                        price > 0 && price < 10000)
                    {
                        items.Add(new ReceiptItem(supermarket, pendingName, price, confidence * 0.75));
                        pendingName = null;
                        continue;
                    }
                }
            }

            // Negative price lines (discounts/promotions)
            var negativeMatch = System.Text.RegularExpressions.Regex.Match(line,
                @"^([A-ZÁÉÍÓÚÑÜ][A-ZÁÉÍÓÚÑÜ\s\&\.\-\%]+?)\s+(-[\d]+[,\.]?[\d]{2})\s*€");
            if (negativeMatch.Success)
            {
                // Skip discounts - they're not products
                pendingName = null;
                continue;
            }
        }

        return items;
    }

    private static bool IsTableHeader(string line)
    {
        var upper = line.ToUpperInvariant();
        return upper.Contains("DESCRIPCIÓN") || 
               upper.Contains("CANTIDAD") || 
               upper.Contains("PRECIO KG") ||
               upper.Contains("TOTAL") && upper.Contains("VENTA");
    }

    private static bool IsMetadataLine(string line)
    {
        var upper = line.ToUpperInvariant().Trim();
        if (upper.Length < 2) return true;
        if (System.Text.RegularExpressions.Regex.IsMatch(upper, @"^[\d\s,\.\-\*\/\(\)x×]+$")) return true;
        if (upper.Contains("TOTAL") || upper.Contains("SUBTOTAL") || upper.Contains("IVA")) return true;
        if (upper.Contains("TARJETA") || upper.Contains("EFECTIVO")) return true;
        return false;
    }
}

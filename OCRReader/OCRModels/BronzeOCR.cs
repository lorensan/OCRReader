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

        // Detect if this is a table-based receipt (like Dia or Moises)
        if (IsTableFormat(text))
        {
            return ParseTableFormat(text, supermarket, confidence);
        }

        return BasicParse(text, supermarket, confidence);
    }

    protected override string ExtractSupermarket(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        
        // First check for known supermarkets in the first 10 lines
        foreach (var line in lines.Take(10))
        {
            string upper = line.ToUpperInvariant().Trim();
            foreach (string market in KnownSupermarkets)
                if (upper.Contains(market))
                    return market;
        }
        
        // If no known supermarket found, try to extract the store name from first line
        foreach (var line in lines.Take(6))
        {
            string trimmed = line.Trim();
            if (trimmed.Length > 3 && trimmed.Length < 50)
            {
                // Check if it looks like a store name (mostly letters)
                int letterCount = trimmed.Count(char.IsLetter);
                if (letterCount > trimmed.Length * 0.5)
                {
                    return trimmed;
                }
            }
        }
        
        return "DESCONOCIDO";
    }

    /// <summary>
    /// Detect table-format receipts by looking for table headers
    /// </summary>
    private static bool IsTableFormat(string text)
    {
        var upper = text.ToUpperInvariant();
        return upper.Contains("DESCRIPCIÓN") ||
               upper.Contains("CANTIDAD") ||
               upper.Contains("PRODUCTOS VENDIDOS") ||
               upper.Contains("ARTICULO");
    }

    /// <summary>
    /// Parse table-format receipts with enhanced pattern matching
    /// </summary>
    private static List<ReceiptItem> ParseTableFormat(string text, string supermarket, double confidence = 0.80)
    {
        var items = new List<ReceiptItem>();
        var lines = text.Split('\n');
        string? pendingName = null;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length < 2) continue;

            // Skip headers and metadata
            if (IsTableHeader(line) || IsMetadataLine(line))
            {
                pendingName = null;
                continue;
            }

            // Pattern 1: "PRODUCT  QTY  PRICE  TOTAL  VAT" (full table row)
            // Match the LAST price on the line (the total column)
            // Updated to handle both € and non-€ formats
            var fullRowMatch = System.Text.RegularExpressions.Regex.Match(line,
                @"^([A-ZÁÉÍÓÚÑÜ][A-ZÁÉÍÓÚÑÜ\s\&\.\-\%\d'\(\)\,]{3,}?)\s+(\d[\d\,\.]*\s*(?:ud|kg|u\.?d\.?)?)\s+[\d]+[,\.]?[\d]{2}\s*€?\s+([\d]+[,\.]?[\d]{2})\s*€?\s*$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (fullRowMatch.Success)
            {
                string name = fullRowMatch.Groups[1].Value.Trim();
                string totalStr = fullRowMatch.Groups[3].Value;

                totalStr = FixPriceFormat(totalStr);

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

                totalStr = FixPriceFormat(totalStr);

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

            // Pattern 1c: Simplified table row - product with quantity and price
            var simpleTableMatch = System.Text.RegularExpressions.Regex.Match(line,
                @"^([A-ZÁÉÍÓÚÑÜ][A-ZÁÉÍÓÚÑÜ\s\&\.\-\%'\(\)\d]{2,}?)\s+(\d[\d\,\.]*\s*(?:ud|kg|u\.?d\.?|U)?)\s+([\d]+[,\.]?[\d]{2})\s*€?\s*[A-Z]?\s*$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (simpleTableMatch.Success)
            {
                string name = simpleTableMatch.Groups[1].Value.Trim();
                string totalStr = simpleTableMatch.Groups[3].Value;

                totalStr = FixPriceFormat(totalStr);

                if (!IsSkippable(name) && name.Length >= 2 &&
                    decimal.TryParse(totalStr.Replace(',', '.'),
                        System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out decimal price) &&
                    price > 0 && price < 10000)
                {
                    items.Add(new ReceiptItem(supermarket, name, price, confidence * 0.85));
                    pendingName = null;
                    continue;
                }
            }

            // Pattern 1d: Product name followed by multiple prices (take the last one as total)
            // Updated to handle both € and non-€ formats
            var multiPriceMatch = System.Text.RegularExpressions.Regex.Match(line,
                @"^([A-ZÁÉÍÓÚÑÜ][A-ZÁÉÍÓÚÑÜ\s\&\.\-\%'\(\)\d\,]{2,}?)\s+([\d]+[,\.]?[\d]{2})\s+([\d]+[,\.]?[\d]{2})\s+([\d]+[,\.]?[\d]{2})\s*€?\s*$");
            if (multiPriceMatch.Success)
            {
                string name = multiPriceMatch.Groups[1].Value.Trim();
                string totalStr = multiPriceMatch.Groups[4].Value;

                totalStr = FixPriceFormat(totalStr);

                if (!IsSkippable(name) && name.Length >= 2 &&
                    decimal.TryParse(totalStr.Replace(',', '.'),
                        System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out decimal price) &&
                    price > 0 && price < 10000)
                {
                    items.Add(new ReceiptItem(supermarket, name, price, confidence * 0.8));
                    pendingName = null;
                    continue;
                }
            }

            // Pattern 1e: Very simple pattern - product name ending with price
            var simplePriceMatch = System.Text.RegularExpressions.Regex.Match(line,
                @"^([A-ZÁÉÍÓÚÑÜ][A-ZÁÉÍÓÚÑÜ\s\&\.\-\%'\(\)\d]{2,}?)\s+([\d]+[,\.][\d]{2})\s*€?\s*$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (simplePriceMatch.Success)
            {
                string name = simplePriceMatch.Groups[1].Value.Trim();
                string priceStr = simplePriceMatch.Groups[2].Value;

                // Skip if name is just a percentage (tax breakdown)
                if (System.Text.RegularExpressions.Regex.IsMatch(name, @"^\d+[,\.]\d{2}%$"))
                {
                    pendingName = null;
                    continue;
                }

                if (!IsSkippable(name) && name.Length >= 2 &&
                    decimal.TryParse(priceStr.Replace(',', '.'),
                        System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out decimal price) &&
                    price > 0 && price < 10000)
                {
                    items.Add(new ReceiptItem(supermarket, name, price, confidence * 0.75));
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
                    totalStr = FixPriceFormat(totalStr);

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

            // Pattern 4: Standalone price line (might be a product from previous line)
            var standalonePriceMatch = System.Text.RegularExpressions.Regex.Match(line,
                @"^([\d]+[,\.][\d]{2})\s*€\s*[A-Z]?\s*$");
            if (standalonePriceMatch.Success && pendingName != null)
            {
                string priceStr = standalonePriceMatch.Groups[1].Value;
                priceStr = FixPriceFormat(priceStr);

                if (decimal.TryParse(priceStr.Replace(',', '.'),
                    System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out decimal price) &&
                    price > 0 && price < 10000)
                {
                    items.Add(new ReceiptItem(supermarket, pendingName, price, confidence * 0.7));
                    pendingName = null;
                    continue;
                }
            }

            // Negative price lines (discounts/promotions)
            var negativeMatch = System.Text.RegularExpressions.Regex.Match(line,
                @"^([A-ZÁÉÍÓÚÑÜ][A-ZÁÉÍÓÚÑÜ\s\&\.\-\%]+?)\s+(-[\d]+[,\.]?[\d]{2})\s*€");
            if (negativeMatch.Success)
            {
                pendingName = null;
                continue;
            }

            // If no pattern matched and this looks like a product name, save it as pending
            if (!IsSkippable(line) && !IsMetadataLine(line) && line.Length >= 2)
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(line, @"[A-ZÁÉÍÓÚÑÜ]", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    pendingName = line;
                }
                else
                {
                    pendingName = null;
                }
            }
            else
            {
                pendingName = null;
            }
        }

        return items;
    }

    /// <summary>
    /// Fixes common OCR price format errors (e.g., "115" → "1,15", "099" → "0,99")
    /// </summary>
    private static string FixPriceFormat(string priceStr)
    {
        if (string.IsNullOrEmpty(priceStr))
            return priceStr;

        if (!priceStr.Contains(',') && !priceStr.Contains('.'))
        {
            if (priceStr.Length >= 3 && System.Text.RegularExpressions.Regex.IsMatch(priceStr, @"^\d{3,}$"))
            {
                priceStr = priceStr.Insert(priceStr.Length - 2, ",");
            }
        }

        return priceStr;
    }

    private static bool IsTableHeader(string line)
    {
        var upper = line.ToUpperInvariant();
        return upper.Contains("DESCRIPCIÓN") ||
               upper.Contains("CANTIDAD") ||
               upper.Contains("PRECIO KG") ||
               upper.Contains("TOTAL") && upper.Contains("VENTA") ||
               upper.Trim() == "ARTICULO" ||
               upper.Trim() == "PVP";
    }

    private static bool IsMetadataLine(string line)
    {
        var upper = line.ToUpperInvariant().Trim();
        if (upper.Length < 2) return true;
        if (System.Text.RegularExpressions.Regex.IsMatch(upper, @"^[\d\s,\.\-\*\/\(\)x×\+\:]+$")) return true;
        if (upper.Contains("TOTAL :") || upper.Contains("ENTREGA") || upper.Contains("CAMBIO")) return true;
        if (upper.Contains("TARJETA") || upper.Contains("EFECTIVO")) return true;
        if (upper.Contains("DESGLOSE") || upper.Contains("TIPO") || upper.Contains("BASE") || upper.Contains("IVA")) return true;
        if (upper.Contains("GRACIAS") || upper.Contains("DEVOLUCIONES") || upper.Contains("ATENDIDO")) return true;
        return false;
    }
}

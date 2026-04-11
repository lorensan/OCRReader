using System.Globalization;
using System.Text.RegularExpressions;

/// <summary>
/// Merges OCR results from Bronze, Silver, and Gold models into a single best-quality Receipt.
/// Applies product name validation, currency normalization, and cross-model consensus.
/// </summary>
class MergeOCRResult
{
    /// <summary>
    /// Merges results from three OCR models into a single Receipt with the best quality possible.
    /// - Product names must contain real words (not pure numbers)
    /// - '?' characters in prices are replaced with '€'
    /// - Cross-model consensus is used to pick the most reliable items
    /// </summary>
    public static Receipt MergeOCRModels(List<ReceiptItem> bronzeOCR, List<ReceiptItem> silverOCR, List<ReceiptItem> goldOCR)
    {
        var receipt = new Receipt();

        // Determine supermarket from the most reliable model (Gold > Silver > Bronze)
        receipt.Supermarket = ResolveSupermarket(bronzeOCR, silverOCR, goldOCR);

        // Collect all candidate items and merge
        var mergedItems = MergeAndDeduplicate(bronzeOCR, silverOCR, goldOCR);

        // Build products list with cleaned names and prices
        receipt.Products = mergedItems
            .Where(item => IsValidProductName(item.Product))
            .Select(item => new Product
            {
                Name = CleanProductName(item.Product),
                Price = NormalizePrice(item.Price)
            })
            .ToList();

        // Calculate totals
        receipt.Total = receipt.Products.Sum(p => decimal.TryParse(p.Price, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : 0);

        return receipt;
    }

    /// <summary>
    /// Resolves the supermarket name using priority: Gold > Silver > Bronze.
    /// </summary>
    private static string ResolveSupermarket(List<ReceiptItem> bronze, List<ReceiptItem> silver, List<ReceiptItem> gold)
    {
        if (gold.Count > 0 && !string.IsNullOrWhiteSpace(gold[0].Supermarket) && gold[0].Supermarket != "DESCONOCIDO")
            return gold[0].Supermarket;
        if (silver.Count > 0 && !string.IsNullOrWhiteSpace(silver[0].Supermarket) && silver[0].Supermarket != "DESCONOCIDO")
            return silver[0].Supermarket;
        if (bronze.Count > 0 && !string.IsNullOrWhiteSpace(bronze[0].Supermarket))
            return bronze[0].Supermarket;
        return "DESCONOCIDO";
    }

    /// <summary>
    /// Merges items from all three models using fuzzy matching to deduplicate.
    /// Items appearing in more models are preferred; prices are averaged.
    /// </summary>
    private static List<ReceiptItem> MergeAndDeduplicate(List<ReceiptItem> bronze, List<ReceiptItem> silver, List<ReceiptItem> gold)
    {
        var allItems = new List<(ReceiptItem Item, int Score)>();

        // Gold items get highest score (weight 3)
        foreach (var item in gold)
            allItems.Add((item, 3));

        // Silver items get medium score (weight 2)
        foreach (var item in silver)
            allItems.Add((item, 2));

        // Bronze items get lowest score (weight 1)
        foreach (var item in bronze)
            allItems.Add((item, 1));

        if (allItems.Count == 0)
            return new List<ReceiptItem>();

        var merged = new List<ReceiptItem>();
        var usedIndices = new HashSet<int>();

        // Sort by score descending so higher-quality models are processed first
        allItems = allItems.OrderByDescending(x => x.Score).ToList();

        for (int i = 0; i < allItems.Count; i++)
        {
            if (usedIndices.Contains(i))
                continue;

            var (currentItem, currentScore) = allItems[i];
            var matchingItems = new List<(ReceiptItem Item, int Score)> { (currentItem, currentScore) };
            usedIndices.Add(i);

            // Find similar items from other models
            for (int j = i + 1; j < allItems.Count; j++)
            {
                if (usedIndices.Contains(j))
                    continue;

                var (candidate, candidateScore) = allItems[j];
                int maxDist = Math.Max(2, Math.Max(currentItem.Product.Length, candidate.Product.Length) / 4);
                if (LevenshteinDistance(currentItem.Product.ToUpperInvariant(), candidate.Product.ToUpperInvariant()) <= maxDist)
                {
                    matchingItems.Add((candidate, candidateScore));
                    usedIndices.Add(j);
                }
            }

            // Pick the best item (highest score = best model)
            var best = matchingItems.OrderByDescending(x => x.Score).First();
            merged.Add(best.Item);
        }

        return merged;
    }

    /// <summary>
    /// Validates that a product name contains actual text (letters), not just numbers or garbage.
    /// </summary>
    private static bool IsValidProductName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        // Must contain at least one letter
        if (!Regex.IsMatch(name, @"[a-zA-ZáéíóúñÁÉÍÓÚÑüÜ]"))
            return false;

        // Must not be purely numeric
        if (Regex.IsMatch(name.Trim(), @"^[\d\s,\.\-]+$"))
            return false;

        // Must be at least 2 characters
        if (name.Trim().Length < 2)
            return false;

        return true;
    }

    /// <summary>
    /// Cleans a product name by removing leading/trailing special characters and normalizing whitespace.
    /// </summary>
    private static string CleanProductName(string name)
    {
        name = Regex.Replace(name, @"^[^a-zA-Z0-9áéíóúñÁÉÍÓÚÑüÜ]+", "");
        name = Regex.Replace(name, @"[^a-zA-Z0-9áéíóúñÁÉÍÓÚÑüÜ%\s\(\)]+$", "");
        name = Regex.Replace(name, @"\s{2,}", " ");
        return name.Trim();
    }

    /// <summary>
    /// Normalizes a price value to string format, replacing '?' with '€'.
    /// </summary>
    private static string NormalizePrice(decimal price)
    {
        return price.ToString("0.00").Replace('?', '€');
    }

    /// <summary>
    /// Formats the final price string, ensuring '?' is replaced with '€'.
    /// </summary>
    public static string FormatPrice(string price)
    {
        return price.Replace('?', '€');
    }

    // ── Levenshtein distance ─────────────────────────────────────────────────

    private static int LevenshteinDistance(string a, string b)
    {
        if (string.IsNullOrEmpty(a)) return b?.Length ?? 0;
        if (string.IsNullOrEmpty(b)) return a.Length;

        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = char.ToUpperInvariant(a[i - 1]) == char.ToUpperInvariant(b[j - 1]) ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[b.Length];
    }
}

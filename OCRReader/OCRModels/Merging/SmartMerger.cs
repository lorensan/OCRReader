using System.Text.RegularExpressions;

namespace OCRReader.Merging;

/// <summary>
/// Intelligently merges results from multiple OCR passes, prioritizing high-confidence extractions.
/// </summary>
public class SmartMerger
{
    /// <summary>
    /// Merges multiple ReceiptItem lists into a single deduplicated list.
    /// </summary>
    public List<ReceiptItem> Merge(IEnumerable<List<ReceiptItem>> allItems)
    {
        var merged = new Dictionary<string, MergedProduct>(StringComparer.OrdinalIgnoreCase);
        
        foreach (var items in allItems)
        {
            foreach (var item in items)
            {
                var key = NormalizeProductName(item.Product);
                
                if (merged.TryGetValue(key, out var existing))
                {
                    // Keep highest confidence version
                    if (item.Confidence > existing.Confidence)
                    {
                        existing.BestItem = item;
                        existing.Confidence = item.Confidence;
                    }
                    existing.Alternatives.Add(item);
                }
                else
                {
                    merged[key] = new MergedProduct
                    {
                        Key = key,
                        BestItem = item,
                        Confidence = item.Confidence,
                        Alternatives = new List<ReceiptItem> { item }
                    };
                }
            }
        }
        
        // Return merged items sorted by confidence (highest first)
        return merged.Values
            .OrderByDescending(m => m.Confidence)
            .Select(m => m.BestItem)
            .ToList();
    }
    
    /// <summary>
    /// Normalizes a product name for deduplication comparison.
    /// </summary>
    public static string NormalizeProductName(string productName)
    {
        if (string.IsNullOrWhiteSpace(productName))
            return string.Empty;
            
        // Remove OCR artifacts and special characters
        var normalized = Regex.Replace(productName, @"[^A-ZÁÉÍÓÚÑÜ0-9\s]", "", RegexOptions.IgnoreCase);
        
        // Normalize spacing
        normalized = Regex.Replace(normalized, @"\s+", " ").Trim().ToUpperInvariant();
        
        // Remove common OCR error patterns
        normalized = normalized.Replace("0", "O").Replace("1", "I").Replace("5", "S");
        
        return normalized;
    }
}

/// <summary>Represents a merged product from multiple OCR passes.</summary>
public class MergedProduct
{
    public string Key { get; set; } = string.Empty;
    public ReceiptItem BestItem { get; set; } = null!;
    public double Confidence { get; set; }
    public List<ReceiptItem> Alternatives { get; set; } = new();
}

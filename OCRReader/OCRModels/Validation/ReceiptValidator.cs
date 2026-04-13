using System.Text.RegularExpressions;

namespace OCRReader.Validation;

/// <summary>
/// Validates the overall structure and consistency of a parsed receipt.
/// </summary>
public class ReceiptValidator
{
    /// <summary>
    /// Validates a complete receipt and returns all detected warnings.
    /// </summary>
    public List<ConfidenceWarning> Validate(Receipt receipt)
    {
        var warnings = new List<ConfidenceWarning>();
        
        if (receipt == null || receipt.Products.Count == 0)
        {
            warnings.Add(new ConfidenceWarning(
                "Receipt",
                0.0,
                "No products detected - receipt may be unreadable or from unsupported format",
                WarningType.StructuralIssue));
            return warnings;
        }
        
        // 1. Validate individual product prices
        ValidateProductPrices(receipt, warnings);
        
        // 2. Check total consistency
        ValidateTotalConsistency(receipt, warnings);
        
        // 3. Detect duplicate products
        ValidateDuplicates(receipt, warnings);
        
        // 4. Check for suspicious patterns
        ValidateSuspiciousPatterns(receipt, warnings);
        
        return warnings;
    }
    
    private void ValidateProductPrices(Receipt receipt, List<ConfidenceWarning> warnings)
    {
        var priceValidator = new PriceValidator();
        
        foreach (var product in receipt.Products)
        {
            var result = priceValidator.Validate(decimal.Parse(product.Price), product.Name);
            
            foreach (var warning in result.Warnings)
            {
                warnings.Add(new ConfidenceWarning(
                    product.Name,
                    0.5, // Medium confidence due to price issue
                    warning,
                    WarningType.PriceOutOfRange));
            }
        }
    }
    
    private void ValidateTotalConsistency(Receipt receipt, List<ConfidenceWarning> warnings)
    {
        if (!receipt.Total.HasValue || receipt.Products.Count == 0)
            return;
            
        var sumOfProducts = receipt.Products.Sum(p => decimal.Parse(p.Price));
        var total = receipt.Total.Value;
        
        // Allow 5% tolerance for tax differences or rounding
        var tolerance = total * 0.05m;
        var difference = Math.Abs(sumOfProducts - total);
        
        if (difference > tolerance && difference > 0.50m) // At least €0.50 difference to flag
        {
            warnings.Add(new ConfidenceWarning(
                "Total",
                0.6,
                $"Sum of products ({sumOfProducts:C}) doesn't match receipt total ({total:C}). Difference: {difference:C}",
                WarningType.TotalMismatch));
        }
    }
    
    private void ValidateDuplicates(Receipt receipt, List<ConfidenceWarning> warnings)
    {
        var productGroups = receipt.Products
            .GroupBy(p => p.Name.ToUpperInvariant())
            .Where(g => g.Count() > 1);
            
        foreach (var group in productGroups)
        {
            warnings.Add(new ConfidenceWarning(
                group.Key,
                0.7,
                $"Product appears {group.Count()} times with different prices: {string.Join(", ", group.Select(p => p.Price))}",
                WarningType.DuplicateProduct));
        }
    }
    
    private void ValidateSuspiciousPatterns(Receipt receipt, List<ConfidenceWarning> warnings)
    {
        // Check for very short product names (likely OCR artifacts)
        var shortNames = receipt.Products
            .Where(p => p.Name.Length < 3 && decimal.Parse(p.Price) > 0);
            
        foreach (var product in shortNames)
        {
            warnings.Add(new ConfidenceWarning(
                product.Name,
                0.3,
                $"Product name is suspiciously short: '{product.Name}'",
                WarningType.LowConfidence));
        }
        
        // Check for products with only numbers (barcode misread as product)
        var numericOnly = receipt.Products
            .Where(p => Regex.IsMatch(p.Name, @"^[\d\s\,\.]+$"));
            
        foreach (var product in numericOnly)
        {
            warnings.Add(new ConfidenceWarning(
                product.Name,
                0.2,
                $"Product name contains only numbers - likely barcode or OCR artifact",
                WarningType.LowConfidence));
        }
        
        // Check for very few products from known supermarkets
        var knownMarkets = new[] { "CARREFOUR", "MERCADONA", "LIDL", "ALDI", "DIA", "EROSKI" };
        if (knownMarkets.Contains(receipt.Supermarket.ToUpperInvariant()) && 
            receipt.Products.Count < 3)
        {
            warnings.Add(new ConfidenceWarning(
                receipt.Supermarket,
                0.4,
                $"Very few products detected ({receipt.Products.Count}) for {receipt.Supermarket} - receipt may be partially unreadable",
                WarningType.StructuralIssue));
        }
    }
}

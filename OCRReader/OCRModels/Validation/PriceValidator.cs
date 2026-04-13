using System.Text.RegularExpressions;

namespace OCRReader.Validation;

/// <summary>
/// Validates extracted prices against reasonable ranges and detects anomalies.
/// </summary>
public class PriceValidator
{
    // Reasonable price ranges by product category (in EUR)
    private static readonly Dictionary<string, (decimal Min, decimal Max)> PriceRanges = new(StringComparer.OrdinalIgnoreCase)
    {
        ["LECHE"] = (0.50m, 5.00m),
        ["PAN"] = (0.50m, 4.00m),
        ["ACEITE"] = (2.00m, 15.00m),
        ["ARROZ"] = (0.50m, 5.00m),
        ["PASTA"] = (0.50m, 4.00m),
        ["TOMATE"] = (0.50m, 5.00m),
        ["JAMON"] = (2.00m, 15.00m),
        ["QUESO"] = (1.00m, 10.00m),
        ["YOGUR"] = (0.50m, 5.00m),
        ["HUEVO"] = (1.00m, 5.00m),
        ["POLLO"] = (2.00m, 15.00m),
        ["TERNERA"] = (3.00m, 25.00m),
        ["CERDO"] = (2.00m, 20.00m),
        ["PESCA"] = (3.00m, 25.00m),
        ["SALMON"] = (3.00m, 20.00m),
        ["ATUN"] = (1.00m, 5.00m),
        ["LECHUGA"] = (0.50m, 3.00m),
        ["PATATA"] = (0.50m, 5.00m),
        ["CEBOLLA"] = (0.50m, 4.00m),
        ["MANZANA"] = (1.00m, 5.00m),
        ["PLATANO"] = (0.50m, 4.00m),
        ["BANANA"] = (0.50m, 5.00m),
        ["NARANJA"] = (1.00m, 5.00m),
        ["AGUA"] = (0.30m, 3.00m),
        ["REFRESCO"] = (0.50m, 4.00m),
        ["ZUMO"] = (1.00m, 5.00m),
        ["CERVEZA"] = (0.50m, 10.00m),
        ["VINO"] = (2.00m, 25.00m),
        ["CAFE"] = (1.00m, 10.00m),
        ["AZUCAR"] = (0.50m, 3.00m),
        ["SAL"] = (0.30m, 2.00m),
        ["HARINA"] = (0.50m, 3.00m),
        ["DETERGENTE"] = (2.00m, 15.00m),
        ["SUAVIZANTE"] = (2.00m, 10.00m),
        ["LEJIA"] = (1.00m, 5.00m),
        ["PAPEL"] = (1.00m, 10.00m),
        ["GALLETAS"] = (1.00m, 5.00m),
        ["CHOCOLATE"] = (1.00m, 5.00m),
        ["CEREALES"] = (1.50m, 6.00m),
        ["PRINGLES"] = (1.50m, 4.00m),
        ["PIZZA"] = (2.00m, 10.00m),
        ["Default"] = (0.10m, 100.00m)
    };
    
    /// <summary>
    /// Validates a price against known product ranges.
    /// </summary>
    public PriceValidationResult Validate(decimal price, string productName)
    {
        var result = new PriceValidationResult
        {
            Price = price,
            ProductName = productName,
            IsValid = true,
            Warnings = new List<string>()
        };
        
        // Negative prices are only valid for discounts
        if (price < 0)
        {
            result.IsValid = false;
            result.Warnings.Add($"Negative price detected: {price:C}");
            return result;
        }
        
        // Zero prices are suspicious (might be free items or OCR errors)
        if (price == 0)
        {
            result.Warnings.Add("Price is €0.00 - verify if this is correct");
            return result;
        }
        
        // Find matching category
        var category = FindCategory(productName);
        var (min, max) = PriceRanges.GetValueOrDefault(category, PriceRanges["Default"]);
        
        // Check if price is within expected range
        if (price < min)
        {
            result.Warnings.Add($"Price {price:C} seems too low for '{productName}' (expected {min:C}-{max:C})");
        }
        else if (price > max)
        {
            result.Warnings.Add($"Price {price:C} seems too high for '{productName}' (expected {min:C}-{max:C})");
        }
        
        // Flag unusually high prices (>50 EUR) regardless of category
        if (price > 50)
        {
            result.Warnings.Add($"Unusually high price: {price:C}");
        }
        
        result.IsValid = result.Warnings.Count == 0;
        return result;
    }
    
    /// <summary>
    /// Validates all products in a receipt.
    /// </summary>
    public List<PriceValidationResult> ValidateAll(IEnumerable<ReceiptItem> products)
    {
        return products.Select(p => Validate(p.Price, p.Product)).ToList();
    }
    
    private static string FindCategory(string productName)
    {
        var upper = productName.ToUpperInvariant();
        
        foreach (var (keyword, _) in PriceRanges)
        {
            if (keyword == "Default") continue;
            if (upper.Contains(keyword))
                return keyword;
        }
        
        return "Default";
    }
}

/// <summary>Result of price validation.</summary>
public class PriceValidationResult
{
    public decimal Price { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public bool IsValid { get; set; }
    public List<string> Warnings { get; set; } = new();
}

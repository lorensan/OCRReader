# Ticket OCR Pipeline

## Goal

Create a highly accurate receipt reader in C# using Tesseract with three OCR levels (BronzeOCR, SilverOCR, GoldOCR) that output ReceiptItem objects. Each level improves accuracy and robustness, enabling construction of a final Receipt as close as possible to the real ticket.

Class Structure
Common Interface
public interface IReceiptOCR
{
    List<ReceiptItem> ProcessTicket(string imagePath);
}

public record ReceiptItem(string Supermarket, string Product, decimal Price);
OCR Implementations
public class BronzeOCR : IReceiptOCR { /* Basic OCR with Tesseract */ }
public class SilverOCR : IReceiptOCR { /* Bronze + heuristics and text cleaning */ }
public class GoldOCR : IReceiptOCR { /* Advanced parsing, pattern recognition */ }
Processing Flow
Input: image path.
BronzeOCR: basic text recognition.
SilverOCR: applies normalization, error correction, and heuristic parsing.
GoldOCR: refines Silver output, deduplicates items, validates prices, applies advanced heuristics.
Construct final Receipt with combined results.
Final Data Model
public class Receipt
{
    public string Supermarket { get; set; }
    public List<Product> Products { get; set; } = new List<Product>();
}

public class Product
{
    public string Name { get; set; }
    public string Price { get; set; }
}
Example Usage
IReceiptOCR bronze = new BronzeOCR();
IReceiptOCR silver = new SilverOCR();
IReceiptOCR gold = new GoldOCR();

List<ReceiptItem> bronzeItems = bronze.ProcessTicket("ticket.jpg");
List<ReceiptItem> silverItems = silver.ProcessTicket("ticket.jpg");
List<ReceiptItem> goldItems = gold.ProcessTicket("ticket.jpg");

// Combine results
var receipt = new Receipt
{
    Supermarket = goldItems.FirstOrDefault()?.Supermarket ?? "Unknown",
    Products = goldItems.Select(x => new Product { Name = x.Product, Price = x.Price.ToString("0.00") }).ToList()
};

Improvement Strategy per Level
BronzeOCR: basic OCR for simple tickets; reads text line by line, detects prices.
SilverOCR: improves Bronze output using:
Character cleaning (remove artifacts from OCR errors).
Pattern recognition for product names and prices.
Normalization of common supermarket abbreviations.
Heuristics to associate prices with product names even if misaligned.
Example prompt for Opus 4.6:
"Given the raw text from BronzeOCR, clean misrecognized characters, detect patterns where prices are numeric values following product names, normalize supermarket abbreviations, and output a list of ReceiptItem(Supermarket, Product, Price)."
GoldOCR: advanced OCR improvements using:
Learning common ticket layouts.
Deduplication of repeated products.
Cross-validation of totals (if available).
Error correction for misread prices or names.
Fuzzy matching against known product catalogs for improved accuracy.
Example prompt for Opus 4.6:
"Given cleaned SilverOCR output, detect errors in product names and prices, deduplicate repeated items, apply fuzzy matching against a product catalog, and produce a final list of ReceiptItem(Supermarket, Product, Price) as accurately as possible."

Notes
Each OCR class should return a List<ReceiptItem>; final Receipt is built using the GoldOCR output.
Opus 4.6 can suggest new heuristics, normalization rules, and error corrections for SilverOCR and GoldOCR based on actual OCR outputs.
The pipeline ensures modularity: future improvements can replace Silver or Gold without changing Bronze.
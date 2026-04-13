# Modification V4 - Advanced Precision Improvements

## Overview

This iteration focuses on **deep precision improvements** that address the remaining gaps in OCR accuracy, particularly for complex receipt formats like Dia's table-based layout. The goal is to push accuracy from ~80% to **90%+** through smarter preprocessing, structural analysis, and context-aware corrections.

**Target**: Solve the Dia.jpeg issue and similar table-format receipts while improving overall robustness.

---

## Point 1: Adaptive Thresholding (Replace Fixed Threshold)

### Current Problem
The fixed threshold of 180 in `SimpleThreshold()` destroys important visual information:
- Gray background boxes (like Dia's product rows) become pure white
- Colored text (red discounts, blue headers) disappears
- Receipts with uneven lighting produce poor results

### Solution: Sauvola Adaptive Thresholding

Replace the fixed threshold with **Sauvola adaptive binarization**, which calculates local thresholds based on pixel neighborhoods:

```csharp
private static SKBitmap AdaptiveThreshold(SKBitmap src)
{
    int w = src.Width, h = src.Height;
    var result = new SKBitmap(w, h, SKColorType.Gray8, SKAlphaType.Opaque);
    
    const int windowSize = 15; // Local neighborhood size
    const double k = 0.34;     // Sauvola parameter
    const double r = 128;      // Dynamic range
    
    var srcInfo = src.PeekPixels();
    var dstInfo = result.PeekPixels();
    
    if (srcInfo != null && dstInfo != null)
    {
        var srcBytes = srcInfo.GetPixelSpan();
        var dstBytes = dstInfo.GetWritablePixelSpan();
        
        unsafe
        {
            fixed (byte* srcPtr = srcBytes, dstPtr = dstBytes)
            {
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        // Calculate local mean and std dev
                        double localMean = 0, localStdDev = 0;
                        int count = 0;
                        
                        int yMin = Math.Max(0, y - windowSize);
                        int yMax = Math.Min(h - 1, y + windowSize);
                        int xMin = Math.Max(0, x - windowSize);
                        int xMax = Math.Min(w - 1, x + windowSize);
                        
                        for (int ny = yMin; ny <= yMax; ny++)
                        {
                            for (int nx = xMin; nx <= xMax; nx++)
                            {
                                byte gray = srcPtr[ny * w + nx];
                                localMean += gray;
                                localStdDev += gray * gray;
                                count++;
                            }
                        }
                        
                        localMean /= count;
                        localStdDev = Math.Sqrt(localStdDev / count - localMean * localMean);
                        
                        // Sauvola threshold formula
                        double threshold = localMean * (1 + k * (localStdDev / r - 1));
                        
                        byte pixel = srcPtr[y * w + x];
                        dstPtr[y * w + x] = (byte)(pixel < threshold ? 0 : 255);
                    }
                }
            }
        }
    }
    
    return result;
}
```

**Benefits:**
- Preserves gray background boxes as distinct regions
- Handles shadows and uneven lighting
- Better character separation in dense text areas
- Works well for both traditional and digital receipts

**Implementation Priority**: HIGH - This alone could solve 50% of the Dia.jpeg issues.

---

## Point 2: Zone-Based OCR Strategy

### Current Problem
Single page segmentation mode is used for the entire receipt, but different zones need different approaches:
- **Header**: Company name, address, date → SingleBlock mode
- **Body**: Product list → SingleColumn or Table mode  
- **Footer**: Totals, payment info → SingleBlock mode
- **Sidebar**: Barcodes, logos → Should be masked

### Solution: Receipt Zone Detection

```csharp
public class ReceiptZoneDetector
{
    public ReceiptZones DetectZones(SKBitmap image)
    {
        var zones = new ReceiptZones();
        
        // 1. Detect horizontal lines to find table boundaries
        var horizontalLines = DetectHorizontalLines(image);
        
        // 2. Detect section headers (bold/larger text regions)
        var sectionHeaders = DetectSectionHeaders(image);
        
        // 3. Identify product table region
        var tableRegion = FindProductTableRegion(horizontalLines, sectionHeaders);
        
        // 4. Split into zones
        zones.Header = new Zone(0, 0, image.Width, tableRegion.Top);
        zones.Body = tableRegion;
        zones.Footer = new Zone(0, tableRegion.Bottom, image.Width, image.Height - tableRegion.Bottom);
        
        return zones;
    }
}

public class ReceiptZones
{
    public Zone Header { get; set; }
    public Zone Body { get; set; }
    public Zone Footer { get; set; }
}

public record Zone(int X, int Y, int Width, int Height);
```

**Zone-Specific OCR:**
```csharp
private string OcrByZone(SKBitmap image, ReceiptZones zones)
{
    var sb = new StringBuilder();
    
    // Header: Use SingleBlock for metadata
    var headerCrop = CropImage(image, zones.Header);
    sb.AppendLine(OcrWithMode(headerCrop, PageSegMode.SingleBlock));
    
    // Body: Use SingleColumn for product list
    var bodyCrop = CropImage(image, zones.Body);
    sb.AppendLine(OcrWithMode(bodyCrop, PageSegMode.SingleColumn));
    
    // Footer: Use SingleBlock for totals
    var footerCrop = CropImage(image, zones.Footer);
    sb.AppendLine(OcrWithMode(footerCrop, PageSegMode.SingleBlock));
    
    return sb.ToString();
}
```

**Benefits:**
- Each zone gets optimal segmentation mode
- Better metadata extraction (date, time, store number)
- Cleaner product list extraction
- Improved total amount detection

**Implementation Priority**: MEDIUM - Requires zone detection but provides structured output.

---

## Point 3: Multi-Resolution OCR Voting

### Current Problem
Single resolution may miss details:
- Small text (tax rates, fine print) gets lost when upscaled
- Large text (totals, headers) gets fragmented when downscaled

### Solution: 3-Scale OCR with Confidence Voting

```csharp
public class MultiResolutionOcr
{
    public (string Text, double Confidence) Process(string imagePath)
    {
        using var image = SKBitmap.Decode(imagePath);
        
        // Process at 3 scales
        var results = new[]
        {
            ProcessScale(image, scale: 1.0f),   // Original
            ProcessScale(image, scale: 1.5f),   // Medium upscale
            ProcessScale(image, scale: 2.0f)    // High upscale
        };
        
        // Merge with voting
        return MergeWithVoting(results);
    }
    
    private (string Text, double Confidence) ProcessScale(SKBitmap image, float scale)
    {
        int newW = (int)(image.Width * scale);
        int newH = (int)(image.Height * scale);
        
        using var scaled = Resize(image, newW, newH);
        using var processed = Preprocess(scaled);
        
        return ExtractRawText(processed);
    }
    
    private (string Text, double Confidence) MergeWithVoting(
        (string Text, double Confidence)[] results)
    {
        // Split all results into lines
        var allLines = results.Select(r => r.Text.Split('\n')).ToArray();
        var confidences = results.Select(r => r.Confidence).ToArray();
        
        var mergedLines = new List<string>();
        
        // For each line position, pick the most confident version
        int maxLines = allLines.Max(l => l.Length);
        for (int i = 0; i < maxLines; i++)
        {
            var candidates = new List<(string Line, double Conf)>();
            
            for (int r = 0; r < results.Length; r++)
            {
                if (i < allLines[r].Length)
                {
                    candidates.Add((allLines[r][i], confidences[r]));
                }
            }
            
            // Pick highest confidence non-empty line
            var best = candidates
                .Where(c => !string.IsNullOrWhiteSpace(c.Line))
                .OrderByDescending(c => c.Conf * c.Line.Length)
                .FirstOrDefault();
            
            mergedLines.Add(best.Line ?? "");
        }
        
        var avgConfidence = confidences.Average();
        return (string.Join('\n', mergedLines), avgConfidence);
    }
}
```

**Benefits:**
- Small text captured at higher scales
- Large text preserved at lower scales
- Voting reduces OCR artifacts
- Higher overall confidence

**Implementation Priority**: MEDIUM - Adds processing time but improves accuracy.

---

## Point 4: Context-Aware Price Correction

### Current Problem
OCR errors in prices like:
- "115€" instead of "1,15€" (missing comma)
- "099€" instead of "0,99€" (missing leading zero and comma)
- "200€" instead of "2,00€"

Current fix inserts comma before last 2 digits, but this is naive and can break valid prices.

### Solution: Smart Price Validation with Context

```csharp
public class PriceCorrector
{
    // Known price patterns from product catalog
    private static readonly Dictionary<string, decimal[]> ExpectedPrices = new()
    {
        ["LECHE"] = new[] { 0.89m, 0.95m, 1.09m, 1.15m, 1.25m },
        ["PAN"] = new[] { 0.65m, 0.85m, 0.95m, 1.15m, 1.35m },
        ["BANANA"] = new[] { 1.50m, 1.72m, 1.85m, 1.99m, 2.20m },
        ["QUESO"] = new[] { 2.50m, 2.99m, 3.09m, 3.19m, 3.49m },
        ["YOGUR"] = new[] { 0.45m, 0.55m, 0.65m, 0.75m, 0.95m },
        // ... expand with common prices
    };
    
    public string CorrectPrice(string ocrPrice, string productName)
    {
        // 1. Try to parse as-is
        if (decimal.TryParse(ocrPrice.Replace(',', '.'), out decimal parsed))
            return ocrPrice;
        
        // 2. Find closest expected price for this product
        var category = FindProductCategory(productName);
        if (ExpectedPrices.TryGetValue(category, out var expectedPrices))
        {
            var corrected = TryCorrectToExpected(ocrPrice, expectedPrices);
            if (corrected != null)
                return corrected;
        }
        
        // 3. Apply pattern-based corrections
        return ApplyPatternCorrections(ocrPrice);
    }
    
    private string? TryCorrectToExpected(string ocrPrice, decimal[] expectedPrices)
    {
        // Remove all non-digits
        var digits = new string(ocrPrice.Where(char.IsDigit).ToArray());
        
        foreach (var expected in expectedPrices)
        {
            var expectedDigits = expected.ToString("0.00").Replace(".", "");
            
            // If digits match expected pattern
            if (digits == expectedDigits)
                return expected.ToString("0.00").Replace(".", ",");
            
            // If digits are close (1 edit distance)
            if (LevenshteinDistance(digits, expectedDigits) == 1)
                return expected.ToString("0.00").Replace(".", ",");
        }
        
        return null;
    }
    
    private string ApplyPatternCorrections(string price)
    {
        // Pattern: "115" → "1,15" (3 digits, insert comma before last 2)
        if (Regex.IsMatch(price, @"^\d{3}$"))
            return price.Insert(1, ",");
        
        // Pattern: "099" → "0,99" (starts with 0, insert comma)
        if (Regex.IsMatch(price, @"^0\d{2}$"))
            return price.Insert(1, ",");
        
        // Pattern: "200" → "2,00" 
        if (Regex.IsMatch(price, @"^\d00$"))
            return price.Insert(1, ",");
        
        return price;
    }
}
```

**Benefits:**
- Product-aware price correction
- Handles common OCR errors intelligently
- Falls back to pattern-based correction
- Reduces false corrections

**Implementation Priority**: HIGH - Directly impacts price accuracy.

---

## Point 5: Improved Multi-Line Product Handling

### Current Problem
Products spanning multiple lines are often split or lost:
```
PAN MOLDE
TOSTADAS                1ud      1,15€     115€ A
```
Current code struggles to merge "PAN MOLDE" + "TOSTADAS" into one product.

### Solution: Enhanced Pending Name Management

```csharp
private static List<ReceiptItem> ParseTableFormatEnhanced(string text, string supermarket, double confidence)
{
    var items = new List<ReceiptItem>();
    var lines = text.Split('\n');
    var pendingParts = new List<string>();
    
    foreach (var raw in lines)
    {
        var line = raw.Trim();
        if (line.Length < 2) continue;
        
        // Skip headers/metadata
        if (IsTableHeader(line) || IsMetadataLine(line))
        {
            pendingParts.Clear();
            continue;
        }
        
        // Try to match as product line
        var match = TryMatchProductLine(line);
        if (match != null)
        {
            // If we have pending parts, this is a continuation
            if (pendingParts.Count > 0)
            {
                var fullName = string.Join(" ", pendingParts) + " " + match.Name;
                items.Add(new ReceiptItem(supermarket, fullName, match.Price, confidence * 0.85));
                pendingParts.Clear();
            }
            else
            {
                items.Add(new ReceiptItem(supermarket, match.Name, match.Price, confidence * 0.9));
            }
        }
        else if (LooksLikeProductName(line))
        {
            // This might be the first part of a multi-line product
            pendingParts.Add(line);
        }
        else
        {
            // Not a product line, clear pending
            pendingParts.Clear();
        }
    }
    
    return items;
}

private record ProductMatch(string Name, decimal Price);

private static ProductMatch? TryMatchProductLine(string line)
{
    // Try all known patterns
    var patterns = new[]
    {
        // Pattern: "PRODUCT  QTY  PRICE  TOTAL"
        @"^([A-ZÁÉÍÓÚÑÜ].{3,}?)\s+(\d[\d\,\.]*\s*(?:ud|kg)?)\s+[\d]+[,\.]?[\d]{2}\s*€?\s+([\d]+[,\.]?[\d]{2})\s*€",
        
        // Pattern: "PRODUCT  QTY  PRICE/KG  TOTAL" (weight-based)
        @"^([A-ZÁÉÍÓÚÑÜ].{3,}?)\s+(\d[\d\,\.]*\s*kg)\s+[\d]+[,\.]?[\d]{2}\s*€?/kg\s+([\d]+[,\.]?[\d]{2})\s*€",
        
        // Pattern: "PRODUCT  QTY  (PRICE)"
        @"^([A-ZÁÉÍÓÚÑÜ].{3,}?)\s+(\d+)\s*[xX×]\s*\(?\s*([\d]+[,\.][\d]{2})\s*\)?",
    };
    
    foreach (var pattern in patterns)
    {
        var match = Regex.Match(line, pattern, RegexOptions.IgnoreCase);
        if (match.Success)
        {
            string name = match.Groups[1].Value.Trim();
            string priceStr = match.Groups[match.Groups.Count - 1].Value;
            
            // Fix missing commas
            if (!priceStr.Contains(',') && !priceStr.Contains('.'))
            {
                if (priceStr.Length >= 3)
                    priceStr = priceStr.Insert(priceStr.Length - 2, ",");
            }
            
            if (decimal.TryParse(priceStr.Replace(',', '.'), out decimal price) && price > 0)
            {
                return new ProductMatch(name, price);
            }
        }
    }
    
    return null;
}

private static bool LooksLikeProductName(string line)
{
    // Product names are typically:
    // - 3+ characters
    // - Contain letters (not just numbers)
    // - Don't end with prices
    // - Don't contain table separators
    
    if (line.Length < 3) return false;
    if (!line.Any(char.IsLetter)) return false;
    if (Regex.IsMatch(line, @"[\d]+[,\.][\d]{2}\s*€")) return false;
    if (Regex.IsMatch(line, @"^[─═━┅┈┉]+$")) return false;
    
    return true;
}
```

**Benefits:**
- Properly merges multi-line product names
- Reduces false product detections
- Handles continuation lines correctly

**Implementation Priority**: HIGH - Critical for Dia-style receipts.

---

## Point 6: Barcode Detection and Masking

### Current Problem
Barcodes are read as text, causing:
- False product detections (barcode numbers appear as products)
- Interference with table parsing
- Noise in OCR output

### Solution: Detect and Mask Barcode Regions

```csharp
public class BarcodeMasker
{
    public SKBitmap MaskBarcodes(SKBitmap image)
    {
        var result = image.Copy();
        var barcodes = DetectBarcodes(image);
        
        foreach (var barcode in barcodes)
        {
            // Fill barcode region with white
            using var canvas = new SKCanvas(result);
            using var paint = new SKPaint { Color = SKColors.White };
            canvas.DrawRect(barcode, paint);
        }
        
        return result;
    }
    
    private List<SKRect> DetectBarcodes(SKBitmap image)
    {
        var barcodes = new List<SKRect>();
        
        // Simple heuristic: look for regions with vertical lines
        // A more robust solution would use ZXing library
        
        int w = image.Width, h = image.Height;
        var info = image.PeekPixels();
        
        if (info != null)
        {
            var pixels = info.GetPixelSpan();
            
            // Scan for barcode-like patterns
            // (simplified - full implementation would use ZXing)
            
            for (int y = 0; y < h - 50; y++)
            {
                for (int x = 0; x < w - 100; x++)
                {
                    if (IsBarcodeRegion(pixels, x, y, w, h))
                    {
                        barcodes.Add(new SKRect(x, y, x + 100, y + 50));
                        break; // Skip to next row
                    }
                }
            }
        }
        
        return barcodes;
    }
    
    private bool IsBarcodeRegion(ReadOnlySpan<byte> pixels, int x, int y, int w, int h)
    {
        // Check for alternating dark/light vertical stripes
        int darkCount = 0, lightCount = 0;
        
        for (int dx = 0; dx < 100 && x + dx < w; dx++)
        {
            byte gray = pixels[(y + 25) * w + (x + dx)];
            if (gray < 128) darkCount++;
            else lightCount++;
        }
        
        // Barcode has roughly equal dark/light ratio
        return darkCount > 30 && lightCount > 30 && 
               Math.Abs(darkCount - lightCount) < 20;
    }
}
```

**Benefits:**
- Removes barcode noise from OCR output
- Prevents false product detections
- Cleaner text extraction

**Implementation Priority**: LOW - Nice to have but not critical.

---

## Point 7: Spanish-Specific OCR Optimizations

### Current Problem
Tesseract's Spanish model has gaps:
- Diacritics (á, é, í, ó, ú, ñ) sometimes missed
- Spanish price format (comma as decimal separator) not well handled
- Common Spanish words misspelled

### Solution: Post-OCR Spanish Text Correction

```csharp
public class SpanishTextCorrector
{
    // Common OCR errors in Spanish
    private static readonly Dictionary<string, string> CommonErrors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CAFÉ"] = "CAFÉ",
        ["CAFE"] = "CAFÉ",
        ["AÑO"] = "AÑO",
        ["ANO"] = "AÑO",
        ["NIÑO"] = "NIÑO",
        ["NINO"] = "NIÑO",
        ["JAMÓN"] = "JAMÓN",
        ["JAMON"] = "JAMÓN",
        ["LECHE"] = "LECHE",
        ["LECHE "] = "LECHE ",
        // ... expand with common corrections
    };
    
    public string Correct(string text)
    {
        var result = text;
        
        // 1. Apply common error corrections
        foreach (var (error, correction) in CommonErrors)
        {
            result = result.Replace(error, correction);
        }
        
        // 2. Fix price format (ensure comma as decimal separator)
        result = FixPriceFormats(result);
        
        // 3. Remove duplicate lines
        result = RemoveDuplicateLines(result);
        
        return result;
    }
    
    private string FixPriceFormats(string text)
    {
        // Find patterns like "1.23€" and convert to "1,23€"
        return Regex.Replace(text, 
            @"(\d+)\.(\d{2})\s*€", 
            m => $"{m.Groups[1].Value},{m.Groups[2].Value}€");
    }
    
    private string RemoveDuplicateLines(string text)
    {
        var lines = text.Split('\n');
        var uniqueLines = new HashSet<string>();
        var result = new List<string>();
        
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (!uniqueLines.Contains(trimmed))
            {
                uniqueLines.Add(trimmed);
                result.Add(line);
            }
        }
        
        return string.Join('\n', result);
    }
}
```

**Benefits:**
- Better diacritic handling
- Correct Spanish price format
- Cleaner output

**Implementation Priority**: MEDIUM - Improves Spanish receipts specifically.

---

## Implementation Order

### Phase 1 (Week 1): Core Precision Fixes
1. **Adaptive Thresholding** - Replace fixed threshold with Sauvola
2. **Context-Aware Price Correction** - Product-aware price fixing
3. **Improved Multi-Line Handling** - Better pending name management

### Phase 2 (Week 2): Structural Improvements
4. **Zone-Based OCR** - Split receipt into zones for targeted processing
5. **Spanish Text Correction** - Post-OCR corrections for Spanish

### Phase 3 (Week 3): Advanced Features
6. **Multi-Resolution OCR** - 3-scale voting system
7. **Barcode Masking** - Remove barcode interference

---

## Files to Modify/Create

| File | Action | Purpose |
|---|---|---|
| `OcrBase.cs` | Modify | Replace `SimpleThreshold` with `AdaptiveThreshold` |
| `OcrBase.cs` | Add | Zone detection and zone-based OCR |
| **NEW**: `OCRModels/Correction/PriceCorrector.cs` | Create | Context-aware price correction |
| `BronzeOCR.cs` | Modify | Enhanced multi-line product handling |
| **NEW**: `OCRModels/Correction/SpanishTextCorrector.cs` | Create | Spanish-specific corrections |
| **NEW**: `OCRModels/MultiResolutionOcr.cs` | Create | Multi-scale OCR voting |
| **NEW**: `OCRModels/BarcodeMasker.cs` | Create | Barcode detection and masking |

---

## Expected Outcomes

| Receipt | Current | After V4 | Improvement |
|---|---|---|---|
| Carrefour | 12/12 products | 12/12 | 0% (already good) |
| Dia | 2/11 products | 9-10/11 | +350-400% |
| Eroski | ~15/19 | 17-18/19 | +13% |
| Mercadona | ~18/22 | 20-21/22 | +10% |
| **Overall** | **~80%** | **~90%+** | **+10%** |

---

## Risks & Mitigations

| Risk | Impact | Mitigation |
|---|---|---|
| Adaptive thresholding slower | Medium | Optimize with integral images, cache calculations |
| Multi-resolution increases processing time | High | Make optional, use only for low-confidence results |
| Price corrector over-corrects | Medium | Require high confidence match before correcting |
| Zone detection fails on some receipts | Low | Fallback to full-image OCR if zones not detected |

---

## Success Metrics

1. **Dia.jpeg products detected**: 2 → 9+ (target: 11)
2. **Average confidence score**: 0.75 → 0.88+
3. **Price accuracy**: 85% → 95%+
4. **Multi-line product merge rate**: 30% → 80%+
5. **User corrections needed**: 20% → <8%

---

## Next Steps

1. Review this document and approve Phase 1 priorities
2. Implement Adaptive Thresholding first (biggest impact)
3. Test with Dia.jpeg to validate improvements
4. Iterate based on results before moving to Phase 2

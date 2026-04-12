# Ticket OCR Pipeline
# Ticket OCR Pipeline

## Goal

Create a highly accurate receipt reader in C# using Tesseract with three OCR levels (BronzeOCR, SilverOCR, GoldOCR) that output ReceiptItem objects. Each level improves accuracy and robustness, enabling construction of a final Receipt as close as possible to the real ticket.

---

## Architecture

### Class Structure

**Common Interface**
```csharp
public interface IReceiptOCR
{
    List<ReceiptItem> ProcessTicket(string imagePath);
}
```

**OCR Implementations**
```csharp
public class BronzeOCR : OcrBase { /* Basic multi-pass Tesseract */ }
public class SilverOCR : OcrBase { /* Bronze + column detection + text cleaning */ }
public class GoldOCR   : OcrBase { /* Multi-pass Tesseract + dedup + fuzzy match */ }
```

**Final Data Model**
```csharp
public class Receipt
{
    public string Supermarket { get; set; }
    public List<Product> Products { get; set; }
}

public record ReceiptItem(string Supermarket, string Product, decimal Price);
```

---

## Processing Flow

1. **Input**: Image path (JPEG/PNG)
2. **Preprocessing**: Grayscale → Deskew → Upscale → Threshold
3. **BronzeOCR**: Multi-pass Tesseract with best segmentation mode selection
4. **SilverOCR**: Bronze + column layout detection + OCR artifact correction
5. **GoldOCR**: Multi-pass Tesseract + deduplication + total validation + fuzzy catalog matching
6. **Output**: Final Receipt from GoldOCR results

---

## Preprocessing Pipeline (OcrBase.cs)

### Step-by-step image transformation:

| Step | Operation | Purpose |
|------|-----------|---------|
| 1 | **Grayscale** | Rec. 709 luma coefficients (0.2126R + 0.7152G + 0.0722B) |
| 2 | **Deskew** | Projection profile analysis (-5° to +5°), fine-tuned to 0.1° precision |
| 3 | **Upscale** | Bicubic interpolation to 2400px minimum side (~300 DPI) |
| 4 | **Threshold** | Fixed threshold of 180 (black text on white paper) |

### Code flow:
```csharp
private static Bitmap Preprocess(Bitmap original)
{
    using var gray = ToGrayscale(original);
    using var deskewed = Deskew(gray);
    using var upscaled = Upscale(deskewed, 2400);
    return SimpleThreshold(upscaled);
}
```

---

## OCR Tiers

### BronzeOCR — Basic Multi-pass Tesseract

- Runs Tesseract with 4 page segmentation modes: Auto, SingleBlock, SingleColumn, SparseText
- Selects the result with the best confidence × length score
- Basic line-by-line regex parsing for product-price pairs
- Detects supermarket name from first 6 lines

**Patterns detected:**
- `PRODUCT   PRICE` (2+ spaces separator)
- `N x ( PRICE )` (multi-quantity sub-lines)
- Standalone price lines (associated with previous product name)

### SilverOCR — Structural Analysis

All Bronze features, plus:

- **Column layout detection**: Identifies if receipt uses 2-column product/price layout (>60% threshold)
- **OCR text cleaning**: Removes non-printable characters, fixes common punctuation artifacts (`}` → `)`, `[` → `(`)
- **Price numerics fix**: Corrects letter→digit swaps in price tokens (O→0, l→1, I→1, S→5, B→8)
- **Supermarket normalization**: Alias dictionary + fuzzy matching (Levenshtein distance ≤ 2)
- **Enhanced parsing**: 5 regex patterns including loose single-space match with validation
- **Section header skipping**: Ignores TOTAL, SUBTOTAL, IVA, DTO, barcode lines

### GoldOCR — Advanced Validation

All Silver features, plus:

- **Multi-pass Tesseract**: Same as Bronze but with deeper text cleaning
- **Systematic misread correction**: "rn" → "m", "vv" → "w", digit swaps in price context
- **Line classification**: Categorizes lines as ProductWithPrice, QuantityPrice, PriceOnly, ProductOnly, noise
- **Deduplication**: Merges similar product names (Levenshtein distance ≤ length/5) by summing prices
- **Total validation**: Cross-checks extracted items against receipt TOTAL line (5% tolerance)
- **Fuzzy catalog matching**: Matches against 50+ known supermarket products (threshold ≤ 3 edit distance)

---

## Product Catalog (GoldOCR)

Built-in catalog for fuzzy matching:
- **Dairy**: LECHE ENTERA, QUESO FRESCO, YOGUR NATURAL, MANTEQUILLA
- **Meat**: JAMON SERRANO, PECHUGA PAVO, POLLO, CERDO, TERNERA
- **Produce**: TOMATE, PATATA, CEBOLLA, BANANA, AGUACATE
- **Pantry**: ARROZ, ACEITE OLIVA, AZUCAR, HARINA, PASTA
- **Snacks**: PRINGLES, GALLETAS, CHOCOLATE, NACHOS
- **Beverages**: CERVEZA, AGUA MINERAL, ZUMO, CAFE
- **Household**: DETERGENTE, PAPEL HIGIENICO, LEJIA

---

## Processing Flow Diagram

```
┌──────────────┐
│  Input Image │
└──────┬───────┘
       │
       ▼
┌─────────────────────────────────────┐
│        Preprocessing Pipeline       │
│  Grayscale → Deskew → Upscale → B&W │
└────────────────────────────────────┘
       │
       ▼
┌─────────────────────────────────────┐
│           BronzeOCR                 │
│  Multi-pass Tesseract (4 modes)     │
│  Basic regex parsing                │
└────────────────────────────────────┘
       │
       ▼
┌─────────────────────────────────────┐
│           SilverOCR                 │
│  Column detection + text cleaning   │
│  Price numerics correction          │
│  Supermarket fuzzy matching         │
└──────┬──────────────────────────────┘
       │
       ▼
┌─────────────────────────────────────┐
│            GoldOCR                  │
│  Systematic misread correction      │
│  Line classification + parsing      │
│  Deduplication + total validation   │
│  Fuzzy catalog matching             │
└──────┬──────────────────────────────┘
       │
       ▼
┌─────────────────────────────────────┐
│         Final Receipt               │
│  Supermarket + Products + Total     │
└─────────────────────────────────────┘
```

---

## Known Limitations

| Issue | Receipt | Cause | Workaround |
|-------|---------|-------|------------|
| Barcode interference | Receipt2 | Large barcode causes deskew rotation to blank out image | Add barcode detection + crop before processing |
| OCR diacritics | Receipt4 | Tesseract detects 47+ diacritics, some lines unrecognizable | Acceptable — text still extracted |
| Fuzzy matching too aggressive | Gold tier | Sometimes over-corrects valid names (e.g., "SALSA SOJA HEINZ" → "SAL") | Increase Levenshtein threshold or add whitelist |

---

## How to Run

```bash
cd C:\Develop\Repositories\OCRReader\OCRReader
dotnet run
```

**Requirements:**
- .NET 10.0 SDK
- `tessdata/` folder with `spa.traineddata` and `eng.traineddata`
- Sample images in `Samples/` folder (JPEG/PNG)

---

## Next Improvements

### 1. Improve Image Text Extraction Algorithm

#### 1.1 Add Inverted Text Detection
Some receipts use dark backgrounds with light text. Add a pre-check:
```csharp
// If >70% of pixels are dark (<128), invert before processing
if (darkPixelRatio > 0.7)
    InvertColors(image);
```

#### 1.2 Adaptive Threshold (Sauvola or Niblack)
Replace fixed threshold (180) with adaptive methods that handle uneven lighting:
- **Sauvola**: `T(x,y) = mean(x,y) * [1 + k * (std(x,y)/R - 1)]`
- Works better for photos taken with phone cameras (shadows, glare)

#### 1.3 Morphological Text Enhancement
After binarization, apply morphological operations to improve character recognition:
```csharp
// Closing: fill gaps inside characters (e.g., broken '0', '8')
image = MorphologicalClose(image, kernelSize: 2);
// Opening: remove isolated noise dots
image = MorphologicalOpen(image, kernelSize: 1);
```

#### 1.4 Skew Detection Improvement
Current deskew uses projection profile. Add Hough Line Transform as fallback:
- Detect text baseline lines using probabilistic Hough
- Calculate angle from dominant line orientation
- More robust for receipts with curved/wavy text lines

#### 1.5 Multi-resolution OCR with Voting
Process image at 3 scales (1x, 1.5x, 2x) and merge results:
```csharp
var results = new[] {
    Ocr(image, scale: 1.0),
    Ocr(image, scale: 1.5),
    Ocr(image, scale: 2.0)
};
return MergeWithVoting(results); // Pick most confident word per position
```

#### 1.6 Zone-based OCR
Split receipt into zones (header, body, footer) and apply different page segmentation modes:
- **Header**: SingleBlock (company name, address, date)
- **Body**: SingleColumn (product list)
- **Footer**: SingleBlock (totals, payment method)

#### 1.7 Confidence-guided Post-processing
For low-confidence words (<60%), apply:
- Spell checking against Spanish dictionary
- Levenshtein distance to nearest catalog item
- Context-aware correction (e.g., "QUES0" → "QUESO" if surrounded by food items)

#### 1.8 Barcode/QR Code Handling
Detect and mask barcode regions before OCR:
```csharp
// Use ZXing to find barcode bounding box
var barcodeRect = FindBarcode(image);
// Fill with white to prevent Tesseract from reading it as text
FillRectangle(image, barcodeRect, Color.White);
```

### 2. Parsing Improvements

#### 2.1 Weight-based Products
Handle weight × price-per-kg format:
```
0,280 kg   2,40 €/kg   0,67€
```
Current: Detected but sometimes misparsed. Improvement: Add dedicated regex pattern.

#### 2.2 Discount Lines
Detect and handle discount rows:
```
DESCUENTO EN 2ª UNIDAD    -1,49
DTO. EMPLEADO             -4,75
```
Add to parsing with negative price support and mark as discount (not a product).

#### 2.3 VAT/Tax Breakdown
Extract tax information for accounting:
```
4%    20,43    0,82€
10%   34,77    3,48€
21%    6,98    1,47€
```
Parse into Receipt.TaxBreakdown list.

#### 2.4 Loyalty Points / Rewards
Some receipts include points earned:
```
PUNTOS GANADOS: 12
```
Extract into Receipt.LoyaltyPoints field.

### 3. Product Catalog Expansion

#### 3.1 Dynamic Catalog Learning
Store unrecognized product names and build catalog over time:
```csharp
// After user confirms/corrects a product name
catalog.Add(correctedName);
SaveCatalog("custom_catalog.txt");
```

#### 3.2 Category-based Matching
Group products by category for better fuzzy matching:
```csharp
var categories = new Dictionary<string, string[]> {
    ["Dairy"] = ["LECHE", "QUESO", "YOGUR", "MANTEQUILLA"],
    ["Meat"] = ["JAMON", "POLLO", "TERNERA", "PECHUGA"],
    // ...
};
// Match within category first, then fallback to global
```

#### 3.3 External Catalog Import
Support loading product catalogs from CSV/JSON:
```csharp
LoadCatalogFromCsv("supermarket_catalog.csv");
// Format: Name,Category,Barcode,Price
```

### 4. Output and Integration

#### 4.1 JSON Export
```csharp
var json = JsonConvert.SerializeObject(receipt, Formatting.Indented);
File.WriteAllText("receipt.json", json);
```

#### 4.2 Excel/CSV Export
Export parsed data for accounting integration:
```csharp
SaveAsCsv(receipt, "receipt.csv");
// Columns: Product, Price, Category, TaxRate
```

#### 4.3 Receipt Comparison
Compare two receipts to find price changes:
```csharp
var diff = CompareReceipts(oldReceipt, newReceipt);
// Output: {"LECHE ENTERA": {old: 1.09, new: 1.15, change: +5.5%}}
```

#### 4.4 Monthly Summary
Aggregate all processed receipts:
```csharp
var summary = AggregateMonthly(receipts, year: 2024, month: 3);
// Output: Total spent, top categories, price trends
```

---

## File Structure

```
OCRReader/
├── OCRReader.csproj          # .NET 10 project (Tesseract only, no PaddleOCR)
├── Program.cs                # Main entry point, processes all samples
├── Receipt.cs                # Data models: Receipt, Product, ReceiptLine
├── ReceiptItem.cs            # Record: (Supermarket, Product, Price)
├── OCRModels/
│   ├── IReceiptOCR.cs        # Common interface
│   ├── OcrBase.cs            # Base class: preprocessing + shared helpers
│   ├── BronzeOCR.cs          # Tier 1: Basic multi-pass Tesseract
│   ├── SilverOCR.cs          # Tier 2: Column detection + cleaning
│   └── GoldOCR.cs            # Tier 3: Dedup + validation + fuzzy match
├── tessdata/
│   ├── spa.traineddata       # Spanish language model
│   └── eng.traineddata       # English language model
└── Samples/
    ├── Receipt1.jpeg         # CARREFOUR
    ├── Receipt2.jpeg         # [Barcode interference issue]
    ├── Receipt3.jpeg         # EROSKI
    └── Receipt4.jpeg         # MERCADONA
```

---

## Modification History

| Date | Change | Files |
|------|--------|-------|
| 2025-04-10 | Initial implementation: 3-tier OCR pipeline with Tesseract | All files |
| 2025-04-10 | Removed PaddleOCRSharp dependency | .csproj, GoldOCR.cs |
| 2025-04-10 | Simplified preprocessing: grayscale → deskew → upscale → threshold | OcrBase.cs |
| 2025-04-10 | Fixed multi-pass OCR: removed OSD-dependent modes | OcrBase.cs |
| 2025-04-10 | Receipt2 not detected (barcode interference — known limitation) | — |

---

## References

- [Tesseract OCR Documentation](https://tesseract-ocr.github.io/)
- [Tesseract Page Segmentation Modes](https://github.com/tesseract-ocr/tesseract/blob/main/doc/tesseract.1.asc)
- [Sauvola Binarization](https://en.wikipedia.org/wiki/Sauvola%27s_method)
- [Levenshtein Distance](https://en.wikipedia.org/wiki/Levenshtein_distance)
- [Rec. 709 Luma Coefficients](https://en.wikipedia.org/wiki/Rec._709)

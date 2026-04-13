# Modification V3 - Smart OCR Validation & Learning Pipeline

## Overview

This iteration focuses on improving the **practical accuracy** of the OCR pipeline for mobile deployment, acknowledging that Tesseract has ~80% baseline accuracy. Instead of chasing perfect OCR, we implement **smart validation, confidence scoring, and a learning mechanism** that improves over time based on user corrections.

**Target**: Increase effective accuracy from 80% to 90%+ through post-processing and learning, while keeping 100% local execution (no APIs, no cloud).

---

## Point 1: Accept 80% OCR Accuracy as Baseline

### Current State
- Tesseract extracts ~80% of products correctly on average
- Some receipts (like Dia.jpeg) have 0% detection due to layout differences
- No feedback mechanism when OCR fails

### Implementation
- Accept that Tesseract is good but imperfect
- Focus engineering effort on **post-processing and validation** instead of preprocessing
- Document known limitations in README

### Expected Outcome
- Realistic expectations: ~80% of products detected automatically
- Remaining 20% handled by user correction flow
- No more "chasing perfect OCR" - focus on smart fallbacks

---

## Point 2: Add Confidence Scoring

### Goal
Flag low-confidence extractions so the mobile app can prompt users for review.

### Implementation

#### 2.1 Extend `ReceiptItem` with Confidence Metadata
```csharp
public record ReceiptItem(
    string Supermarket, 
    string Product, 
    decimal Price,
    double Confidence = 1.0,           // 0.0 to 1.0
    ExtractionSource Source = ExtractionSource.OcrDirect, // Where it came from
    bool RequiresReview = false        // Flag for mobile app
);

public enum ExtractionSource
{
    OcrDirect,         // Directly from OCR text
    CatalogMatch,      // Fuzzy-matched to known product
    UserCorrected,     // Corrected by user
    Inferred,          // Inferred from context/total
    LowConfidence      // OCR confidence < threshold
}
```

#### 2.2 Track Confidence at Each OCR Level
- **BronzeOCR**: Base confidence from Tesseract's `GetMeanConfidence()`
- **SilverOCR**: Adjust confidence based on text cleaning success
- **GoldOCR**: Final confidence after deduplication and catalog matching

#### 2.3 Confidence Thresholds
```csharp
public static class ConfidenceThresholds
{
    public const double High = 0.85;    // Auto-accept
    public const double Medium = 0.60;  // Flag for review
    public const double Low = 0.40;     // Reject or heavy review
}
```

#### 2.4 Output Structure
```csharp
public class OcrResult
{
    public Receipt Receipt { get; set; }
    public double OverallConfidence { get; set; }
    public int ProductsRequiringReview { get; set; }
    public List<ConfidenceWarning> Warnings { get; set; }
}

public record ConfidenceWarning(
    string ProductName,
    double Confidence,
    string Reason
);
```

### Expected Outcome
- Mobile app receives confidence scores with each product
- Can highlight low-confidence items for user review
- Enables "quick approve" for high-confidence extractions

---

## Point 3: Implement Smart Validation

### Goal
Detect impossible prices, missing products, and structural issues automatically.

### Implementation

#### 3.1 Price Validation Rules
```csharp
public class PriceValidator
{
    // Reasonable price ranges by product category
    private static readonly Dictionary<string, (decimal Min, decimal Max)> PriceRanges = new()
    {
        ["Beverage"] = (0.50m, 15.00m),
        ["Dairy"] = (0.50m, 10.00m),
        ["Snack"] = (0.50m, 8.00m),
        ["Produce"] = (0.20m, 20.00m),  // Weight-based can be higher
        ["Meat"] = (1.00m, 30.00m),
        ["Household"] = (1.00m, 25.00m),
        ["Default"] = (0.10m, 100.00m)  // Fallback
    };

    public ValidationResult Validate(decimal price, string productName)
    {
        // Check against known ranges
        // Flag outliers for review
        // Return warnings
    }
}
```

#### 3.2 Structural Validation
```csharp
public class ReceiptValidator
{
    public List<ValidationWarning> Validate(Receipt receipt)
    {
        var warnings = new List<ValidationWarning>();

        // 1. Check if total matches sum of products (within 5% tolerance)
        ValidateTotalConsistency(receipt, warnings);

        // 2. Detect suspiciously few products for known supermarkets
        ValidateProductCount(receipt, warnings);

        // 3. Check for duplicate products (same name, different prices)
        ValidateDuplicates(receipt, warnings);

        // 4. Flag negative prices that aren't discounts
        ValidateNegativePrices(receipt, warnings);

        // 5. Detect missing VAT/tax information
        ValidateTaxInfo(receipt, warnings);

        return warnings;
    }
}
```

#### 3.3 Integration Point
```csharp
// In OCRManager.ProcessReceipt()
public OcrResult ProcessReceiptWithValidation(string imagePath)
{
    var receipt = ProcessReceipt(imagePath);
    var validator = new ReceiptValidator();
    var warnings = validator.Validate(receipt);
    
    return new OcrResult
    {
        Receipt = receipt,
        OverallConfidence = CalculateOverallConfidence(receipt),
        ProductsRequiringReview = warnings.Count,
        Warnings = warnings
    };
}
```

### Expected Outcome
- Automatic detection of OCR errors (impossible prices, missing products)
- Clear warnings for mobile app to display
- Prevents garbage data from entering the database

---

## Point 4: User Correction Learning Mechanism

### Goal
Enable the library to **learn from user corrections** over time, improving accuracy for future receipts, **without requiring a database or save mechanism in the library itself**.

### The Challenge
The library is a **stateless processing unit** - it doesn't own the database. The mobile app handles persistence. So how does the library "learn"?

### Solution: Multi-Layer Learning Architecture

#### Layer 1: Session-Based Learning (In-Memory)
```csharp
public class CorrectionSession
{
    // Stores corrections for current app session
    public Dictionary<string, string> ProductNameCorrections { get; } = new();
    public Dictionary<string, decimal> PriceCorrections { get; } = new();
    
    // Example: User corrects "QUES0 FRESC0" → "QUESO FRESCO"
    // Library stores: ProductNameCorrections["QUES0 FRESC0"] = "QUESO FRESCO"
    // Next time OCR extracts "QUES0 FRESC0", library auto-corrects
}
```

**How it works:**
1. Mobile app calls `library.ApplyCorrection(originalText, correctedText)`
2. Library stores correction in session dictionary
3. Future OCR results check dictionary before returning
4. Corrections last until app restarts

#### Layer 2: Persistent Correction File (Local File System)
```csharp
public class CorrectionStore
{
    private readonly string _correctionsFilePath;
    
    // Called by mobile app after user corrects a product
    public void SaveCorrection(string ocrText, string correctedText, string supermarket)
    {
        var correction = new CorrectionRecord
        {
            OcrText = ocrText,
            CorrectedText = correctedText,
            Supermarket = supermarket,
            Count = 1,
            LastUsed = DateTime.UtcNow
        };
        
        // Append to JSON file: corrections.json
        // {
        //   "CARREFOUR": {
        //     "QUES0 FRESC0": { "corrected": "QUESO FRESCO", "count": 5 }
        //   }
        // }
    }
    
    // Called during library initialization
    public void LoadCorrections()
    {
        // Load corrections.json into memory
        // Apply to all future OCR results
    }
}
```

**Mobile App Integration:**
```csharp
// In mobile app, after user corrects a product:
var ocrLibrary = new OCRManager();
ocrLibrary.LearningStore.SaveCorrection(
    ocrText: "QUES0 FRESC0",
    correctedText: "QUESO FRESCO",
    supermarket: "CARREFOUR"
);

// Next time user scans a receipt:
// Library automatically corrects "QUES0 FRESC0" → "QUESO FRESCO"
```

#### Layer 3: Custom Product Catalog (Learned Over Time)
```csharp
public class LearnedCatalog
{
    // Products the user has confirmed/corrected
    public HashSet<string> ConfirmedProducts { get; } = new();
    
    // Frequency map: how often each product appears
    public Dictionary<string, int> ProductFrequency { get; } = new();
    
    // Used for fuzzy matching with higher confidence
    public string FindBestMatch(string ocrProduct)
    {
        // First try exact match in learned catalog
        // Then try fuzzy match against learned catalog
        // Learned products have higher match priority than built-in catalog
    }
}
```

#### Layer 4: Correction-Driven Pattern Learning
```csharp
public class PatternLearner
{
    // Learn OCR error patterns specific to user's device/camera
    private readonly Dictionary<string, string> _ocrErrorPatterns = new();
    
    // Example: If user consistently corrects "115€" → "1,15€"
    // Library learns this pattern and auto-applies it
    
    public void LearnCorrection(string pattern, string correction)
    {
        _ocrErrorPatterns[pattern] = correction;
    }
    
    public string ApplyLearnedCorrections(string text)
    {
        foreach (var (pattern, correction) in _ocrErrorPatterns)
        {
            text = text.Replace(pattern, correction);
        }
        return text;
    }
}
```

### Complete Learning Flow Diagram

```
┌─────────────────────────────────────────────────────────────┐
│                    MOBILE APPLICATION                       │
│                                                             │
│  User scans receipt → Library processes → Shows results     │
│                                                             │
│  ┌─────────────────────────────────────────────────────┐   │
│  │  Products Detected:                                  │   │
│  │  ✓ QUESO FRESCO         €3.19  (95% confidence)     │   │
│  │  ⚠ QUES0 FRESC0         €3.19  (45% confidence)     │   │
│  │     [Review] [Correct]                              │   │
│  └─────────────────────────────────────────────────────┘   │
│                                                             │
│  User clicks [Correct] → Types "QUESO FRESCO" → Saves      │
│                                                             │
│  App calls: library.LearningStore.SaveCorrection(          │
│      "QUES0 FRESC0", "QUESO FRESCO", "CARREFOUR")          │
└─────────────────────────────────────────────────────────────┘
                            │
                            ▼
┌─────────────────────────────────────────────────────────────┐
│                    OCRReader LIBRARY                        │
│                                                             │
│  1. Saves to corrections.json (local file)                 │
│  2. Updates in-memory correction dictionary                │
│  3. Increments correction count                            │
│                                                             │
│  Next time user scans receipt:                             │
│  - OCR extracts "QUES0 FRESC0"                             │
│  - Library checks correction store                         │
│  - Finds match: "QUES0 FRESC0" → "QUESO FRESCO"           │
│  - Auto-corrects BEFORE returning to app                   │
│  - Confidence boosted from 45% to 90%                      │
└─────────────────────────────────────────────────────────────┘
```

### API Surface for Mobile App

```csharp
// 1. Initialize library with learning enabled
var ocrManager = new OCRManager(new LearningOptions
{
    EnableLearning = true,
    CorrectionsFilePath = Path.Combine(AppDataDir, "ocr_corrections.json"),
    MaxCorrections = 10000  // Limit file size
});

// 2. Process receipt (returns confidence scores)
var result = ocrManager.ProcessReceiptWithValidation(imagePath);

// 3. User corrects a product in mobile app
// Mobile app calls this after user saves correction:
ocrManager.LearningStore.RecordCorrection(
    originalOcrText: "QUES0 FRESC0",
    correctedText: "QUESO FRESCO",
    supermarket: "CARREFOUR",
    productName: "QUESO FRESCO"  // Final confirmed name
);

// 4. Library learns automatically
// - Future receipts auto-correct "QUES0 FRESC0" → "QUESO FRESCO"
// - Confidence increases for similar corrections
// - Learned catalog improves fuzzy matching

// 5. Export learned data (optional, for analytics)
var learnedData = ocrManager.LearningStore.ExportLearnedData();
// Mobile app can send this to cloud for aggregate learning across users
```

### Persistence Strategy

| Storage Type | Location | Purpose | Size |
|---|---|---|---|
| **Session Corrections** | Memory | Immediate auto-correction | <1MB |
| **corrections.json** | App local storage | Persistent corrections across sessions | <5MB |
| **learned_catalog.json** | App local storage | User's confirmed product catalog | <2MB |
| **error_patterns.json** | App local storage | Learned OCR error patterns | <1MB |

**Total additional storage**: ~8MB maximum (negligible for mobile)

### Expected Outcome
- **Day 1**: 80% accuracy (Tesseract baseline)
- **Week 1**: 85% accuracy (session learning kicks in)
- **Month 1**: 90%+ accuracy (persistent corrections accumulate)
- **Long-term**: 92-95% accuracy for user's typical shopping patterns

---

## Point 5: Multi-Pass Merging Strategy

### Goal
Intelligently combine Bronze, Silver, and Gold OCR results to maximize product detection.

### Current Problem
The current `MergeOCRResult.MergeOCRModels()` simply combines results, which can lead to:
- Duplicate products with different names
- Conflicting prices for same product
- No confidence-based prioritization

### Implementation

#### 5.1 Confidence-Weighted Merging
```csharp
public class SmartMerger
{
    public Receipt Merge(List<Receipt> receipts)
    {
        var allProducts = new Dictionary<string, MergedProduct>();
        
        foreach (var receipt in receipts)
        {
            foreach (var product in receipt.Products)
            {
                var key = NormalizeProductName(product.Name);
                
                if (allProducts.TryGetValue(key, out var existing))
                {
                    // Merge: keep highest confidence version
                    if (product.Confidence > existing.Confidence)
                    {
                        existing.UpdateWith(product);
                    }
                    else
                    {
                        existing.AddAlternative(product);
                    }
                }
                else
                {
                    allProducts[key] = new MergedProduct(product);
                }
            }
        }
        
        return BuildFinalReceipt(allProducts.Values);
    }
}
```

#### 5.2 Product Name Normalization
```csharp
public class ProductNormalizer
{
    public string Normalize(string productName)
    {
        // 1. Remove OCR artifacts
        productName = RemoveOcrArtifacts(productName);
        
        // 2. Apply learned corrections
        productName = ApplyLearnedCorrections(productName);
        
        // 3. Normalize spacing and case
        productName = Regex.Replace(productName, @"\s+", " ").Trim().ToUpper();
        
        // 4. Remove special characters that vary between OCR passes
        productName = Regex.Replace(productName, @"[^A-ZÁÉÍÓÚÑÜ0-9\s]", "");
        
        return productName;
    }
}
```

#### 5.3 Conflict Resolution Rules
```csharp
public class ConflictResolver
{
    public MergedProduct Resolve(List<ReceiptItem> alternatives)
    {
        // Priority 1: Highest confidence
        var best = alternatives.OrderByDescending(p => p.Confidence).First();
        
        // Priority 2: Most common price (median)
        var medianPrice = CalculateMedianPrice(alternatives);
        
        // Priority 3: Learned corrections override
        if (LearningStore.HasCorrection(best.Product))
        {
            best = best with { Product = LearningStore.GetCorrection(best.Product) };
        }
        
        return new MergedProduct
        {
            Name = best.Product,
            Price = medianPrice,
            Confidence = best.Confidence,
            Alternatives = alternatives.Skip(1).ToList()
        };
    }
}
```

### Expected Outcome
- Single consolidated product list from 3 OCR passes
- Highest-confidence version of each product selected
- Conflicts resolved intelligently (median price, learned corrections)
- No duplicate products in final output

---

## Implementation Order

1. **Phase 1** (Week 1): Confidence scoring + validation
   - Extend `ReceiptItem` with confidence metadata
   - Implement `PriceValidator` and `ReceiptValidator`
   - Add `OcrResult` wrapper with warnings

2. **Phase 2** (Week 2): Learning mechanism
   - Implement `CorrectionStore` with JSON persistence
   - Add `ApplyCorrection()` API for mobile app
   - Implement session-based auto-correction

3. **Phase 3** (Week 3): Smart merging
   - Refactor `MergeOCRResult` to use confidence-weighted merging
   - Add product name normalization
   - Implement conflict resolution

4. **Phase 4** (Week 4): Integration & testing
   - Test with all sample receipts
   - Simulate user correction flow
   - Measure accuracy improvement

---

## Files to Modify

| File | Changes |
|---|---|
| `ReceiptItem.cs` | Add `Confidence`, `Source`, `RequiresReview` fields |
| `Receipt.cs` | Add `OcrResult` wrapper class |
| `OCRModels/OcrBase.cs` | Add confidence tracking in `ExtractRawText()` |
| `OCRModels/BronzeOCR.cs` | Pass confidence from Tesseract to items |
| `OCRModels/SilverOCR.cs` | Adjust confidence after text cleaning |
| `OCRModels/GoldOCR.cs` | Boost confidence after catalog matching |
| `OCRModels/IOCRManager.cs` | Add `ProcessReceiptWithValidation()` method |
| `OCRModels/OCRManager.cs` | Implement validation + merging logic |
| **NEW**: `OCRModels/Learning/CorrectionStore.cs` | Persistent correction storage |
| **NEW**: `OCRModels/Learning/CorrectionSession.cs` | In-memory correction cache |
| **NEW**: `OCRModels/Validation/PriceValidator.cs` | Price range validation |
| **NEW**: `OCRModels/Validation/ReceiptValidator.cs` | Structural validation |
| **NEW**: `OCRModels/Merging/SmartMerger.cs` | Confidence-weighted merging |

---

## Backward Compatibility

- All existing APIs remain unchanged
- New functionality is opt-in via `ProcessReceiptWithValidation()`
- Existing `ProcessReceipt()` continues to work as before
- Learning is disabled by default (must be explicitly enabled)

---

## Mobile App Integration Notes

The mobile app needs to:

1. **Initialize learning** on app start:
   ```csharp
   ocrManager.LearningStore.Initialize(Path.Combine(AppDataDir, "ocr_learned"));
   ```

2. **Display confidence** in UI:
   ```csharp
   foreach (var product in result.Receipt.Products)
   {
       var icon = product.Confidence > 0.85 ? "✓" : "⚠";
       Console.WriteLine($"{icon} {product.Product} - {product.Price} ({product.Confidence:P0})");
   }
   ```

3. **Report corrections** after user edits:
   ```csharp
   // After user saves edited receipt:
   foreach (var correction in userCorrections)
   {
       ocrManager.LearningStore.RecordCorrection(
           correction.OcrText,
           correction.CorrectedText,
           result.Receipt.Supermarket
       );
   }
   ```

4. **Handle warnings**:
   ```csharp
   if (result.Warnings.Count > 0)
   {
       ShowReviewScreen(result.Warnings);
   }
   ```

---

## Success Metrics

| Metric | Before V3 | After V3 | Measurement |
|---|---|---|---|
| Products detected (Dia.jpeg) | 2 / 11 | 9+ / 11 | Manual count |
| Average confidence score | N/A | 0.85+ | Per-receipt average |
| User corrections needed | ~20% | <10% | App analytics |
| Accuracy after 1 week | 80% | 88% | User testing |
| Accuracy after 1 month | 80% | 92%+ | User testing |

---

## Risks & Mitigations

| Risk | Impact | Mitigation |
|---|---|---|
| Learning file grows too large | Medium | Implement max size limit + cleanup old corrections |
| Incorrect learning (bad corrections) | High | Require minimum confidence threshold before auto-applying |
| Mobile app doesn't implement correction flow | High | Provide reference implementation + clear API docs |
| Performance impact on mobile | Low | Learning is async, corrections cached in memory |
| Privacy concerns with learning data | Medium | All data stays local, no cloud sync unless user opts in |

---

## Next Steps

1. Review this document and provide feedback
2. Prioritize which phases to implement first
3. Define mobile app API contract for correction flow
4. Begin Phase 1 implementation after approval

/// <summary>Represents a single product line extracted from a receipt.</summary>
public record ReceiptItem(
    string Supermarket, 
    string Product, 
    decimal Price,
    double Confidence = 1.0,
    ExtractionSource Source = ExtractionSource.OcrDirect,
    bool RequiresReview = false);

/// <summary>Indicates the source of the product extraction.</summary>
public enum ExtractionSource
{
    OcrDirect,         // Directly from OCR text
    CatalogMatch,      // Fuzzy-matched to known product
    UserCorrected,     // Corrected by user
    Inferred,          // Inferred from context/total
    LowConfidence      // OCR confidence below threshold
}

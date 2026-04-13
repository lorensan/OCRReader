/// <summary>
/// Represents the complete result of an OCR operation, including confidence scores and validation warnings.
/// </summary>
public class OcrResult
{
    /// <summary>The parsed receipt with all extracted data.</summary>
    public Receipt Receipt { get; set; } = new();
    
    /// <summary>Overall confidence score for this OCR result (0.0 to 1.0).</summary>
    public double OverallConfidence { get; set; }
    
    /// <summary>Number of products that require user review.</summary>
    public int ProductsRequiringReview { get; set; }
    
    /// <summary>Validation warnings detected during processing.</summary>
    public List<ConfidenceWarning> Warnings { get; set; } = new();
    
    /// <summary>True if the result has warnings that should be reviewed.</summary>
    public bool HasWarnings => Warnings.Count > 0;
}

/// <summary>
/// Represents a warning about a specific product or extraction issue.
/// </summary>
public record ConfidenceWarning(
    string ProductName,
    double Confidence,
    string Reason,
    WarningType Type);

/// <summary>Type of validation warning.</summary>
public enum WarningType
{
    LowConfidence,       // OCR confidence below threshold
    PriceOutOfRange,     // Price seems unrealistic
    MissingProduct,      // Expected product not found
    DuplicateProduct,    // Same product detected multiple times
    TotalMismatch,       // Sum doesn't match receipt total
    StructuralIssue      // Receipt structure seems incomplete
}

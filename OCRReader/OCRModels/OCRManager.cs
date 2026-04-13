using System.Globalization;
using OCRReader.Merging;
using OCRReader.Validation;
using OCRReader.Learning;

/// <summary>
/// Manages the multi-pass OCR approach by running Bronze, Silver, and Gold OCR models
/// and merging their results into a single Receipt object.
/// </summary>
public class OCRManager : IOCRManager
{
    private readonly IReceiptOCR _bronzeOCR;
    private readonly IReceiptOCR _silverOCR;
    private readonly IReceiptOCR _goldOCR;
    private readonly SmartMerger _merger;
    private readonly ReceiptValidator _validator;

    /// <summary>Learning store for persistent corrections (optional).</summary>
    public CorrectionStore? LearningStore { get; set; }

    /// <summary>
    /// Creates an OCRManager with default OCR implementations.
    /// </summary>
    public OCRManager() : this(new BronzeOCR(), new SilverOCR(), new GoldOCR()) { }

    /// <summary>
    /// Creates an OCRManager with specified OCR implementations for dependency injection.
    /// </summary>
    public OCRManager(IReceiptOCR bronzeOCR, IReceiptOCR silverOCR, IReceiptOCR goldOCR)
    {
        _bronzeOCR = bronzeOCR ?? throw new ArgumentNullException(nameof(bronzeOCR));
        _silverOCR = silverOCR ?? throw new ArgumentNullException(nameof(silverOCR));
        _goldOCR = goldOCR ?? throw new ArgumentNullException(nameof(goldOCR));
        _merger = new SmartMerger();
        _validator = new ReceiptValidator();
    }

    /// <summary>
    /// Processes a receipt image through all OCR models and returns a merged Receipt.
    /// </summary>
    public Receipt ProcessReceipt(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
            throw new ArgumentException("Image path cannot be null or empty.", nameof(imagePath));

        if (!File.Exists(imagePath))
            throw new FileNotFoundException($"Image file not found: {imagePath}", imagePath);

        // Run all three OCR models
        var bronzeItems = _bronzeOCR.ProcessTicket(imagePath);
        var silverItems = _silverOCR.ProcessTicket(imagePath);
        var goldItems = _goldOCR.ProcessTicket(imagePath);

        // Merge results using smart merger
        var mergedItems = _merger.Merge(new[] { bronzeItems, silverItems, goldItems });

        // Build Receipt from merged items
        var receipt = MergeOCRResult.MergeOCRModels(
            bronzeItems, silverItems, goldItems);
        
        // Update products with merged items
        receipt.Products = mergedItems.Select(i => new Product 
        { 
            Name = i.Product, 
            Price = i.Price.ToString("0.00") 
        }).ToList();

        return receipt;
    }

    /// <summary>
    /// Processes a receipt image with confidence scoring and validation.
    /// </summary>
    public OcrResult ProcessReceiptWithValidation(string imagePath)
    {
        var receipt = ProcessReceipt(imagePath);
        var warnings = _validator.Validate(receipt);
        
        // Calculate overall confidence
        var avgConfidence = receipt.Products.Count > 0 
            ? receipt.Products.Average(p => 0.85) // Default confidence
            : 0.0;
        
        return new OcrResult
        {
            Receipt = receipt,
            OverallConfidence = avgConfidence,
            ProductsRequiringReview = warnings.Count(w => 
                w.Type == WarningType.LowConfidence || w.Type == WarningType.PriceOutOfRange),
            Warnings = warnings
        };
    }
}

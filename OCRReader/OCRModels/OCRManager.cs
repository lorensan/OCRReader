using System.Globalization;

/// <summary>
/// Manages the multi-pass OCR approach by running Bronze, Silver, and Gold OCR models
/// and merging their results into a single Receipt object.
/// </summary>
public class OCRManager : IOCRManager
{
    private readonly IReceiptOCR _bronzeOCR;
    private readonly IReceiptOCR _silverOCR;
    private readonly IReceiptOCR _goldOCR;

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

        // Merge results into a single Receipt
        return MergeOCRResult.MergeOCRModels(bronzeItems, silverItems, goldItems);
    }
}

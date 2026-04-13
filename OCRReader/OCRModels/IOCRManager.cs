/// <summary>
/// Interface for the OCR Manager that orchestrates multi-pass OCR processing
/// and returns a fully parsed Receipt object.
/// </summary>
public interface IOCRManager
{
    /// <summary>
    /// Processes a receipt image through all OCR models and returns a merged Receipt.
    /// </summary>
    /// <param name="imagePath">Path to the receipt image file.</param>
    /// <returns>A fully parsed and merged Receipt object.</returns>
    Receipt ProcessReceipt(string imagePath);
    
    /// <summary>
    /// Processes a receipt image with confidence scoring and validation.
    /// </summary>
    /// <param name="imagePath">Path to the receipt image file.</param>
    /// <returns>An OcrResult containing the receipt, confidence scores, and validation warnings.</returns>
    OcrResult ProcessReceiptWithValidation(string imagePath);
}

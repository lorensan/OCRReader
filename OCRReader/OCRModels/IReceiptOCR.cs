/// <summary>
/// Common interface for Receipt OCR processing at different accuracy levels.
/// Each implementation applies progressively more sophisticated heuristics.
/// </summary>
public interface IReceiptOCR
{
    /// <summary>
    /// Processes a receipt image and extracts product items.
    /// </summary>
    /// <param name="imagePath">Path to the receipt image file.</param>
    /// <returns>A list of parsed receipt items.</returns>
    List<ReceiptItem> ProcessTicket(string imagePath);
}

/// <summary>
/// Bronze-level OCR: basic Tesseract text recognition with simple
/// line-by-line regex parsing. Reads text, detects prices, and
/// associates them with the nearest product name.
/// </summary>
class BronzeOCR : OcrBase
{
    public BronzeOCR(string tessDataPath = @"./tessdata") : base(tessDataPath) { }

    public override List<ReceiptItem> ProcessTicket(string imagePath)
    {
        var (text, _) = ExtractRawText(imagePath);
        string supermarket = ExtractSupermarket(text);
        return BasicParse(text, supermarket);
    }
}

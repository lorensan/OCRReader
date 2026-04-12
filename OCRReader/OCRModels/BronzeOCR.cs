/// <summary>
/// Bronze-level OCR: basic multi-pass Tesseract with line-by-line parsing.
/// </summary>
public class BronzeOCR : OcrBase
{
    public BronzeOCR(string tessDataPath = @"./tessdata") : base(tessDataPath) { }

    public override List<ReceiptItem> ProcessTicket(string imagePath)
    {
        var (text, _) = ExtractRawTextMultiPass(imagePath);
        string supermarket = ExtractSupermarket(text);
        return BasicParse(text, supermarket);
    }
}

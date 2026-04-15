using System.Globalization;
using System.Text.RegularExpressions;
using Tesseract;
using SkiaSharp;
using System.IO;

/// <summary>
/// Base class providing shared Tesseract OCR extraction, image preprocessing,
/// supermarket detection, metadata extraction, and common parsing helpers.
/// </summary>
public abstract class OcrBase : IReceiptOCR
{
    protected readonly string TessDataPath;
    
    /// <summary>Last OCR confidence score (0.0 to 1.0).</summary>
    protected double LastOcrConfidence { get; private set; }

    protected OcrBase(string tessDataPath = @"./tessdata")
    {
        TessDataPath = tessDataPath;
    }

    public abstract List<ReceiptItem> ProcessTicket(string imagePath);

    // ── Debug helper ─────────────────────────────────────────────────────────

    public string DebugExtractRawText(string imagePath)
    {
        var (text, confidence) = ExtractRawText(imagePath);
        return $"[Confidence: {confidence:P2}]\n\n{text}";
    }

    /// <summary>
    /// Debug method to save preprocessed image to disk for analysis.
    /// </summary>
    public void SavePreprocessedImage(string imagePath, string outputPath)
    {
        if (!File.Exists(imagePath))
            throw new FileNotFoundException($"Image file not found: {imagePath}");

        using var original = SKBitmap.Decode(imagePath);
        using var preprocessed = PreprocessForDebug(original);
        using var encoded = preprocessed.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(outputPath, encoded.ToArray());
    }

    private static SKBitmap PreprocessForDebug(SKBitmap original)
    {
        // Same as Preprocess but returns the intermediate grayscale image for debugging
        int maxInputSide = 2000;
        int maxOrigSide = Math.Max(original.Width, original.Height);
        SKBitmap limited;
        if (maxOrigSide > maxInputSide)
        {
            float scale = (float)maxInputSide / maxOrigSide;
            int newW = Math.Max(100, (int)(original.Width * scale));
            int newH = Math.Max(100, (int)(original.Height * scale));
            limited = Resize(original, newW, newH);
        }
        else
        {
            limited = original.Copy();
        }

        using var _limited = limited;
        using var gray = ToGrayscaleFast(limited);
        using var deskewed = gray.Width > 100 && gray.Height > 100 ? DeskewFast(gray) : gray.Copy();
        using var upscaled = Upscale(deskewed, 1200);

        // Try multiple thresholds and save all
        return SimpleThresholdFast(upscaled);
    }

    // ── Tesseract OCR extraction ─────────────────────────────────────────────

    protected (string Text, double Confidence) ExtractRawText(string imagePath)
    {
        return ExtractRawText(imagePath, PageSegMode.Auto);
    }

    protected (string Text, double Confidence) ExtractRawText(string imagePath, PageSegMode pageSegMode)
    {
        if (!File.Exists(imagePath))
            throw new FileNotFoundException($"Image file not found: {imagePath}");

        using var image = SKBitmap.Decode(imagePath);
        using var processed = Preprocess(image);

        using var engine = new TesseractEngine(TessDataPath, "spa+eng", EngineMode.LstmOnly);
        engine.SetVariable("preserve_interword_spaces", "1");
        // Set resolution explicitly to avoid "Estimating resolution" message
        engine.SetVariable("user_defined_dpi", "300");

        using var ms = new MemoryStream();
        processed.Encode(ms, SKEncodedImageFormat.Png, 100);
        ms.Position = 0;
        using var pix = Pix.LoadFromMemory(ms.ToArray());

        using var page = engine.Process(pix, pageSegMode);
        return (page.GetText(), page.GetMeanConfidence());
    }

    /// <summary>
    /// Multi-pass OCR with different page segmentation modes.
    /// </summary>
    protected (string Text, double Confidence) ExtractRawTextMultiPass(string imagePath)
    {
        var modes = new[]
        {
            PageSegMode.Auto,
            PageSegMode.SingleBlock,
            PageSegMode.SingleColumn,
            PageSegMode.SparseText,
            // Add table-optimized modes for receipts like Dia's
            PageSegMode.SingleBlockVertText
        };

        string bestText = "";
        double bestConfidence = 0;

        foreach (var mode in modes)
        {
            try
            {
                var (text, confidence) = ExtractRawText(imagePath, mode);
                // Prefer longer text with reasonable confidence
                double score = confidence * text.Length;
                if (score > bestConfidence * Math.Max(bestText.Length, 1))
                {
                    bestConfidence = confidence;
                    bestText = text;
                }
            }
            catch { }
        }

        LastOcrConfidence = bestConfidence;
        return (bestText, bestConfidence);
    }

    // ── Supermarket detection ────────────────────────────────────────────────

    protected virtual string ExtractSupermarket(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines.Take(6))
        {
            string upper = line.ToUpperInvariant();
            foreach (string market in KnownSupermarkets)
                if (upper.Contains(market))
                    return market;
        }
        return lines.FirstOrDefault(l => l.Trim().Length > 3)?.Trim() ?? "DESCONOCIDO";
    }

    // ── Metadata extraction ──────────────────────────────────────────────────

    protected static (string? Total, string? Date) ExtractMetadata(string text)
    {
        string? total = null;
        string? date = null;

        var totalMatch = Regex.Match(text,
            @"TOTAL\s*(?:A\s+PAGAR)?\s*[:\-]?\s*€?\s*([\d]+[,\.]\d{2})",
            RegexOptions.IgnoreCase);
        if (totalMatch.Success)
            total = totalMatch.Groups[1].Value;

        var dateMatch = Regex.Match(text,
            @"\b(\d{2}[/\-\.]\d{2}[/\-\.]\d{2,4})(?:\s+(\d{2}:\d{2}(?::\d{2})?))?\b");
        if (dateMatch.Success)
            date = dateMatch.Value.Trim();

        return (total, date);
    }

    // ── Basic line-by-line parsing ───────────────────────────────────────────

    protected static List<ReceiptItem> BasicParse(string text, string supermarket, double confidence = 0.80)
    {
        var items = new List<ReceiptItem>();
        string? pending = null;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length < 2) continue;

            // Pattern A: "N x ( PRICE )"
            var qtyMatch = Regex.Match(line,
                @"^(\d+)\s*[xX×]\s*\(?\s*([\d]+[,\.][\d]{2})\s*\)?");
            if (qtyMatch.Success && pending != null)
            {
                if (int.TryParse(qtyMatch.Groups[1].Value, out int qty) &&
                    decimal.TryParse(qtyMatch.Groups[2].Value.Replace(',', '.'),
                        NumberStyles.Number, CultureInfo.InvariantCulture, out decimal unit))
                {
                    items.Add(new ReceiptItem(supermarket, pending, qty * unit, confidence * 0.9));
                    pending = null;
                }
                continue;
            }

            // Pattern B: "PRODUCT   PRICE" (2+ spaces)
            var sameLineMatch = Regex.Match(line,
                @"^(?:\d+\s+)?(.+?)\s{2,}([\d]+[,\.][\d]{2})\s*€?\s*$");
            if (sameLineMatch.Success)
            {
                string name = sameLineMatch.Groups[1].Value.Trim();
                string priceStr = sameLineMatch.Groups[2].Value.Replace(',', '.');
                if (!IsSkippable(name) &&
                    decimal.TryParse(priceStr,
                        NumberStyles.Number, CultureInfo.InvariantCulture, out decimal price))
                {
                    items.Add(new ReceiptItem(supermarket, name, price, confidence));
                    pending = null;
                    continue;
                }
            }

            // Pattern D: Table format - "PRODUCT   QTY   PRICE   TOTAL" (Dia-style receipts)
            // Matches lines like: "PETIT FRESA 6 X 50 G  1ud  0,99€  0,99€ A"
            // or: "BANANA  1,550kg  1,11 €/kg  1,72€ A"
            // Also handles OCR errors where commas are lost: "115€" instead of "1,15€"
            // Try multiple variations for robustness
            var tableMatch = Regex.Match(line,
                @"^([A-ZÁÉÍÓÚÑÜ][A-ZÁÉÍÓÚÑÜ\s\&\.\-\%\d]+?)\s+(\d[\d\,\.]*\s*(?:ud|kg|u\.?d\.?)?)\s+([\d]+[,\.]?[\d]{2})\s*€?\s*(?:€?/kg)?\s+([\d]+[,\.]?[\d]{2})\s*€?\s*[A-Z]?\s*$",
                RegexOptions.IgnoreCase);

            // Fallback pattern D2: Simpler table format without strict column structure
            // Matches: "PRODUCT  QTY  PRICE  TOTAL" where columns might have varying spacing
            if (!tableMatch.Success)
            {
                tableMatch = Regex.Match(line,
                    @"^([A-ZÁÉÍÓÚÑÜ][A-ZÁÉÍÓÚÑÜ\s\&\.\-\%\d'\(\)]{3,}?)\s+(\d[\d\,\.]*\s*(?:ud|kg|u\.?d\.?)?)\s+([\d]+[,\.]?[\d]{2})\s*€?\s+([\d]+[,\.]?[\d]{2})\s*€",
                    RegexOptions.IgnoreCase);
            }

            if (tableMatch.Success)
            {
                string name = tableMatch.Groups[1].Value.Trim();
                string totalStr = tableMatch.Groups[4].Value;

                // Fix OCR errors: if no comma/period, insert one (e.g., "115" → "1,15")
                if (!totalStr.Contains(',') && !totalStr.Contains('.'))
                {
                    if (totalStr.Length >= 3)
                        totalStr = totalStr.Insert(totalStr.Length - 2, ",");
                }

                if (!IsSkippable(name) && name.Length >= 3 &&
                    decimal.TryParse(totalStr.Replace(',', '.'),
                        NumberStyles.Number, CultureInfo.InvariantCulture, out decimal price) && price > 0 && price < 10000)
                {
                    items.Add(new ReceiptItem(supermarket, name, price, confidence * 0.95)); // Table format is reliable
                    pending = null;
                    continue;
                }
            }

            // Pattern C: standalone price
            var standaloneMatch = Regex.Match(line, @"^([\d]+[,\.][\d]{2})\s*€?\s*$");
            if (standaloneMatch.Success && pending != null)
            {
                if (decimal.TryParse(standaloneMatch.Groups[1].Value.Replace(',', '.'),
                        NumberStyles.Number, CultureInfo.InvariantCulture, out decimal price))
                {
                    items.Add(new ReceiptItem(supermarket, pending, price, confidence * 0.85));
                    pending = null;
                }
                continue;
            }

            // No price — keep as pending
            pending = IsSkippable(line) ? null : line;
        }

        return items;
    }

    // ── Preprocessing: grayscale → deskew → upscale → threshold ──────────────

    private static SKBitmap Preprocess(SKBitmap original)
    {
        // Limit input size first to avoid processing huge images
        int maxInputSide = 2000;
        int maxOrigSide = Math.Max(original.Width, original.Height);
        SKBitmap limited;
        if (maxOrigSide > maxInputSide)
        {
            float scale = (float)maxInputSide / maxOrigSide;
            int newW = Math.Max(100, (int)(original.Width * scale));
            int newH = Math.Max(100, (int)(original.Height * scale));
            limited = Resize(original, newW, newH);
        }
        else
        {
            limited = original.Copy();
        }

        using var _limited = limited;
        using var gray = ToGrayscaleFast(limited);

        // Deskew only if image is large enough
        using var deskewed = gray.Width > 100 && gray.Height > 100 ? DeskewFast(gray) : gray.Copy();

        // Reduce upscale target to reasonable size for Tesseract
        using var upscaled = Upscale(deskewed, 1200);

        // Try multiple threshold strategies and return the one most likely to preserve text
        return SmartThreshold(upscaled);
    }

    /// <summary>
    /// Intelligent threshold selection: tries multiple approaches and picks the best.
    /// </summary>
    private static SKBitmap SmartThreshold(SKBitmap src)
    {
        // Strategy 1: Try fixed threshold of 180 (original - proven to work)
        var fixedResult = TryFixedThreshold(src, 180);
        var fixedScore = ScoreThresholdResult(fixedResult);

        // Strategy 2: Try fixed threshold of 150 (more conservative)
        var conservativeResult = TryFixedThreshold(src, 150);
        var conservativeScore = ScoreThresholdResult(conservativeResult);

        // Strategy 3: Try Otsu's method
        var otsuResult = TryOtsuThreshold(src);
        var otsuScore = ScoreThresholdResult(otsuResult);

        // Pick the best scoring result
        var bestScore = Math.Max(fixedScore, Math.Max(conservativeScore, otsuScore));

        if (bestScore == fixedScore)
        {
            conservativeResult.Dispose();
            otsuResult.Dispose();
            return fixedResult;
        }
        else if (bestScore == conservativeScore)
        {
            fixedResult.Dispose();
            otsuResult.Dispose();
            return conservativeResult;
        }
        else
        {
            fixedResult.Dispose();
            conservativeResult.Dispose();
            return otsuResult;
        }
    }

    private static SKBitmap TryOtsuThreshold(SKBitmap src)
    {
        int w = src.Width, h = src.Height;
        var result = new SKBitmap(w, h, SKColorType.Rgb888x, SKAlphaType.Opaque);

        var srcInfo = src.PeekPixels();
        var dstInfo = result.PeekPixels();

        if (srcInfo != null && dstInfo != null)
        {
            var srcBytes = srcInfo.GetPixelSpan();
            var dstBytes = dstInfo.GetPixelSpan();
            int totalPixels = w * h;

            var histogram = new int[256];
            unsafe
            {
                fixed (byte* srcPtr = srcBytes)
                {
                    for (int i = 0; i < totalPixels; i++)
                    {
                        int idx = i * 4;
                        byte r = srcPtr[idx + 2];
                        byte g = srcPtr[idx + 1];
                        byte b = srcPtr[idx];
                        histogram[(r + g + b) / 3]++;
                    }
                }
            }

            int threshold = CalculateOtsuThreshold(histogram, totalPixels);
            threshold = Math.Max(100, Math.Min(220, threshold));
            int blackCount = 0;

            unsafe
            {
                fixed (byte* srcPtr = srcBytes, dstPtr = dstBytes)
                {
                    for (int i = 0; i < totalPixels; i++)
                    {
                        int idx = i * 4;
                        byte r = srcPtr[idx + 2];
                        byte g = srcPtr[idx + 1];
                        byte b = srcPtr[idx];
                        byte gray = (byte)((r + g + b) / 3);
                        byte val = gray < threshold ? (byte)0 : (byte)255;
                        if (val == 0) blackCount++;

                        dstPtr[idx] = val;
                        dstPtr[idx + 1] = val;
                        dstPtr[idx + 2] = val;
                        dstPtr[idx + 3] = 0;
                    }
                }
            }

            if (blackCount < totalPixels * 0.001)
            {
                result.Dispose();
                return src.Copy();
            }
        }

        return result;
    }

    private static SKBitmap TryFixedThreshold(SKBitmap src, int threshold)
    {
        int w = src.Width, h = src.Height;
        var result = new SKBitmap(w, h, SKColorType.Rgb888x, SKAlphaType.Opaque);

        var srcInfo = src.PeekPixels();
        var dstInfo = result.PeekPixels();

        if (srcInfo != null && dstInfo != null)
        {
            var srcBytes = srcInfo.GetPixelSpan();
            var dstBytes = dstInfo.GetPixelSpan();
            int totalPixels = w * h;
            int blackCount = 0;

            unsafe
            {
                fixed (byte* srcPtr = srcBytes, dstPtr = dstBytes)
                {
                    for (int i = 0; i < totalPixels; i++)
                    {
                        int idx = i * 4;
                        byte r = srcPtr[idx + 2];
                        byte g = srcPtr[idx + 1];
                        byte b = srcPtr[idx];
                        byte gray = (byte)((r + g + b) / 3);
                        byte val = gray < threshold ? (byte)0 : (byte)255;
                        if (val == 0) blackCount++;

                        dstPtr[idx] = val;
                        dstPtr[idx + 1] = val;
                        dstPtr[idx + 2] = val;
                        dstPtr[idx + 3] = 0;
                    }
                }
            }

            if (blackCount < totalPixels * 0.001)
            {
                result.Dispose();
                return src.Copy();
            }
        }

        return result;
    }

    /// <summary>
    /// Score a thresholded image based on how well it preserves text structure.
    /// Higher score = better text preservation.
    /// </summary>
    private static double ScoreThresholdResult(SKBitmap bitmap)
    {
        int w = bitmap.Width, h = bitmap.Height;
        var info = bitmap.PeekPixels();
        if (info == null) return 0;

        var pixels = info.GetPixelSpan();
        int totalPixels = w * h;
        int blackCount = 0;
        int horizontalEdges = 0;
        int verticalEdges = 0;

        unsafe
        {
            fixed (byte* ptr = pixels)
            {
                for (int i = 0; i < totalPixels; i++)
                {
                    int idx = i * 4;
                    if (ptr[idx] == 0) blackCount++; // Black pixel

                    // Count horizontal edges (transitions from black to white)
                    if (i % w > 0)
                    {
                        int prevIdx = (i - 1) * 4;
                        if ((ptr[idx] == 0 && ptr[prevIdx] == 255) ||
                            (ptr[idx] == 255 && ptr[prevIdx] == 0))
                            horizontalEdges++;
                    }

                    // Count vertical edges
                    if (i >= w)
                    {
                        int aboveIdx = (i - w) * 4;
                        if ((ptr[idx] == 0 && ptr[aboveIdx] == 255) ||
                            (ptr[idx] == 255 && ptr[aboveIdx] == 0))
                            verticalEdges++;
                    }
                }
            }
        }

        double blackRatio = blackCount / (double)totalPixels;
        double edgeRatio = (horizontalEdges + verticalEdges) / (double)(totalPixels * 2);

        // Good text images have:
        // - 5-30% black pixels (text vs background)
        // - High edge density (sharp text boundaries)
        double score = 0;
        if (blackRatio >= 0.05 && blackRatio <= 0.30)
            score += 50;
        else if (blackRatio >= 0.01 && blackRatio <= 0.50)
            score += 25;

        score += edgeRatio * 1000; // Edge density bonus

        return score;
    }

    /// <summary>
    /// Calculate optimal threshold using Otsu's method.
    /// </summary>
    private static int CalculateOtsuThreshold(int[] histogram, int totalPixels)
    {
        double sum = 0;
        for (int i = 0; i < 256; i++)
            sum += i * histogram[i];

        double sumB = 0;
        int wB = 0;
        double varMax = 0;
        int threshold = 128;

        for (int t = 0; t < 256; t++)
        {
            wB += histogram[t];
            if (wB == 0) continue;

            int wF = totalPixels - wB;
            if (wF == 0) break;

            sumB += t * histogram[t];

            double mB = sumB / wB;
            double mF = (sum - sumB) / wF;

            double varBetween = wB * wF * (mB - mF) * (mB - mF);

            if (varBetween > varMax)
            {
                varMax = varBetween;
                threshold = t;
            }
        }

        return threshold;
    }

    /// <summary>
    /// Simple fixed threshold - more reliable than Otsu for receipts.
    /// Optimized with direct pixel buffer access for speed.
    /// </summary>
    private static SKBitmap SimpleThresholdFast(SKBitmap src)
    {
        int w = src.Width, h = src.Height;
        var result = new SKBitmap(w, h, SKColorType.Rgb888x, SKAlphaType.Opaque);

        var srcInfo = src.PeekPixels();
        var dstInfo = result.PeekPixels();
        
        if (srcInfo != null && dstInfo != null)
        {
            var srcBytes = srcInfo.GetPixelSpan();
            var dstBytes = dstInfo.GetPixelSpan();

            const int threshold = 180;
            int blackCount = 0;
            int totalPixels = w * h;

            unsafe
            {
                fixed (byte* srcPtr = srcBytes, dstPtr = dstBytes)
                {
                    // RGB888x is 4 bytes per pixel (BGRX)
                    for (int i = 0; i < totalPixels; i++)
                    {
                        int idx = i * 4;
                        byte b = srcPtr[idx];
                        byte g = srcPtr[idx + 1];
                        byte r = srcPtr[idx + 2];
                        
                        // Fast grayscale: average is close enough for thresholding
                        byte gray = (byte)((r + g + b) / 3);
                        byte val = gray < threshold ? (byte)0 : (byte)255;
                        if (val == 0) blackCount++;
                        
                        dstPtr[idx] = val;
                        dstPtr[idx + 1] = val;
                        dstPtr[idx + 2] = val;
                        dstPtr[idx + 3] = 0; // X byte
                    }
                }
            }

            // If too few black pixels (< 0.1%), return grayscale instead
            if (blackCount < totalPixels * 0.001)
            {
                result.Dispose();
                return src.Copy();
            }
        }
        else
        {
            // Fallback to slow method if PeekPixels fails
            return SimpleThresholdFallback(src);
        }

        return result;
    }

    private static SKBitmap SimpleThresholdFallback(SKBitmap src)
    {
        int w = src.Width, h = src.Height;
        var result = new SKBitmap(w, h, SKColorType.Rgb888x, SKAlphaType.Opaque);
        const int threshold = 180;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                var pixel = src.GetPixel(x, y);
                byte gray = (byte)(pixel.Red * 0.2126 + pixel.Green * 0.7152 + pixel.Blue * 0.0722);
                byte val = gray < threshold ? (byte)0 : (byte)255;
                result.SetPixel(x, y, new SKColor(val, val, val));
            }
        }
        return result;
    }

    // ── Step 1: Grayscale (fast version) ─────────────────────────────────────

    private static SKBitmap ToGrayscaleFast(SKBitmap src)
    {
        int w = src.Width, h = src.Height;
        var result = new SKBitmap(w, h, SKColorType.Rgb888x, SKAlphaType.Opaque);

        var srcInfo = src.PeekPixels();
        var dstInfo = result.PeekPixels();
        
        if (srcInfo != null && dstInfo != null)
        {
            var srcBytes = srcInfo.GetPixelSpan();
            var dstBytes = dstInfo.GetPixelSpan();
            int totalPixels = w * h;

            unsafe
            {
                fixed (byte* srcPtr = srcBytes, dstPtr = dstBytes)
                {
                    for (int i = 0; i < totalPixels; i++)
                    {
                        int idx = i * 4;
                        byte b = srcPtr[idx];
                        byte g = srcPtr[idx + 1];
                        byte r = srcPtr[idx + 2];
                        
                        // Fast grayscale: approximation of 0.299R + 0.587G + 0.114B
                        byte gray = (byte)((r * 77 + g * 151 + b * 28) >> 8);
                        
                        dstPtr[idx] = gray;
                        dstPtr[idx + 1] = gray;
                        dstPtr[idx + 2] = gray;
                        dstPtr[idx + 3] = 0;
                    }
                }
            }
        }
        else
        {
            // Fallback
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    var pixel = src.GetPixel(x, y);
                    byte luma = (byte)(pixel.Red * 0.2126 + pixel.Green * 0.7152 + pixel.Blue * 0.0722);
                    result.SetPixel(x, y, new SKColor(luma, luma, luma));
                }
            }
        }

        return result;
    }

    // ── Step 2: Deskew (fast version) ────────────────────────────────────────

    private static SKBitmap DeskewFast(SKBitmap src)
    {
        int origW = src.Width, origH = src.Height;
        
        // Work on a smaller image for speed
        int smallSide = Math.Min(Math.Min(origW, origH), 300);
        using var small = Resize(src, 
            Math.Min(origW, smallSide), 
            Math.Min(origH, smallSide));

        double bestAngle = 0;
        int maxVariance = 0;

        // Coarse search: fewer angles
        for (int angleDeg = -30; angleDeg <= 30; angleDeg += 3)
        {
            double angle = angleDeg / 10.0;
            double radians = angle * Math.PI / 180.0;
            int variance = CalculateProjectionVarianceFast(small, radians);
            if (variance > maxVariance)
            {
                maxVariance = variance;
                bestAngle = angle;
            }
        }

        // Fine-tune around best angle
        double startFine = (bestAngle - 0.3) * 10;
        double endFine = (bestAngle + 0.3) * 10;
        for (int angleDeg = (int)startFine; angleDeg <= (int)endFine; angleDeg++)
        {
            double angle = angleDeg / 10.0;
            double radians = angle * Math.PI / 180.0;
            int variance = CalculateProjectionVarianceFast(small, radians);
            if (variance > maxVariance)
            {
                maxVariance = variance;
                bestAngle = angle;
            }
        }

        if (Math.Abs(bestAngle) > 0.5)
            return RotateImage(src, (float)-bestAngle);

        return src.Copy();
    }

    private static int CalculateProjectionVarianceFast(SKBitmap bmp, double radians)
    {
        int w = bmp.Width, h = bmp.Height;
        var projection = new int[w];
        double cx = w / 2.0, cy = h / 2.0;
        double cos = Math.Cos(radians);
        double sin = Math.Sin(radians);

        var info = bmp.PeekPixels();
        if (info != null)
        {
            var pixels = info.GetPixelSpan();
            // Sample every 2nd row for speed
            for (int y = 0; y < h; y += 2)
            {
                for (int x = 0; x < w; x++)
                {
                    double dx = x - cx, dy = y - cy;
                    int rx = (int)(cx + dx * cos - dy * sin);
                    int ry = (int)(cy + dx * sin + dy * cos);
                    if (rx >= 0 && rx < w && ry >= 0 && ry < h)
                    {
                        int idx = (ry * w + rx) * 4;
                        byte b = pixels[idx];
                        byte g = pixels[idx + 1];
                        byte r = pixels[idx + 2];
                        byte gray = (byte)((r + g + b) / 3);
                        if (gray < 128)
                            projection[rx]++;
                    }
                }
            }
        }
        else
        {
            // Fallback
            for (int y = 0; y < h; y += 2)
            {
                for (int x = 0; x < w; x++)
                {
                    double dx = x - cx, dy = y - cy;
                    int rx = (int)(cx + dx * cos - dy * sin);
                    int ry = (int)(cy + dx * sin + dy * cos);
                    if (rx >= 0 && rx < w && ry >= 0 && ry < h)
                    {
                        var pixel = bmp.GetPixel(rx, ry);
                        byte gray = (byte)((pixel.Red + pixel.Green + pixel.Blue) / 3);
                        if (gray < 128)
                            projection[rx]++;
                    }
                }
            }
        }

        double mean = projection.Sum() / (double)w;
        double variance = 0;
        foreach (int count in projection)
            variance += (count - mean) * (count - mean);

        return (int)(variance / w);
    }

    private static SKBitmap RotateImage(SKBitmap src, float angle)
    {
        int w = src.Width, h = src.Height;
        var dst = new SKBitmap(w, h, SKColorType.Rgb888x, SKAlphaType.Opaque);

        using var canvas = new SKCanvas(dst);
        canvas.Clear(SKColors.White);
        canvas.Translate(w / 2f, h / 2f);
        canvas.RotateDegrees(angle);
        canvas.Translate(-w / 2f, -h / 2f);
        canvas.DrawBitmap(src, 0, 0);

        return dst;
    }

    // ── Step 3: Upscale ──────────────────────────────────────────────────────

    private static SKBitmap Upscale(SKBitmap src, int targetMinSide)
    {
        int minSide = Math.Min(src.Width, src.Height);
        if (minSide >= targetMinSide)
            return src.Copy();

        float scale = (float)targetMinSide / minSide;
        int newW = Math.Max(100, (int)(src.Width * scale));
        int newH = Math.Max(100, (int)(src.Height * scale));

        return Resize(src, newW, newH);
    }

    // ── Resize helper ────────────────────────────────────────────────────────

    private static SKBitmap Resize(SKBitmap src, int newW, int newH)
    {
        var dst = new SKBitmap(newW, newH, SKColorType.Rgb888x, SKAlphaType.Opaque);
        
        using var canvas = new SKCanvas(dst);
        using var paint = new SKPaint
        {
            FilterQuality = SKFilterQuality.High
        };
        canvas.DrawBitmap(src, new SKRect(0, 0, newW, newH), paint);
        
        return dst;
    }

    // ── Shared constants ─────────────────────────────────────────────────────

    protected static readonly string[] KnownSupermarkets =
    [
        "CARREFOUR", "MERCADONA", "LIDL", "ALDI", "DIA", "EROSKI",
        "ALCAMPO", "EL CORTE INGLÉS", "SUPERCOR", "SPAR", "CONSUM",
        "CONDIS", "BONPREU", "CAPRABO", "HIPERCOR", "GADIS", "FAMILY CASH"
    ];

    protected static readonly HashSet<string> SkipKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "TOTAL", "SUBTOTAL", "IVA", "DESCUENTO", "DTO.", "AHORRO", "CAMBIO",
        "EFECTIVO", "TARJETA", "BASE IMPONIBLE", "IMPORTE", "A PAGAR", "FACTURA",
        // Table headers (Dia-style receipts)
        "PRODUCTOS VENDIDOS", "DESCRIPCIÓN", "CANTIDAD", "PRECIO KG", "TOTAL VENTA",
        "RESUMEN DE LA COMPRA", "FORMA DE PAGO", "DATOS DE LA OPERACIÓN",
        "TIPO", "CUOTA", "VENTA", "OPERACION CONTACTLESS",
        // Tax breakdown
        "DESGLOSE", "BASE", "IVA", "ENTREGA",
        // Footer text
        "GRACIAS", "DEVOLUCIONES", "ATENDIDO", "VISITA"
    };

    protected static bool IsSkippable(string name) =>
        name.Length < 2 ||
        SkipKeywords.Any(kw => name.StartsWith(kw, StringComparison.OrdinalIgnoreCase)) ||
        Regex.IsMatch(name, @"^[\d\s,\.\-\*\/\(\)x×]+$");

    // ── Levenshtein distance ─────────────────────────────────────────────────

    protected static int LevenshteinDistance(string a, string b)
    {
        if (string.IsNullOrEmpty(a)) return b?.Length ?? 0;
        if (string.IsNullOrEmpty(b)) return a.Length;

        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = char.ToUpperInvariant(a[i - 1]) == char.ToUpperInvariant(b[j - 1]) ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[b.Length];
    }
}

using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Tesseract;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

/// <summary>
/// Base class providing shared Tesseract OCR extraction, image preprocessing,
/// supermarket detection, metadata extraction, and common parsing helpers.
/// </summary>
public abstract class OcrBase : IReceiptOCR
{
    protected readonly string TessDataPath;

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

    // ── Tesseract OCR extraction ─────────────────────────────────────────────

    protected (string Text, double Confidence) ExtractRawText(string imagePath)
    {
        return ExtractRawText(imagePath, PageSegMode.Auto);
    }

    protected (string Text, double Confidence) ExtractRawText(string imagePath, PageSegMode pageSegMode)
    {
        if (!File.Exists(imagePath))
            throw new FileNotFoundException($"Image file not found: {imagePath}");

        using var image = new Bitmap(imagePath);
        using var processed = Preprocess(image);

        using var engine = new TesseractEngine(TessDataPath, "spa+eng", EngineMode.LstmOnly);
        engine.SetVariable("preserve_interword_spaces", "1");

        using var ms = new MemoryStream();
        processed.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
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
            PageSegMode.SparseText
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

    protected static List<ReceiptItem> BasicParse(string text, string supermarket)
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
                    items.Add(new ReceiptItem(supermarket, pending, qty * unit));
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
                    items.Add(new ReceiptItem(supermarket, name, price));
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
                    items.Add(new ReceiptItem(supermarket, pending, price));
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

    private static Bitmap Preprocess(Bitmap original)
    {
        using var gray = ToGrayscale(original);

        // Deskew only if image is large enough
        using var deskewed = gray.Width > 100 && gray.Height > 100 ? Deskew(gray) : new Bitmap(gray);

        using var upscaled = Upscale(deskewed, 2400);
        return SimpleThreshold(upscaled);
    }

    /// <summary>
    /// Simple fixed threshold - more reliable than Otsu for receipts.
    /// </summary>
    private static Bitmap SimpleThreshold(Bitmap src)
    {
        var (pixels, stride) = LockRead(src);
        int w = src.Width, h = src.Height;

        // Use fixed threshold of 180 (works well for black text on white paper)
        const int threshold = 180;
        var result = new byte[stride * h];

        int blackCount = 0;
        for (int i = 0; i < pixels.Length; i += 3)
        {
            byte val = pixels[i] < threshold ? (byte)0 : (byte)255;
            if (val == 0) blackCount++;
            result[i] = result[i + 1] = result[i + 2] = val;
        }

        // If too few black pixels (< 0.1%), return grayscale instead
        if (blackCount < pixels.Length * 0.001)
        {
            Array.Copy(pixels, result, pixels.Length);
        }

        return CreateBitmap(result, w, h, stride);
    }

    // ── Step 1: Grayscale ────────────────────────────────────────────────────

    private static Bitmap ToGrayscale(Bitmap src)
    {
        var (pixels, stride) = LockRead(src);
        int w = src.Width, h = src.Height;
        var result = new byte[stride * h];

        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * stride + x * 3;
                byte luma = (byte)(pixels[i + 2] * 0.2126 + pixels[i + 1] * 0.7152 + pixels[i] * 0.0722);
                result[i] = result[i + 1] = result[i + 2] = luma;
            }

        return CreateBitmap(result, w, h, stride);
    }

    // ── Step 2: Deskew ───────────────────────────────────────────────────────

    private static Bitmap Deskew(Bitmap src)
    {
        using var small = Resize(src, Math.Min(src.Width, 400), Math.Min(src.Height, 400));
        var (pixels, stride) = LockRead(small);
        int sw = small.Width, sh = small.Height;

        double bestAngle = 0;
        int maxVariance = 0;

        for (int angleDeg = -50; angleDeg <= 50; angleDeg += 2)
        {
            double angle = angleDeg / 10.0;
            double radians = angle * Math.PI / 180.0;
            int variance = CalculateProjectionVariance(pixels, stride, sw, sh, radians);
            if (variance > maxVariance)
            {
                maxVariance = variance;
                bestAngle = angle;
            }
        }

        // Fine-tune
        for (int angleDeg = (int)((bestAngle - 0.5) * 10); angleDeg <= (int)((bestAngle + 0.5) * 10); angleDeg++)
        {
            double angle = angleDeg / 10.0;
            double radians = angle * Math.PI / 180.0;
            int variance = CalculateProjectionVariance(pixels, stride, sw, sh, radians);
            if (variance > maxVariance)
            {
                maxVariance = variance;
                bestAngle = angle;
            }
        }

        if (Math.Abs(bestAngle) > 0.3)
            return RotateImage(src, (float)-bestAngle);

        return new Bitmap(src);
    }

    private static int CalculateProjectionVariance(byte[] pixels, int stride, int w, int h, double radians)
    {
        double cos = Math.Cos(radians);
        double sin = Math.Sin(radians);
        var projection = new int[w];
        double cx = w / 2.0, cy = h / 2.0;

        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double dx = x - cx, dy = y - cy;
                int rx = (int)(cx + dx * cos - dy * sin);
                int ry = (int)(cy + dx * sin + dy * cos);
                if (rx >= 0 && rx < w && ry >= 0 && ry < h)
                {
                    if (pixels[ry * stride + rx * 3] < 128)
                        projection[rx]++;
                }
            }

        double mean = projection.Sum() / (double)w;
        double variance = 0;
        foreach (int count in projection)
            variance += (count - mean) * (count - mean);

        return (int)(variance / w);
    }

    private static Bitmap RotateImage(Bitmap src, float angle)
    {
        var dst = new Bitmap(src.Width, src.Height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(dst);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.White);
        g.TranslateTransform(src.Width / 2f, src.Height / 2f);
        g.RotateTransform(angle);
        g.TranslateTransform(-src.Width / 2f, -src.Height / 2f);
        g.DrawImage(src, 0, 0);
        return dst;
    }

    // ── Step 3: Upscale ──────────────────────────────────────────────────────

    private static Bitmap Upscale(Bitmap src, int targetMinSide)
    {
        int minSide = Math.Min(src.Width, src.Height);
        if (minSide >= targetMinSide)
            return new Bitmap(src);

        float scale = (float)targetMinSide / minSide;
        int newW = Math.Max(100, (int)(src.Width * scale));
        int newH = Math.Max(100, (int)(src.Height * scale));

        var dst = new Bitmap(newW, newH, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(dst);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(src, 0, 0, newW, newH);
        return dst;
    }

    // ── Resize helper ────────────────────────────────────────────────────────

    private static Bitmap Resize(Bitmap src, int newW, int newH)
    {
        var dst = new Bitmap(newW, newH, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(dst);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(src, 0, 0, newW, newH);
        return dst;
    }

    // ── Bitmap helpers ───────────────────────────────────────────────────────

    private static (byte[] pixels, int stride) LockRead(Bitmap bmp)
    {
        var data = bmp.LockBits(
            new Rectangle(0, 0, bmp.Width, bmp.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format24bppRgb);
        int stride = Math.Abs(data.Stride);
        var bytes = new byte[stride * bmp.Height];
        Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
        bmp.UnlockBits(data);
        return (bytes, stride);
    }

    private static Bitmap CreateBitmap(byte[] pixels, int width, int height, int stride)
    {
        var bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        var data = bmp.LockBits(
            new Rectangle(0, 0, width, height),
            ImageLockMode.WriteOnly,
            PixelFormat.Format24bppRgb);
        Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
        bmp.UnlockBits(data);
        return bmp;
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
        "EFECTIVO", "TARJETA", "BASE IMPONIBLE", "IMPORTE", "A PAGAR", "FACTURA"
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

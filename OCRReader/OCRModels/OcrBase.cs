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
abstract class OcrBase : IReceiptOCR
{
    protected readonly string TessDataPath;

    protected OcrBase(string tessDataPath = @"./tessdata")
    {
        TessDataPath = tessDataPath;
    }

    public abstract List<ReceiptItem> ProcessTicket(string imagePath);

    // ── Tesseract OCR extraction ─────────────────────────────────────────────

    protected (string Text, double Confidence) ExtractRawText(string imagePath)
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

        using var page = engine.Process(pix, PageSegMode.Auto);
        return (page.GetText(), page.GetMeanConfidence());
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

    // ── Basic line-by-line parsing (used by BronzeOCR, overridden by others) ─

    protected static List<ReceiptItem> BasicParse(string text, string supermarket)
    {
        var items = new List<ReceiptItem>();
        string? pending = null;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length < 2) continue;

            // Pattern A: "N x ( PRICE )" — multi-quantity sub-line
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

            // Pattern B: "PRODUCT   PRICE" on the same line (2+ spaces before price)
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

            // Pattern C: standalone price — associate with the pending product
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

            // No price found — keep as pending product name for the next line
            pending = IsSkippable(line) ? null : line;
        }

        return items;
    }

    // ── Preprocessing pipeline ───────────────────────────────────────────────

    private static Bitmap Preprocess(Bitmap original)
    {
        using var upscaled = Upscale(original, targetMinSide: 1800);
        using var gray = ToGrayscale(upscaled);
        using var blurred = GaussianBlur(gray);
        return AdaptiveBinarize(blurred);
    }

    private static Bitmap Upscale(Bitmap src, int targetMinSide)
    {
        int minSide = Math.Min(src.Width, src.Height);
        if (minSide >= targetMinSide)
            return new Bitmap(src);

        float scale = (float)targetMinSide / minSide;
        int newW = (int)(src.Width * scale);
        int newH = (int)(src.Height * scale);

        var dst = new Bitmap(newW, newH, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(dst);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(src, 0, 0, newW, newH);
        return dst;
    }

    private static Bitmap ToGrayscale(Bitmap src)
    {
        var (pixels, stride) = LockRead(src);
        int w = src.Width, h = src.Height;
        var result = new byte[stride * h];

        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * stride + x * 3;
                byte luma = (byte)(pixels[i + 2] * 0.299 + pixels[i + 1] * 0.587 + pixels[i] * 0.114);
                result[i] = result[i + 1] = result[i + 2] = luma;
            }

        return CreateBitmap(result, w, h, stride);
    }

    private static Bitmap GaussianBlur(Bitmap src)
    {
        ReadOnlySpan<int> dx = [-1, 0, 1, -1, 0, 1, -1, 0, 1];
        ReadOnlySpan<int> dy = [-1, -1, -1, 0, 0, 0, 1, 1, 1];
        ReadOnlySpan<float> kw = [1f, 2f, 1f, 2f, 4f, 2f, 1f, 2f, 1f];

        var (pixels, stride) = LockRead(src);
        int w = src.Width, h = src.Height;
        var result = new byte[stride * h];

        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float sum = 0;
                for (int k = 0; k < 9; k++)
                {
                    int px = Math.Clamp(x + dx[k], 0, w - 1);
                    int py = Math.Clamp(y + dy[k], 0, h - 1);
                    sum += pixels[py * stride + px * 3] * kw[k];
                }
                byte val = (byte)Math.Clamp((int)(sum / 16f), 0, 255);
                int i = y * stride + x * 3;
                result[i] = result[i + 1] = result[i + 2] = val;
            }

        return CreateBitmap(result, w, h, stride);
    }

    private static Bitmap AdaptiveBinarize(Bitmap src)
    {
        var (pixels, stride) = LockRead(src);
        int w = src.Width, h = src.Height;
        int iStride = w + 1;

        var integral   = new long[iStride * (h + 1)];
        var integralSq = new long[iStride * (h + 1)];

        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                long v = pixels[y * stride + x * 3];
                int iy = y + 1, ix = x + 1;
                integral[iy * iStride + ix] = v
                    + integral[(iy - 1) * iStride + ix]
                    + integral[iy * iStride + (ix - 1)]
                    - integral[(iy - 1) * iStride + (ix - 1)];
                integralSq[iy * iStride + ix] = v * v
                    + integralSq[(iy - 1) * iStride + ix]
                    + integralSq[iy * iStride + (ix - 1)]
                    - integralSq[(iy - 1) * iStride + (ix - 1)];
            }

        int r = Math.Clamp(Math.Min(w, h) / 40, 15, 60);
        const double k = 0.34, R = 128.0;

        var result = new byte[stride * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int x1 = Math.Max(x - r, 0), y1 = Math.Max(y - r, 0);
                int x2 = Math.Min(x + r, w - 1), y2 = Math.Min(y + r, h - 1);
                int count = (x2 - x1 + 1) * (y2 - y1 + 1);

                long s = integral[(y2 + 1) * iStride + (x2 + 1)]
                       - integral[y1 * iStride + (x2 + 1)]
                       - integral[(y2 + 1) * iStride + x1]
                       + integral[y1 * iStride + x1];

                long sq = integralSq[(y2 + 1) * iStride + (x2 + 1)]
                        - integralSq[y1 * iStride + (x2 + 1)]
                        - integralSq[(y2 + 1) * iStride + x1]
                        + integralSq[y1 * iStride + x1];

                double mean   = (double)s / count;
                double stdDev = Math.Sqrt(Math.Max(0.0, (double)sq / count - mean * mean));

                double threshold = mean * (1.0 + k * (stdDev / R - 1.0));
                byte val = pixels[y * stride + x * 3] >= threshold ? (byte)255 : (byte)0;
                int i = y * stride + x * 3;
                result[i] = result[i + 1] = result[i + 2] = val;
            }

        return CreateBitmap(result, w, h, stride);
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

    // ── String similarity helper ─────────────────────────────────────────────

    protected static int LevenshteinDistance(string a, string b)
    {
        if (string.IsNullOrEmpty(a)) return b?.Length ?? 0;
        if (string.IsNullOrEmpty(b)) return a.Length;

        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];

        for (int j = 0; j <= b.Length; j++)
            prev[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = char.ToUpperInvariant(a[i - 1]) == char.ToUpperInvariant(b[j - 1]) ? 0 : 1;
                curr[j] = Math.Min(
                    Math.Min(curr[j - 1] + 1, prev[j] + 1),
                    prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[b.Length];
    }
}

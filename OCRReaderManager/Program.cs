using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace OCRReaderManager;

internal class Program
{
    static async Task Main(string[] args)
    {
        // Resolve tessdata path relative to the OCRReader project
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var tessdataPath = Path.Combine(baseDir, "tessdata");

        if (!Directory.Exists(tessdataPath))
        {
            // Fallback: look for tessdata relative to the solution root
            var solutionDir = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", ".."));
            tessdataPath = Path.Combine(solutionDir, "OCRReader", "tessdata");
        }

        var host = Host.CreateDefaultBuilder(args)
            .ConfigureServices((context, services) =>
            {
                // Register OCR dependencies with the resolved tessdata path
                services.AddSingleton<IReceiptOCR>(sp => new BronzeOCR(tessdataPath));
                services.AddSingleton<IReceiptOCR>(sp => new SilverOCR(tessdataPath));
                services.AddSingleton<IReceiptOCR>(sp => new GoldOCR(tessdataPath));

                // Register OCRManager
                services.AddSingleton<IOCRManager, OCRManager>();
            })
            .Build();

        var ocrManager = host.Services.GetRequiredService<IOCRManager>();

        // Find sample images relative to the OCRReader project
        var samplesDir = Path.Combine(baseDir, "Samples");

        if (!Directory.Exists(samplesDir))
        {
            Console.WriteLine($"Samples directory not found: {samplesDir}");
            return;
        }

        var imageFiles = Directory.GetFiles(samplesDir, "*.jpeg")
                                  .Concat(Directory.GetFiles(samplesDir, "*.jpg"))
                                  .Concat(Directory.GetFiles(samplesDir, "*.png"))
                                  .OrderBy(f => f)
                                  .ToList();

        if (imageFiles.Count == 0)
        {
            Console.WriteLine("No sample images found in Samples directory.");
            return;
        }

        Console.WriteLine("========================================");
        Console.WriteLine("  OCR Receipt Processor");
        Console.WriteLine("========================================\n");
        Console.WriteLine($"Found {imageFiles.Count} image(s) to process.\n");

        foreach (var imagePath in imageFiles)
        {
            Console.WriteLine("----------------------------------------");
            Console.WriteLine($"Processing: {Path.GetFileName(imagePath)}");
            Console.WriteLine("----------------------------------------");

            try
            {
                var receipt = ocrManager.ProcessReceipt(imagePath);

                DisplayReceipt(receipt);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ERROR: {ex.Message}");
            }

            Console.WriteLine();
        }

        Console.WriteLine("========================================");
        Console.WriteLine("  Processing Complete!");
        Console.WriteLine("========================================");

        await host.StopAsync();
    }

    static void DisplayReceipt(Receipt receipt)
    {
        Console.WriteLine($"\n  Supermarket:     {receipt.Supermarket}");
        Console.WriteLine($"  Store Location:  {receipt.StoreLocation ?? "N/A"}");
        Console.WriteLine($"  Ticket Number:   {receipt.TicketNumber ?? "N/A"}");
        Console.WriteLine($"  Date:            {receipt.Date ?? "N/A"}");
        Console.WriteLine($"  Time:            {receipt.Time ?? "N/A"}");
        Console.WriteLine($"  Cashier:         {receipt.Cashier ?? "N/A"}");
        Console.WriteLine($"  CIF:             {receipt.Cif ?? "N/A"}");
        Console.WriteLine($"  Barcode:         {receipt.Barcode ?? "N/A"}");
        Console.WriteLine($"  Phone:           {receipt.Phone ?? "N/A"}");

        Console.WriteLine($"\n  Products ({receipt.Products.Count}):");
        if (receipt.Products.Count > 0)
        {
            foreach (var product in receipt.Products)
            {
                Console.WriteLine($"    - {product.Name,-30} {product.Price}");
            }
        }
        else
        {
            Console.WriteLine("    (No products detected)");
        }

        Console.WriteLine($"\n  Lines ({receipt.Lines.Count}):");
        if (receipt.Lines.Count > 0)
        {
            foreach (var line in receipt.Lines)
            {
                Console.WriteLine($"    [{line.Type,-15}] {line.Description,-30} {line.Amount:C}");
            }
        }
        else
        {
            Console.WriteLine("    (No lines detected)");
        }

        Console.WriteLine($"\n  Subtotal:        {(receipt.Subtotal.HasValue ? $"{receipt.Subtotal.Value:C}" : "N/A")}");
        Console.WriteLine($"  Tax Amount:      {(receipt.TaxAmount.HasValue ? $"{receipt.TaxAmount.Value:C}" : "N/A")}");
        Console.WriteLine($"  Discount:        {(receipt.DiscountAmount.HasValue ? $"{receipt.DiscountAmount.Value:C}" : "N/A")}");
        Console.WriteLine($"  Total:           {(receipt.Total.HasValue ? $"{receipt.Total.Value:C}" : "N/A")}");
        Console.WriteLine($"  Payment Method:  {receipt.PaymentMethod ?? "N/A"}");
        Console.WriteLine($"  Amount Paid:     {(receipt.AmountPaid.HasValue ? $"{receipt.AmountPaid.Value:C}" : "N/A")}");
        Console.WriteLine($"  Change:          {(receipt.Change.HasValue ? $"{receipt.Change.Value:C}" : "N/A")}");
    }
}

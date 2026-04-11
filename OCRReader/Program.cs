using System.Globalization;

class Program
{
    static void Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var sampleDir = @"./Samples";
        var imageFiles = Directory.GetFiles(sampleDir, "*.jpeg")
            .Concat(Directory.GetFiles(sampleDir, "*.jpg"))
            .Concat(Directory.GetFiles(sampleDir, "*.png"))
            .Distinct()
            .OrderBy(f => f)
            .ToList();

        if (imageFiles.Count == 0)
        {
            Console.WriteLine("No sample images found.");
            return;
        }

        Console.WriteLine("=== ADVANCED RECEIPT OCR PIPELINE ===\n");

        IReceiptOCR bronze = new BronzeOCR();
        IReceiptOCR silver = new SilverOCR();
        IReceiptOCR gold = new GoldOCR();

        int idx = 0;
        foreach (var imagePath in imageFiles)
        {
            idx++;
            var fileName = Path.GetFileName(imagePath);
            Console.WriteLine(new string('=', 70));
            Console.WriteLine($"RECEIPT #{idx}: {fileName}");
            Console.WriteLine(new string('=', 70));

            try
            {
                Console.WriteLine("\n--- BRONZE OCR ---");
                var bronzeItems = bronze.ProcessTicket(imagePath);
                PrintItems(bronzeItems);

                Console.WriteLine("\n--- SILVER OCR ---");
                var silverItems = silver.ProcessTicket(imagePath);
                PrintItems(silverItems);

                Console.WriteLine("\n--- GOLD OCR ---");
                var goldItems = gold.ProcessTicket(imagePath);
                PrintItems(goldItems);

                // Final receipt from Gold
                Console.WriteLine("\n--- FINAL RECEIPT MERGED ---");
                var mergedReceipt = MergeOCRResult.MergeOCRModels(bronzeItems, silverItems, goldItems);
                if (mergedReceipt.Products.Count > 0)
                {
                    Console.WriteLine($"Supermercado: {mergedReceipt.Supermarket}");
                    Console.WriteLine($"Productos: {mergedReceipt.Products.Count}");
                    decimal total = mergedReceipt.Products.Sum(p => decimal.TryParse(p.Price, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : 0);
                    Console.WriteLine($"Total: {total:F2}€");
                    foreach (var p in mergedReceipt.Products)
                        Console.WriteLine($"  {p.Name,-35} {MergeOCRResult.FormatPrice(p.Price),8}€");
                }
                else
                {
                    Console.WriteLine("  (no items detected)");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
            }

            Console.WriteLine();
        }

        Console.WriteLine("Done!");
    }

    private static void PrintItems(List<ReceiptItem> items)
    {
        if (items.Count == 0)
        {
            Console.WriteLine("  (no items detected)");
            return;
        }
        Console.WriteLine($"  Supermercado: {items[0].Supermarket}");
        Console.WriteLine($"  Items: {items.Count}");
        decimal total = 0;
        foreach (var item in items)
        {
            Console.WriteLine($"    {item.Product,-35} {item.Price,8:F2}€");
            total += item.Price;
        }
        Console.WriteLine($"  Subtotal: {total:F2}€");
    }
}

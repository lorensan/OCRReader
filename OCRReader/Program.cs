class Program
{
    static void Main()
    {
        string imagePath = @"./Samples/Receipt1.jpeg";

        IReceiptOCR bronze = new BronzeOCR();
        IReceiptOCR silver = new SilverOCR();
        IReceiptOCR gold = new GoldOCR();

        // ── Bronze: basic OCR ────────────────────────────────────────────────
        Console.WriteLine("=== BRONZE OCR ===");
        List<ReceiptItem> bronzeItems = bronze.ProcessTicket(imagePath);
        PrintItems(bronzeItems);

        // ── Silver: cleaned + heuristic parsing ─────────────────────────────
        Console.WriteLine("\n=== SILVER OCR ===");
        List<ReceiptItem> silverItems = silver.ProcessTicket(imagePath);
        PrintItems(silverItems);

        // ── Gold: advanced parsing + dedup + validation + fuzzy match ────────
        Console.WriteLine("\n=== GOLD OCR ===");
        List<ReceiptItem> goldItems = gold.ProcessTicket(imagePath);
        PrintItems(goldItems);

        // ── Construct final Receipt from Gold output ────────────────────────
        var receipt = new Receipt
        {
            Supermarket = goldItems.FirstOrDefault()?.Supermarket ?? "Unknown",
            Products = goldItems
                .Select(x => new Product { Name = x.Product, Price = x.Price.ToString("0.00") })
                .ToList()
        };

        Console.WriteLine("\n=== FINAL RECEIPT ===");
        Console.WriteLine($"Supermercado: {receipt.Supermarket}");
        Console.WriteLine($"Productos: {receipt.Products.Count}");
        foreach (var p in receipt.Products)
            Console.WriteLine($"  {p.Name,-35} {p.Price,8}€");
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
        foreach (var item in items)
            Console.WriteLine($"    {item.Product,-35} {item.Price,8:F2}€");
    }
}

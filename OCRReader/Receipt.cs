/// <summary>Represents a fully parsed receipt with all available metadata and line items.</summary>
class Receipt
{
    public string Supermarket { get; set; } = "DESCONOCIDO";
    public string? StoreLocation { get; set; }
    public string? TicketNumber { get; set; }
    public string? Date { get; set; }
    public string? Time { get; set; }
    public string? Cashier { get; set; }
    public string? Cif { get; set; }
    public string? Barcode { get; set; }
    public string? Phone { get; set; }
    public List<ReceiptLine> Lines { get; set; } = new List<ReceiptLine>();
    public List<Product> Products { get; set; } = new List<Product>();
    public decimal? Subtotal { get; set; }
    public decimal? Total { get; set; }
    public decimal? TaxAmount { get; set; }
    public decimal? DiscountAmount { get; set; }
    public string? PaymentMethod { get; set; }
    public decimal? AmountPaid { get; set; }
    public decimal? Change { get; set; }
}

/// <summary>Represents a single product on a receipt.</summary>
class Product
{
    public string Name { get; set; } = string.Empty;
    public string Price { get; set; } = "0.00";
}

/// <summary>Represents a single line item from the receipt, including non-product lines.</summary>
record ReceiptLine(string Type, string Description, decimal Amount, int? Quantity = null, decimal? UnitPrice = null);

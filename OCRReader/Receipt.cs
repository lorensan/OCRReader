/// <summary>Represents a fully parsed receipt with supermarket info and product list.</summary>
class Receipt
{
    public string Supermarket { get; set; } = "DESCONOCIDO";
    public List<Product> Products { get; set; } = new List<Product>();
}

/// <summary>Represents a single product on a receipt.</summary>
class Product
{
    public string Name { get; set; } = string.Empty;
    public string Price { get; set; } = "0.00";
}

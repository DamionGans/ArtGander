namespace ArtGander.Core.Model;

/// <summary>A product as stored inside an art piece in format version 2. Only read, to move it to a product file.</summary>
public sealed class LegacyProduct : ModelBase
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public ProductType Type { get; set; } = ProductType.SelfPrint;
    public string Provider { get; set; } = "";
    public string Variant { get; set; } = "";
    public List<string> Files { get; set; } = [];
    public decimal? ProductionCost { get; set; }
    public decimal? Price { get; set; }
    public string Currency { get; set; } = "EUR";
    public List<CostLine> CostBreakdown { get; set; } = [];
    public int? EditionSize { get; set; }
    public int? Stock { get; set; }
    public List<LegacyListing> Listings { get; set; } = [];
    public string Notes { get; set; } = "";
    public List<BundleItem> Includes { get; set; } = [];
}

public sealed class LegacyListing : ModelBase
{
    public string Channel { get; set; } = "shopify";
    public string Url { get; set; } = "";
    public string ExternalId { get; set; } = "";
    public decimal? Price { get; set; }
    public ListingStatus Status { get; set; } = ListingStatus.Active;
}

/// <summary>A sale line as stored inside an art piece in format version 2. Only read, to move it to an order file.</summary>
public sealed class LegacySale : ModelBase
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateOnly Date { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public string OrderRef { get; set; } = "";
    public string Channel { get; set; } = "shopify";
    public Guid? ProductId { get; set; }
    public int Quantity { get; set; } = 1;
    public decimal UnitPrice { get; set; }
    public decimal Fees { get; set; }
    public decimal? UnitCost { get; set; }
    public string Currency { get; set; } = "EUR";
    public string Customer { get; set; } = "";
    public string Notes { get; set; } = "";
}

/// <summary>A record stored as one JSON file in a library folder (products, orders).</summary>
public interface IStoredRecord
{
    Guid Id { get; }
    /// <summary>The file it was loaded from or last saved to.</summary>
    string? StoredFileName { get; set; }
    /// <summary>The hash of that file's content, to detect edits made on another computer in the meantime.</summary>
    string? StoredHash { get; set; }
}

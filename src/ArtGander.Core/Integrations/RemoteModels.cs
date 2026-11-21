namespace ArtGander.Core.Integrations;

/// <summary>A product as a platform (Printify or Shopify) currently has it.</summary>
public sealed record RemoteProduct
{
    public required string Channel { get; init; }
    public required string Id { get; init; }
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public IReadOnlyList<string> Tags { get; init; } = [];
    /// <summary>Shopify: active/draft/archived. Printify: visible/hidden.</summary>
    public string Status { get; init; } = "";
    /// <summary>The platform's last-modified stamp, compared to detect edits made outside ArtGander.</summary>
    public string UpdatedAt { get; init; } = "";
    public IReadOnlyList<RemoteVariant> Variants { get; init; } = [];
    public string Handle { get; init; } = "";
    public string Url { get; init; } = "";
    public string? ImageUrl { get; init; }

    // Printify only
    public long? ShopId { get; init; }
    /// <summary>The Shopify product id (numeric) that Printify published this product as.</summary>
    public string? ExternalId { get; init; }
    public bool IsLocked { get; init; }
    public long? BlueprintId { get; init; }
    public long? PrintProviderId { get; init; }

    public IEnumerable<RemoteVariant> EnabledVariants => Variants.Where(v => v.Enabled);
}

public sealed record RemoteVariant
{
    public required string Id { get; init; }
    public string Title { get; init; } = "";
    public string Sku { get; init; } = "";
    public decimal? Price { get; init; }
    public decimal? Cost { get; init; }
    public bool Enabled { get; init; } = true;
    public int? Stock { get; init; }
    public string? InventoryItemId { get; init; }
}

/// <summary>An order as Shopify or Printify has it.</summary>
public sealed record RemoteOrder
{
    public required string Channel { get; init; }
    public required string Id { get; init; }
    /// <summary>The number people see, e.g. "#1001" (Shopify), or Printify's label for the shop order.</summary>
    public string Name { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public string UpdatedAt { get; init; } = "";
    public string Currency { get; init; } = "";
    public string Customer { get; init; } = "";
    public bool Cancelled { get; init; }
    public bool Refunded { get; init; }
    public bool Paid { get; init; }
    public bool Fulfilled { get; init; }
    public bool Delivered { get; init; }

    // Shopify money
    public decimal? TotalPaid { get; init; }
    public decimal? TotalTax { get; init; }

    // Printify money (what Printify charges you)
    public decimal? ProductionCost { get; init; }
    public decimal? ProductionTax { get; init; }
    public decimal? ShippingCost { get; init; }

    public string Carrier { get; init; } = "";
    public string TrackingCode { get; init; } = "";
    public DateOnly? ShippedOn { get; init; }

    /// <summary>Printify: the Shopify order this order fulfills (numeric id and its label, e.g. "#1001").</summary>
    public string? ShopOrderId { get; init; }
    public string? ShopOrderLabel { get; init; }

    public IReadOnlyList<RemoteOrderLine> Lines { get; init; } = [];
}

public sealed record RemoteOrderLine
{
    public required string Id { get; init; }
    public string Title { get; init; } = "";
    public string Sku { get; init; } = "";
    public int Quantity { get; init; } = 1;
    public decimal? UnitPrice { get; init; }
    /// <summary>Printify: production cost per unit.</summary>
    public decimal? UnitCost { get; init; }
    public string? ProductId { get; init; }
    public string? VariantId { get; init; }
}

/// <summary>A Printify shop (a connection to a sales channel such as a Shopify store).</summary>
public sealed record PrintifyShop(long Id, string Title, string SalesChannel);

/// <summary>Turns platform ids into each other's formats.</summary>
public static class Ids
{
    private const string ProductGid = "gid://shopify/Product/";

    /// <summary>"gid://shopify/Product/123" → "123".</summary>
    public static string Numeric(string id) => id.Contains('/') ? id[(id.LastIndexOf('/') + 1)..] : id;

    public static string ShopifyProductGid(string numericOrGid) => numericOrGid.StartsWith("gid://", StringComparison.Ordinal) ? numericOrGid : ProductGid + numericOrGid;

    public static bool SameShopifyId(string? a, string? b) =>
        !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && Numeric(a) == Numeric(b);
}

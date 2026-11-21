using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ArtGander.Core.Model;

[JsonConverter(typeof(CamelCaseEnumConverter<OrderStatus>))]
public enum OrderStatus { Open, Paid, Shipped, Delivered, Cancelled, Refunded }

/// <summary>
/// An order: its items (<see cref="Lines"/>), what the customer paid, shipping, tracking, and the actual production bill.
/// Stored as <c>{ArtRoot}/.artgander/orders/{order}.json</c>. All tax fields are optional. Leave them empty to keep things simple.
/// Orders imported from Shopify and Printify are combined into one order when they are the same order (<see cref="Sources"/>).
/// </summary>
public sealed partial class Order : ModelBase, IStoredRecord
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public Guid Id { get; set; } = Guid.NewGuid();

    [ObservableProperty] public partial string OrderRef { get; set; } = "";
    [ObservableProperty] public partial DateOnly Date { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    [ObservableProperty] public partial string Channel { get; set; } = "shopify";
    [ObservableProperty] public partial OrderStatus Status { get; set; } = OrderStatus.Paid;
    [ObservableProperty] public partial string Customer { get; set; } = "";
    [ObservableProperty] public partial string Currency { get; set; } = "EUR";

    /// <summary>The total the customer paid, including any shipping they were charged. Left empty, the sum of the item prices is used.</summary>
    [ObservableProperty] public partial decimal? AmountPaid { get; set; }
    /// <summary>Tax (e.g. VAT/sales tax) included in <see cref="AmountPaid"/>. Optional.</summary>
    [ObservableProperty] public partial decimal? AmountPaidTax { get; set; }

    /// <summary>The actual production cost of this order, e.g. the Printify invoice. Left empty, the products' production costs are used.</summary>
    [ObservableProperty] public partial decimal? ProductionCost { get; set; }
    /// <summary>Tax paid on the production cost. Optional.</summary>
    [ObservableProperty] public partial decimal? ProductionTax { get; set; }

    /// <summary>What shipping cost you, before tax.</summary>
    [ObservableProperty] public partial decimal? ShippingCost { get; set; }
    /// <summary>Tax paid on shipping. Optional.</summary>
    [ObservableProperty] public partial decimal? ShippingTax { get; set; }

    /// <summary>The delivery provider, e.g. "USPS", "PostNL", "DHL".</summary>
    [ObservableProperty] public partial string Carrier { get; set; } = "";
    [ObservableProperty] public partial string TrackingCode { get; set; } = "";
    [ObservableProperty] public partial DateOnly? ShippedOn { get; set; }

    [ObservableProperty] public partial string Notes { get; set; } = "";

    public ObservableCollection<OrderLine> Lines { get; set; } = [];

    /// <summary>Where this order was imported from.</summary>
    public OrderSources Sources { get; set; } = new();

    /// <summary>
    /// The imported value of each imported field at the last import (field name → value). A field you changed yourself
    /// since then is kept on the next import instead of being overwritten.
    /// </summary>
    public Dictionary<string, string?> ImportBase { get; set; } = [];

    public DateTimeOffset? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }

    [JsonIgnore] public string? StoredFileName { get; set; }
    [JsonIgnore] public string? StoredHash { get; set; }

    [JsonIgnore] public bool IsImported => Sources.Shopify is not null || Sources.Printify is not null;
}

/// <summary>One item of an order.</summary>
public sealed partial class OrderLine : ModelBase
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [ObservableProperty] public partial Guid? ProductId { get; set; }
    [ObservableProperty] public partial string? VariantKey { get; set; }
    /// <summary>The art piece, when the line is about one piece (e.g. selling the original without a product).</summary>
    [ObservableProperty] public partial Guid? PieceId { get; set; }
    /// <summary>The item's name as it was ordered. Kept so imported items that aren't linked to a product still read well.</summary>
    [ObservableProperty] public partial string Title { get; set; } = "";
    [ObservableProperty] public partial string Sku { get; set; } = "";
    [ObservableProperty] public partial int Quantity { get; set; } = 1;
    [ObservableProperty] public partial decimal UnitPrice { get; set; }
    /// <summary>The cost per unit at the time of the order. Left empty, the product's production cost is used.</summary>
    [ObservableProperty] public partial decimal? UnitCost { get; set; }
    [ObservableProperty] public partial decimal Fees { get; set; }
    [ObservableProperty] public partial Fulfillment Fulfillment { get; set; } = Fulfillment.Self;
    [ObservableProperty] public partial string Notes { get; set; } = "";
    /// <summary>The Shopify line item id, for imported lines.</summary>
    public string? ShopifyLineId { get; set; }
    /// <summary>The Printify line item key, for imported lines.</summary>
    public string? PrintifyLineId { get; set; }
}

public sealed class OrderSources : ModelBase
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public ExternalOrder? Shopify { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public ExternalOrder? Printify { get; set; }
}

public sealed class ExternalOrder : ModelBase
{
    public string Id { get; set; } = "";
    /// <summary>The name people see, e.g. "#1001".</summary>
    public string Name { get; set; } = "";
    public string? UpdatedAt { get; set; }
    public DateTimeOffset? ImportedAt { get; set; }
}

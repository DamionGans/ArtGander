using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ArtGander.Core.Model;

[JsonConverter(typeof(CamelCaseEnumConverter<OrderStatus>))]
public enum OrderStatus { Open, Paid, Shipped, Delivered, Cancelled, Refunded }

/// <summary>
/// Order-level details that don't belong to a single art piece: what the customer paid, shipping, tracking, and
/// the actual production bill. Stored as <c>{ArtRoot}/.artgander/orders/{order}.json</c>. The individual items
/// stay in each art piece's <c>artgander.json</c> as <see cref="Sale"/> lines with the same <see cref="OrderRef"/>.
/// All tax fields are optional. Leave them empty to keep things simple.
/// </summary>
public sealed partial class Order : ModelBase
{
    public int SchemaVersion { get; set; } = 1;
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

    public DateTimeOffset? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

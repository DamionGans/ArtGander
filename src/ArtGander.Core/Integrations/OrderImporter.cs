using System.Globalization;
using ArtGander.Core.Model;
using ArtGander.Core.Naming;

namespace ArtGander.Core.Integrations;

/// <summary>One field of an order that an import would change.</summary>
public sealed record FieldChange(string Field, string Label, string? Local, string? Remote, bool IsConflict);

/// <summary>What importing one order would do.</summary>
public sealed class OrderImportItem
{
    /// <summary>A working copy (or a new order). Nothing is saved until the import is applied.</summary>
    public required Order Order { get; init; }
    public required bool IsNew { get; init; }
    public RemoteOrder? Shopify { get; init; }
    public RemoteOrder? Printify { get; init; }
    public List<FieldChange> Changes { get; } = [];
    public List<OrderLine> NewLines { get; } = [];
    /// <summary>Imported values per field, applied to <see cref="Order.ImportBase"/>.</summary>
    internal Dictionary<string, string?> Remote { get; } = [];

    public bool HasChanges => IsNew || Changes.Count > 0 || NewLines.Count > 0 || SourcesChanged;
    public bool HasConflicts => Changes.Any(c => c.IsConflict);
    internal bool SourcesChanged { get; set; }
    public string Title => (Order.OrderRef.Length > 0 ? Order.OrderRef : "(no number)") + (Shopify is not null && Printify is not null ? " · Shopify + Printify" : Shopify is not null ? " · Shopify" : " · Printify");
}

/// <summary>
/// Imports orders from Shopify and Printify. A Printify order that fulfills a Shopify order (Printify's
/// <c>metadata.shop_order_id</c>) is combined with it into one ArtGander order: what the customer paid comes from
/// Shopify, the production bill, shipping and tracking from Printify.
/// <para>
/// Your own edits are kept: every imported field remembers what was imported last time (<see cref="Order.ImportBase"/>).
/// A field you changed yourself is only replaced when you choose so (it's reported as a conflict).
/// </para>
/// </summary>
public static class OrderImporter
{
    private sealed record Field(string Label, Func<Order, string?> Get, Action<Order, string?> Set);

    private static readonly Dictionary<string, Field> Fields = new()
    {
        ["orderRef"] = new("Order number", o => o.OrderRef, (o, v) => o.OrderRef = v ?? ""),
        ["date"] = new("Date", o => o.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), (o, v) => { if (DateOnly.TryParse(v, CultureInfo.InvariantCulture, out var d)) o.Date = d; }),
        ["channel"] = new("Channel", o => o.Channel, (o, v) => o.Channel = v ?? ""),
        ["status"] = new("Status", o => o.Status.ToString(), (o, v) => { if (Enum.TryParse<OrderStatus>(v, out var s)) o.Status = s; }),
        ["customer"] = new("Customer", o => o.Customer, (o, v) => o.Customer = v ?? ""),
        ["currency"] = new("Currency", o => o.Currency, (o, v) => o.Currency = v ?? ""),
        ["amountPaid"] = new("Customer paid", o => Money(o.AmountPaid), (o, v) => o.AmountPaid = ParseMoney(v)),
        ["amountPaidTax"] = new("Tax paid by customer", o => Money(o.AmountPaidTax), (o, v) => o.AmountPaidTax = ParseMoney(v)),
        ["productionCost"] = new("Production cost", o => Money(o.ProductionCost), (o, v) => o.ProductionCost = ParseMoney(v)),
        ["productionTax"] = new("Tax on production", o => Money(o.ProductionTax), (o, v) => o.ProductionTax = ParseMoney(v)),
        ["shippingCost"] = new("Shipping cost", o => Money(o.ShippingCost), (o, v) => o.ShippingCost = ParseMoney(v)),
        ["carrier"] = new("Delivery provider", o => o.Carrier, (o, v) => o.Carrier = v ?? ""),
        ["trackingCode"] = new("Tracking code", o => o.TrackingCode, (o, v) => o.TrackingCode = v ?? ""),
        ["shippedOn"] = new("Shipped on", o => o.ShippedOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            (o, v) => o.ShippedOn = DateOnly.TryParse(v, CultureInfo.InvariantCulture, out var d) ? d : null),
    };

    private static string? Money(decimal? d) => d?.ToString("0.00", CultureInfo.InvariantCulture);
    private static decimal? ParseMoney(string? v) => decimal.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : null;

    public static List<OrderImportItem> Plan(IReadOnlyList<RemoteOrder> shopify, IReadOnlyList<RemoteOrder> printify,
        IReadOnlyList<Order> existing, IReadOnlyList<Product> products, string currency)
    {
        var items = new List<OrderImportItem>();
        var usedPrintify = new HashSet<string>();

        foreach (var s in shopify)
        {
            var p = printify.FirstOrDefault(x => Ids.SameShopifyId(x.ShopOrderId, s.Id))
                    ?? printify.FirstOrDefault(x => x.ShopOrderId is null && x.ShopOrderLabel is { Length: > 0 } l && NameRules.NamesEqual(l, s.Name));
            if (p is not null) usedPrintify.Add(p.Id);
            items.Add(PlanOne(s, p, existing, products, currency));
        }
        foreach (var p in printify.Where(p => !usedPrintify.Contains(p.Id)))
        {
            // A Printify order whose Shopify order wasn't fetched this time (e.g. older) still joins its ArtGander order.
            items.Add(PlanOne(null, p, existing, products, currency));
        }
        return items;
    }

    private static OrderImportItem PlanOne(RemoteOrder? s, RemoteOrder? p, IReadOnlyList<Order> existing, IReadOnlyList<Product> products, string currency)
    {
        var match = existing.FirstOrDefault(o => s is not null && o.Sources.Shopify?.Id == s.Id)
                    ?? existing.FirstOrDefault(o => p is not null && o.Sources.Printify?.Id == p.Id)
                    ?? existing.FirstOrDefault(o => s is not null && o.Sources.Shopify is null && NameRules.NamesEqual(o.OrderRef.Trim(), s.Name))
                    ?? existing.FirstOrDefault(o => p is not null && s is null && p.ShopOrderId is { } sid && Ids.SameShopifyId(o.Sources.Shopify?.Id, sid))
                    ?? existing.FirstOrDefault(o => p is not null && s is null && p.ShopOrderLabel is { Length: > 0 } l && o.Sources.Printify is null && NameRules.NamesEqual(o.OrderRef.Trim(), l));

        var order = match is null ? new Order { Currency = currency } : Copy(match);
        var item = new OrderImportItem { Order = order, IsNew = match is null, Shopify = s, Printify = p };

        // The imported values. Shopify knows the customer side, Printify the production side.
        var remote = item.Remote;
        if (s is not null)
        {
            remote["orderRef"] = s.Name;
            remote["date"] = DateOnly.FromDateTime(s.CreatedAt.LocalDateTime).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            remote["channel"] = "shopify";
            remote["customer"] = s.Customer.Length > 0 ? s.Customer : null;
            remote["currency"] = s.Currency.Length > 0 ? s.Currency : null;
            remote["amountPaid"] = Money(s.TotalPaid);
            remote["amountPaidTax"] = Money(s.TotalTax);
            remote["status"] = StatusOf(s, p).ToString();
        }
        else if (p is not null)
        {
            if (match is null)
            {
                remote["orderRef"] = p.ShopOrderLabel ?? "PFY-" + p.Id[..Math.Min(8, p.Id.Length)];
                remote["date"] = DateOnly.FromDateTime(p.CreatedAt.LocalDateTime).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                remote["channel"] = "printify";
            }
            remote["status"] = StatusOf(null, p).ToString();
        }
        if (p is not null)
        {
            remote["productionCost"] = Money(p.ProductionCost);
            remote["productionTax"] = Money(p.ProductionTax);
            remote["shippingCost"] = Money(p.ShippingCost);
        }
        var shipping = p is { TrackingCode.Length: > 0 } ? p : s;
        if (shipping is { TrackingCode.Length: > 0 })
        {
            remote["carrier"] = shipping.Carrier;
            remote["trackingCode"] = shipping.TrackingCode;
            remote["shippedOn"] = shipping.ShippedOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        foreach (var (key, value) in remote)
        {
            if (value is null) continue; // nothing imported for this field: leave it alone
            var field = Fields[key];
            var local = field.Get(order);
            if (Same(local, value)) continue;
            order.ImportBase.TryGetValue(key, out var lastImported);
            if (item.IsNew || Same(local, lastImported) || (lastImported is null && string.IsNullOrEmpty(local)))
                item.Changes.Add(new FieldChange(key, field.Label, local, value, IsConflict: false));
            else if (!Same(value, lastImported))
                item.Changes.Add(new FieldChange(key, field.Label, local, value, IsConflict: true)); // you changed it, and so did the shop
            // else: you changed it after the last import and the shop didn't: keep yours, nothing to report.
        }

        PlanLines(item, s, p, products);
        item.SourcesChanged = (s is not null && order.Sources.Shopify?.UpdatedAt != s.UpdatedAt)
                              || (p is not null && order.Sources.Printify?.UpdatedAt != p.UpdatedAt);
        return item;
    }

    private static OrderStatus StatusOf(RemoteOrder? s, RemoteOrder? p) =>
        s?.Refunded == true ? OrderStatus.Refunded
        : s?.Cancelled == true || (s is null && p?.Cancelled == true) ? OrderStatus.Cancelled
        : p?.Delivered == true || s?.Delivered == true ? OrderStatus.Delivered
        : p?.Fulfilled == true || s?.Fulfilled == true ? OrderStatus.Shipped
        : s?.Paid != false ? OrderStatus.Paid
        : OrderStatus.Open;

    private static void PlanLines(OrderImportItem item, RemoteOrder? s, RemoteOrder? p, IReadOnlyList<Product> products)
    {
        var order = item.Order;
        if (s is not null)
        {
            foreach (var line in s.Lines)
            {
                if (order.Lines.Any(l => l.ShopifyLineId == line.Id)) continue;
                var product = products.FirstOrDefault(x => Ids.SameShopifyId(x.Channels.Shopify?.ProductId, line.ProductId));
                var variant = product?.Variants.FirstOrDefault(v => v.ShopifyVariantId == line.VariantId);
                var fromPrintify = p?.Lines.Any(pl => (pl.Sku.Length > 0 && pl.Sku == line.Sku) || (product?.Channels.Printify?.ProductId is { } pid && pl.ProductId == pid)) == true;

                // A line entered by hand earlier (e.g. before importing) for the same product is linked instead of added twice.
                var manual = order.Lines.FirstOrDefault(l => l.ShopifyLineId is null && l.PrintifyLineId is null
                                                             && product is not null && l.ProductId == product.Id && l.Quantity == line.Quantity);
                if (manual is not null) { manual.ShopifyLineId = line.Id; item.SourcesChanged = true; continue; }

                item.NewLines.Add(new OrderLine
                {
                    ProductId = product?.Id,
                    VariantKey = variant?.Key,
                    Title = line.Title,
                    Sku = line.Sku,
                    Quantity = line.Quantity,
                    UnitPrice = line.UnitPrice ?? 0,
                    Fulfillment = fromPrintify || product?.Fulfillment == Fulfillment.Printify ? Fulfillment.Printify : product?.Fulfillment ?? Fulfillment.Self,
                    ShopifyLineId = line.Id,
                });
            }
        }
        else if (p is not null)
        {
            foreach (var line in p.Lines)
            {
                if (order.Lines.Any(l => l.PrintifyLineId == line.Id)) continue;
                var product = products.FirstOrDefault(x => x.Channels.Printify?.ProductId == line.ProductId);
                var variant = product?.Variants.FirstOrDefault(v => v.PrintifyVariantId?.ToString() == line.VariantId);
                item.NewLines.Add(new OrderLine
                {
                    ProductId = product?.Id,
                    VariantKey = variant?.Key,
                    Title = line.Title,
                    Sku = line.Sku,
                    Quantity = line.Quantity,
                    UnitPrice = line.UnitPrice ?? 0,
                    UnitCost = line.UnitCost,
                    Fulfillment = Fulfillment.Printify,
                    PrintifyLineId = line.Id,
                });
            }
        }
    }

    /// <summary>
    /// Applies an import to its order. Conflicting fields keep your value unless their field name is in
    /// <paramref name="takeRemote"/>. The caller saves the order.
    /// </summary>
    public static void Apply(OrderImportItem item, IReadOnlySet<string>? takeRemote = null)
    {
        var order = item.Order;
        foreach (var change in item.Changes)
        {
            if (change.IsConflict && takeRemote?.Contains(change.Field) != true) continue;
            Fields[change.Field].Set(order, change.Remote);
        }
        // Remember what was imported, so the next import can tell your edits from the shop's.
        foreach (var (key, value) in item.Remote)
            if (value is not null) order.ImportBase[key] = value;
        foreach (var line in item.NewLines) order.Lines.Add(line);

        var now = DateTimeOffset.Now;
        if (item.Shopify is { } s)
            order.Sources.Shopify = new ExternalOrder { Id = s.Id, Name = s.Name, UpdatedAt = s.UpdatedAt, ImportedAt = now };
        if (item.Printify is { } p)
            order.Sources.Printify = new ExternalOrder { Id = p.Id, Name = p.ShopOrderLabel ?? "", UpdatedAt = p.UpdatedAt, ImportedAt = now };
    }

    private static bool Same(string? a, string? b) => string.Equals(a ?? "", b ?? "", StringComparison.Ordinal);

    private static Order Copy(Order o)
    {
        var copy = Schema.ArtGanderJson.DeserializeOrder(Schema.ArtGanderJson.Serialize(o))!;
        copy.StoredFileName = o.StoredFileName;
        copy.StoredHash = o.StoredHash;
        return copy;
    }
}

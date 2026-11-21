using ArtGander.Core.Model;
using ArtGander.Core.Storage;

namespace ArtGander.Core.Services;

/// <summary>An order line together with its product and art piece, for overviews.</summary>
public sealed record SaleRow(
    Order Order,
    OrderLine Line,
    Product? Product,
    ArtPieceDocument? Piece,
    decimal? BundleUnitCost = null)
{
    public DateOnly Date => Order.Date;

    /// <summary>What was sold: the product, or the line's own title for items that aren't linked to a product.</summary>
    public string ProductName => Product?.Name is { Length: > 0 } n ? n : Line.Title is { Length: > 0 } t ? t : "(unknown item)";

    /// <summary>The art piece or project; pieces inside a project read "Project › Piece".</summary>
    public string PieceTitle => Piece?.DisplayTitle ?? "";

    /// <summary>"Product (Piece)" or just the product.</summary>
    public string ItemTitle => PieceTitle.Length > 0 && !string.Equals(PieceTitle, ProductName, StringComparison.CurrentCultureIgnoreCase)
        ? $"{ProductName} ({PieceTitle})"
        : ProductName;

    public decimal Revenue => Line.UnitPrice * Line.Quantity;
    public decimal UnitCost => Line.UnitCost ?? Product?.CostOf(Product.FindVariant(Line.VariantKey)) ?? BundleUnitCost ?? 0m;
    public decimal Cost => UnitCost * Line.Quantity;
    public decimal Profit => Revenue - Cost - Line.Fees;
}

/// <summary>
/// An order with its lines.
/// <para>
/// Money logic, kept simple:
/// revenue = what the customer paid (or the sum of item prices) minus the tax in it;
/// costs = production (the actual bill, or the products' costs) + production tax + shipping + shipping tax + fees.
/// </para>
/// </summary>
public sealed record OrderSummary(Order Details, IReadOnlyList<SaleRow> Lines)
{
    public string OrderRef => Details.OrderRef;
    public DateOnly Date => Details.Date;
    public string Channel => Details.Channel;

    public int Items => Lines.Sum(l => l.Line.Quantity);
    public decimal ItemsTotal => Lines.Sum(l => l.Revenue);

    /// <summary>What the customer paid, including tax and any shipping they were charged.</summary>
    public decimal Paid => Details.AmountPaid ?? ItemsTotal;
    public decimal TaxCollected => Details.AmountPaidTax ?? 0;
    public decimal Revenue => Paid - TaxCollected;

    public decimal ProductionCost => (Details.ProductionCost ?? Lines.Sum(l => l.Cost)) + (Details.ProductionTax ?? 0);
    public decimal Shipping => (Details.ShippingCost ?? 0) + (Details.ShippingTax ?? 0);
    public decimal Fees => Lines.Sum(l => l.Line.Fees);
    public decimal Cost => ProductionCost;
    public decimal CostAndFees => ProductionCost + Shipping + Fees;
    public decimal Profit => Revenue - CostAndFees;

    public string Customer => Details.Customer;
    public string PiecesText => Lines.Count == 0 ? "(no items yet)" : string.Join(", ", Lines.Select(l => l.ItemTitle).Distinct());
    public OrderStatus? Status => Details.Status;
    public string TrackingText => Details is { TrackingCode.Length: > 0 } d ? $"{d.Carrier} {d.TrackingCode}".Trim() : "";

    /// <summary>True when some items are made and shipped by you rather than Printify.</summary>
    public bool ShipYourself => Lines.Any(l => l.Line.Fulfillment == Fulfillment.Self);
}

public sealed record SalesTotals(int Orders, int Items, decimal Revenue, decimal Cost, decimal Shipping, decimal Fees, decimal TaxCollected, decimal Profit);

public static class SalesReport
{
    /// <summary>Every order with its lines resolved to products and pieces.</summary>
    public static IReadOnlyList<OrderSummary> Orders(IEnumerable<Order> orders, IReadOnlyCollection<Product> products, IEnumerable<ArtPieceDocument> docs)
    {
        var productsById = products.GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First());
        var piecesById = docs.GroupBy(d => d.Piece.Id).ToDictionary(g => g.Key, g => g.First());
        return orders
            .Select(o => new OrderSummary(o, o.Lines.Select(l => Row(o, l, productsById, piecesById)).ToList()))
            .OrderByDescending(o => o.Date)
            .ThenByDescending(o => o.OrderRef, Naming.NameRules.NaturalComparer)
            .ToList();
    }

    /// <summary>Every order line, newest first.</summary>
    public static IReadOnlyList<SaleRow> Rows(IEnumerable<Order> orders, IReadOnlyCollection<Product> products, IEnumerable<ArtPieceDocument> docs) =>
        Orders(orders, products, docs).SelectMany(o => o.Lines).ToList();

    /// <summary>The lines that sold this art piece: directly, or through a product that shows it.</summary>
    public static IReadOnlyList<SaleRow> ForPiece(IEnumerable<SaleRow> rows, Guid pieceId) =>
        rows.Where(r => r.Line.PieceId == pieceId || (r.Product is { } p && ProductCatalog.Shows(p, pieceId))).ToList();

    public static IReadOnlyList<SaleRow> ForProduct(IEnumerable<SaleRow> rows, Guid productId) =>
        rows.Where(r => r.Line.ProductId == productId).ToList();

    private static SaleRow Row(Order order, OrderLine line, Dictionary<Guid, Product> products, Dictionary<Guid, ArtPieceDocument> pieces)
    {
        var product = line.ProductId is { } id && products.TryGetValue(id, out var p) ? p : null;
        var pieceId = line.PieceId ?? (product?.Pieces.Count == 1 ? product.Pieces[0] : null);
        var piece = pieceId is { } pid && pieces.TryGetValue(pid, out var d) ? d : null;
        return new SaleRow(order, line, product, piece, product is null ? null : BundleCost(product, products));
    }

    /// <summary>
    /// The production cost of a bundle product (e.g. "Set of 3 prints"): the sum of the included products' costs
    /// times their quantity. Null when the product isn't a bundle.
    /// </summary>
    public static decimal? BundleCost(Product product, IReadOnlyDictionary<Guid, Product> productsById)
    {
        if (product.Includes.Count == 0) return null;
        decimal total = 0;
        foreach (var item in product.Includes)
        {
            var cost = item.ProductId is { } id && productsById.TryGetValue(id, out var included) ? included.ProductionCost ?? 0 : 0;
            total += cost * Math.Max(1, item.Quantity);
        }
        return total;
    }

    public static SalesTotals Totals(IReadOnlyCollection<OrderSummary> orders) => new(
        orders.Count,
        orders.Sum(o => o.Items),
        orders.Sum(o => o.Revenue),
        orders.Sum(o => o.ProductionCost),
        orders.Sum(o => o.Shipping),
        orders.Sum(o => o.Fees),
        orders.Sum(o => o.TaxCollected),
        orders.Sum(o => o.Profit));
}

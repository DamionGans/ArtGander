using ArtGander.Core.Model;
using ArtGander.Core.Storage;

namespace ArtGander.Core.Services;

/// <summary>A sale line flattened together with its piece and product, for library-wide order views.</summary>
public sealed record SaleRow(
    ArtPieceDocument Piece,
    Sale Sale,
    Product? Product)
{
    public string PieceTitle => Piece.Piece.Title;
    public string ProductName => Product?.Name ?? "(unknown product)";
    public decimal Revenue => Sale.UnitPrice * Sale.Quantity;
    public decimal Cost => (Sale.UnitCost ?? Product?.ProductionCost ?? 0m) * Sale.Quantity;
    public decimal Profit => Revenue - Cost - Sale.Fees;
}

/// <summary>
/// An order: every sale line across the library that shares an order reference, plus the optional order details
/// (what the customer paid, shipping, tracking…).
/// <para>
/// Money logic, kept simple:
/// revenue = what the customer paid (or the sum of item prices) minus the tax in it;
/// costs = production (the actual bill, or the products' costs) + production tax + shipping + shipping tax + fees.
/// </para>
/// </summary>
public sealed record OrderSummary(string OrderRef, DateOnly Date, string Channel, IReadOnlyList<SaleRow> Lines, Order? Details)
{
    public int Items => Lines.Sum(l => l.Sale.Quantity);
    public decimal ItemsTotal => Lines.Sum(l => l.Revenue);

    /// <summary>What the customer paid, including tax and any shipping they were charged.</summary>
    public decimal Paid => Details?.AmountPaid ?? ItemsTotal;
    public decimal TaxCollected => Details?.AmountPaidTax ?? 0;
    public decimal Revenue => Paid - TaxCollected;

    public decimal ProductionCost => (Details?.ProductionCost ?? Lines.Sum(l => l.Cost)) + (Details?.ProductionTax ?? 0);
    public decimal Shipping => (Details?.ShippingCost ?? 0) + (Details?.ShippingTax ?? 0);
    public decimal Fees => Lines.Sum(l => l.Sale.Fees);
    public decimal Cost => ProductionCost;
    public decimal CostAndFees => ProductionCost + Shipping + Fees;
    public decimal Profit => Revenue - CostAndFees;

    public string Customer => Details?.Customer is { Length: > 0 } c ? c
        : Lines.Select(l => l.Sale.Customer).FirstOrDefault(c => !string.IsNullOrWhiteSpace(c)) ?? "";
    public string PiecesText => Lines.Count == 0 ? "(no items yet)" : string.Join(", ", Lines.Select(l => l.PieceTitle).Distinct());
    public OrderStatus? Status => Details?.Status;
    public string TrackingText => Details is { TrackingCode.Length: > 0 } d ? $"{d.Carrier} {d.TrackingCode}".Trim() : "";
}

public sealed record SalesTotals(int Orders, int Items, decimal Revenue, decimal Cost, decimal Shipping, decimal Fees, decimal TaxCollected, decimal Profit);

public static class SalesReport
{
    private const string NoRef = "\0no-ref:";

    public static IReadOnlyList<SaleRow> Rows(IEnumerable<ArtPieceDocument> docs) =>
        docs.SelectMany(d => d.Piece.Sales.Select(s => new SaleRow(d, s, d.Piece.FindProduct(s.ProductId))))
            .OrderByDescending(r => r.Sale.Date)
            .ToList();

    /// <summary>
    /// Groups sale lines into orders and attaches order details by reference. Lines without an order reference
    /// each count as their own order. Order details without any lines still show up (e.g. a newly created order).
    /// </summary>
    public static IReadOnlyList<OrderSummary> Orders(IEnumerable<SaleRow> rows, IEnumerable<Order>? details = null)
    {
        var byRef = (details ?? [])
            .Where(o => !string.IsNullOrWhiteSpace(o.OrderRef))
            .GroupBy(o => o.OrderRef.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var result = rows
            .GroupBy(r => string.IsNullOrWhiteSpace(r.Sale.OrderRef) ? NoRef + r.Sale.Id : r.Sale.OrderRef.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var lines = g.ToList();
                var first = lines.MinBy(l => l.Sale.Date)!;
                var orderRef = g.Key.StartsWith(NoRef, StringComparison.Ordinal) ? "" : g.Key;
                var order = orderRef.Length > 0 && byRef.Remove(orderRef, out var o) ? o : null;
                return new OrderSummary(orderRef, order?.Date ?? first.Sale.Date, order?.Channel ?? first.Sale.Channel, lines, order);
            })
            .ToList();

        // Orders that have details but no items (yet).
        result.AddRange(byRef.Values.Select(o => new OrderSummary(o.OrderRef, o.Date, o.Channel, [], o)));
        return result.OrderByDescending(o => o.Date).ToList();
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

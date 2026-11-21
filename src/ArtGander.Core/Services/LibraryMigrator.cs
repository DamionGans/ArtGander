using ArtGander.Core.Model;
using ArtGander.Core.Naming;
using ArtGander.Core.Storage;

namespace ArtGander.Core.Services;

/// <summary>
/// Moves products and sales that are still stored inside art pieces (format version 2) to their own product and
/// order files. Safe to run again at any time: product ids and sale ids are kept, so nothing is copied twice, and a
/// piece is only cleaned up after its products and orders were written.
/// </summary>
public static class LibraryMigrator
{
    public sealed record Result(int Products, int OrderLines, int Pieces, IReadOnlyList<string> Problems);

    public static bool NeedsMigration(IEnumerable<ArtPieceDocument> docs) =>
        docs.Any(d => !d.IsReadOnly && (d.Piece.LegacyProducts is { Count: > 0 } || d.Piece.LegacySales is { Count: > 0 }));

    /// <summary>Migrates every piece that still has products or sales inside. Takes the library lock while working.</summary>
    public static Result Run(string rootPath, IEnumerable<ArtPieceDocument> docs, ArtPieceStore store, ProductCatalog catalog)
    {
        var todo = docs.Where(d => !d.IsReadOnly && (d.Piece.LegacyProducts is { Count: > 0 } || d.Piece.LegacySales is { Count: > 0 })).ToList();
        if (todo.Count == 0) return new Result(0, 0, 0, []);

        using var libraryLock = LibraryLock.Acquire(rootPath, "upgrading the library");
        if (catalog.RootPath != rootPath) catalog.Load(rootPath);
        var orders = Records.Orders.LoadAll(rootPath).ToList();
        var problems = new List<string>();
        int products = 0, lines = 0, pieces = 0;

        foreach (var doc in todo)
        {
            try
            {
                var piece = doc.Piece;
                var legacyById = (piece.LegacyProducts ?? []).ToDictionary(p => p.Id);

                foreach (var legacy in piece.LegacyProducts ?? [])
                {
                    if (catalog.Find(legacy.Id) is { } existing)
                    {
                        if (!existing.Pieces.Contains(piece.Id)) { existing.Pieces.Add(piece.Id); catalog.Save(existing, force: true); }
                        continue;
                    }
                    catalog.Save(ToProduct(legacy, piece));
                    products++;
                }

                foreach (var sale in piece.LegacySales ?? [])
                {
                    var order = FindOrder(orders, sale);
                    if (order is null)
                    {
                        order = new Order
                        {
                            OrderRef = sale.OrderRef.Trim(),
                            Date = sale.Date,
                            Channel = sale.Channel,
                            Currency = sale.Currency,
                            Customer = sale.Customer,
                        };
                        orders.Add(order);
                    }
                    if (order.Lines.Any(l => l.Id == sale.Id)) continue;

                    legacyById.TryGetValue(sale.ProductId ?? Guid.Empty, out var legacyProduct);
                    var product = catalog.Find(sale.ProductId);
                    order.Lines.Add(new OrderLine
                    {
                        Id = sale.Id,
                        ProductId = product?.Id,
                        PieceId = piece.Id,
                        Title = product?.Name is { Length: > 0 } name ? name : legacyProduct?.Name is { Length: > 0 } n ? n : piece.Title,
                        Quantity = sale.Quantity,
                        UnitPrice = sale.UnitPrice,
                        UnitCost = sale.UnitCost,
                        Fees = sale.Fees,
                        Fulfillment = product?.Fulfillment ?? Fulfillment.Self,
                        Notes = JoinNotes(sale.Notes, !string.IsNullOrWhiteSpace(sale.Customer) && !NameRules.NamesEqual(sale.Customer, order.Customer) ? sale.Customer : ""),
                    });
                    if (string.IsNullOrWhiteSpace(order.Customer)) order.Customer = sale.Customer;
                    if (sale.Date < order.Date && order.StoredFileName is null) order.Date = sale.Date;
                    Records.Orders.Save(rootPath, order, force: true);
                    lines++;
                }

                piece.LegacyProducts = null;
                piece.LegacySales = null;
                store.Save(doc);
                pieces++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArtPieceFileException or LibraryBusyException)
            {
                problems.Add($"{doc.DisplayTitle}: {ex.Message}");
            }
            libraryLock.Heartbeat();
        }
        return new Result(products, lines, pieces, problems);
    }

    private static Order? FindOrder(List<Order> orders, LegacySale sale)
    {
        if (string.IsNullOrWhiteSpace(sale.OrderRef))
            return orders.FirstOrDefault(o => o.Lines.Any(l => l.Id == sale.Id));
        return orders.FirstOrDefault(o => NameRules.NamesEqual(o.OrderRef.Trim(), sale.OrderRef.Trim()));
    }

    private static string JoinNotes(string a, string b) =>
        string.Join(" · ", new[] { a, b }.Where(s => !string.IsNullOrWhiteSpace(s)));

    private static Product ToProduct(LegacyProduct legacy, ArtPiece piece)
    {
        var product = new Product
        {
            Id = legacy.Id,
            Name = string.IsNullOrWhiteSpace(legacy.Name) ? piece.Title : legacy.Name,
            Type = legacy.Type,
            Provider = legacy.Provider,
            Variant = legacy.Variant,
            Price = legacy.Price,
            ProductionCost = legacy.ProductionCost,
            Currency = legacy.Currency,
            EditionSize = legacy.EditionSize,
            Stock = legacy.Stock,
            Notes = legacy.Notes,
            Status = legacy.Listings.Count > 0 && legacy.Listings.All(l => l.Status == ListingStatus.Retired) ? ProductStatus.Archived : ProductStatus.Active,
            Pieces = [piece.Id],
            Extra = legacy.Extra,
        };
        foreach (var f in legacy.Files) product.Files.Add(new ProductFile { PieceId = piece.Id, FileName = f });
        foreach (var c in legacy.CostBreakdown) product.CostBreakdown.Add(c);
        foreach (var i in legacy.Includes) product.Includes.Add(i);
        foreach (var l in legacy.Listings)
            product.Listings.Add(new Listing { Channel = l.Channel, Url = l.Url, ExternalId = l.ExternalId, Price = l.Price, Status = l.Status, Extra = l.Extra });
        return product;
    }
}

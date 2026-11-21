using ArtGander.Core.Model;
using ArtGander.Core.Services;
using ArtGander.Core.Storage;

namespace ArtGander.Core.Integrations;

/// <summary>Fetches products and orders from Printify and Shopify and imports what you approve.</summary>
public sealed class ImportService(Connections connections, ProductCatalog catalog)
{
    public sealed record ApplyResult(int Saved, IReadOnlyList<string> Skipped);

    // ---- Products -------------------------------------------------------------------------------------

    /// <summary>Reads (only reads) every product in Printify and Shopify and pairs them with ArtGander's.</summary>
    public async Task<List<ProductImportCandidate>> FetchProductsAsync(LibrarySettings settings, IEnumerable<ArtPieceDocument> docs, CancellationToken ct)
    {
        var printify = await connections.PrintifyAsync();
        var shopify = await connections.ShopifyAsync(settings.Integrations);
        if (printify is null && shopify is null) throw new InvalidOperationException("Connect Printify or Shopify first (Settings → Shops).");

        IReadOnlyList<RemoteProduct> pp = [], sp = [];
        if (printify is not null && settings.Integrations.PrintifyShopId is { } shopId) pp = await printify.GetProductsAsync(shopId, ct);
        if (shopify is not null) sp = await shopify.GetProductsAsync(ct);
        return ProductImporter.Plan(pp, sp, catalog.All, docs);
    }

    /// <summary>Imports the chosen products (with the art pieces you picked for each).</summary>
    public async Task<ApplyResult> ImportProductsAsync(string root, LibrarySettings settings, IReadOnlyList<(ProductImportCandidate Candidate, IReadOnlyList<Guid> Pieces)> chosen, CancellationToken ct)
    {
        string? location = null;
        if (chosen.Any(c => c.Candidate.Shopify is not null && c.Candidate.Printify is null)
            && await connections.ShopifyAsync(settings.Integrations) is { } shopify)
        {
            try { location = await shopify.GetPrimaryLocationIdAsync(ct); } catch (Exception) { /* stock sync can pick it later */ }
        }

        using var libraryLock = LibraryLock.Acquire(root, "importing products");
        var saved = 0;
        var skipped = new List<string>();
        foreach (var (candidate, pieces) in chosen)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // Always work on the newest version of an existing product.
                var current = candidate.Existing is { } e ? catalog.Reload(e) ?? e : null;
                var c = current is null ? candidate : new ProductImportCandidate
                {
                    Printify = candidate.Printify, Shopify = candidate.Shopify, Existing = current, SuggestedPieces = candidate.SuggestedPieces,
                };
                var product = ProductImporter.Apply(c, pieces, settings.DefaultCurrency, location);
                catalog.Save(product);
                saved++;
            }
            catch (Exception ex) when (ex is IOException or ConcurrentEditException or InvalidOperationException)
            {
                skipped.Add($"{candidate.Title}: {ex.Message}");
            }
            libraryLock.Heartbeat();
        }
        var state = SyncState.Load(root);
        state.LastProductImport = DateTimeOffset.Now;
        state.Save(root);
        return new ApplyResult(saved, skipped);
    }

    // ---- Orders ---------------------------------------------------------------------------------------

    /// <summary>
    /// Reads (only reads) the orders changed since the last import (all orders the first time) and works out
    /// what importing them would change.
    /// </summary>
    public async Task<List<OrderImportItem>> FetchOrdersAsync(string root, LibrarySettings settings, IReadOnlyList<Order> existing, bool everything, CancellationToken ct)
    {
        var printify = await connections.PrintifyAsync();
        var shopify = await connections.ShopifyAsync(settings.Integrations);
        if (printify is null && shopify is null) throw new InvalidOperationException("Connect Printify or Shopify first (Settings → Shops).");

        var last = everything ? null : SyncState.Load(root).LastOrderImport;
        // Overlap generously: shipments and refunds arrive days or weeks after an order was placed.
        var shopifySince = last?.AddDays(-2);
        var printifySince = last?.AddDays(-60);

        IReadOnlyList<RemoteOrder> so = [], po = [];
        if (shopify is not null) so = await shopify.GetOrdersAsync(shopifySince, ct);
        if (printify is not null && settings.Integrations.PrintifyShopId is { } shopId) po = await printify.GetOrdersAsync(shopId, printifySince, ct);
        return OrderImporter.Plan(so, po, existing, catalog.All, settings.DefaultCurrency).Where(i => i.HasChanges).ToList();
    }

    /// <summary>Saves the imported orders. An order changed on another computer in the meantime is skipped (import again).</summary>
    public ApplyResult ApplyOrders(string root, IReadOnlyList<(OrderImportItem Item, IReadOnlySet<string> TakeRemote)> chosen, bool markImported)
    {
        using var libraryLock = LibraryLock.Acquire(root, "importing orders");
        var saved = 0;
        var skipped = new List<string>();
        foreach (var (item, takeRemote) in chosen)
        {
            try
            {
                OrderImporter.Apply(item, takeRemote);
                Records.Orders.Save(root, item.Order);
                saved++;
            }
            catch (ConcurrentEditException)
            {
                skipped.Add($"{item.Title}: changed on another computer meanwhile; import again to include it.");
            }
            catch (IOException ex)
            {
                skipped.Add($"{item.Title}: {ex.Message}");
            }
            libraryLock.Heartbeat();
        }
        if (markImported && skipped.Count == 0)
        {
            var state = SyncState.Load(root);
            state.LastOrderImport = DateTimeOffset.Now;
            state.Save(root);
        }
        return new ApplyResult(saved, skipped);
    }
}

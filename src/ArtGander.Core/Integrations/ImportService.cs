using ArtGander.Core.Model;
using ArtGander.Core.Services;
using ArtGander.Core.Storage;

namespace ArtGander.Core.Integrations;

/// <summary>Fetches products and orders from Printify and Shopify and imports what you approve.</summary>
public sealed class ImportService(Connections connections, ProductCatalog catalog, HttpClient? http = null)
{
    private PictureDownloader? Download => http is null ? null : ShopPictures.HttpDownloader(http);
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
                // Its pictures: Printify's mockups, or the Shopify pictures (now that the product has its folder).
                if (Download is { } download)
                {
                    try
                    {
                        if (await ShopPictures.DownloadFromShopAsync(product, c.Printify, c.Shopify, download, ct) > 0) catalog.Save(product);
                    }
                    catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
                    {
                        skipped.Add($"{candidate.Title}: imported, but not all pictures could be downloaded ({ex.Message})");
                    }
                }
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

    // ---- Product sync -----------------------------------------------------------------------------------

    /// <summary>Reads the shops and works out, field by field, what syncing the linked products would do. Read-only.</summary>
    public async Task<List<ProductSyncPlan>> CheckProductsAsync(LibrarySettings settings, CancellationToken ct)
    {
        var printify = await connections.PrintifyAsync();
        var shopify = await connections.ShopifyAsync(settings.Integrations);
        var linked = catalog.All.Where(p => p.IsOnPrintify || p.IsOnShopify).ToList();
        IReadOnlyList<RemoteProduct> pp = [], sp = [];
        if (linked.Any(p => p.IsOnPrintify))
        {
            if (printify is null || settings.Integrations.PrintifyShopId is not { } shopId) throw new InvalidOperationException("Printify isn't connected on this computer (Settings → Shops).");
            pp = await printify.GetProductsAsync(shopId, ct);
        }
        if (linked.Any(p => p.IsOnShopify))
        {
            if (shopify is null) throw new InvalidOperationException("Shopify isn't connected on this computer (Settings → Shops).");
            sp = await shopify.GetProductsAsync(ct);
        }
        return SyncPlanner.Plan(linked, pp, sp);
    }

    /// <summary>Carries out the approved sync, and writes a log in <c>.artgander/sync/log/</c>.</summary>
    public async Task<List<SyncOutcome>> ApplySyncAsync(string root, LibrarySettings settings, IReadOnlyList<ProductSyncPlan> plans,
        IReadOnlyDictionary<string, SyncResolution> choices, CancellationToken ct)
    {
        var runner = new SyncRunner(catalog, await connections.PrintifyAsync(), await connections.ShopifyAsync(settings.Integrations), Download);
        var outcomes = await runner.ApplyAsync(root, plans, choices, ct);
        WriteLog(root, plans, choices, outcomes);
        return outcomes;
    }

    /// <summary>
    /// Gets a product's pictures from the shops: Printify's current mockups (for products that show them), or the
    /// Shopify pictures that aren't in ArtGander yet. Saves the product. Returns how many pictures were added.
    /// </summary>
    public async Task<int> DownloadPicturesAsync(LibrarySettings settings, Product product, CancellationToken ct)
    {
        if (Download is not { } download) throw new InvalidOperationException("Downloading isn't available.");
        RemoteProduct? p = null, s = null;
        if (product.Channels.Printify is { ProductId.Length: > 0 } pl && product.EffectivePictures == PictureMode.PrintifyMockups)
        {
            var printify = await connections.PrintifyAsync() ?? throw new InvalidOperationException("Printify isn't connected on this computer (Settings → Shops).");
            p = await printify.GetProductAsync(pl.ShopId, pl.ProductId, ct);
        }
        else if (product.Channels.Shopify is { ProductId.Length: > 0 } sl)
        {
            var shopify = await connections.ShopifyAsync(settings.Integrations) ?? throw new InvalidOperationException("Shopify isn't connected on this computer (Settings → Shops).");
            s = await shopify.GetProductAsync(sl.ProductId, ct);
        }
        else throw new InvalidOperationException("This product isn't linked to Printify or Shopify yet.");
        var added = await ShopPictures.DownloadFromShopAsync(product, p, s, download, ct);
        catalog.Save(product);
        return added;
    }

    private static void WriteLog(string root, IReadOnlyList<ProductSyncPlan> plans, IReadOnlyDictionary<string, SyncResolution> choices, List<SyncOutcome> outcomes)
    {
        try
        {
            var folder = Path.Combine(root, Schema.CurrentSchema.LibraryFolderName, "sync", "log");
            Directory.CreateDirectory(folder);
            var lines = new List<string> { $"# Sync {DateTimeOffset.Now:O} on {Environment.MachineName}" };
            foreach (var plan in plans)
            {
                var outcome = outcomes.FirstOrDefault(o => o.Product.Id == plan.Product.Id);
                lines.Add($"{plan.Product.Name}: {(outcome is null ? plan.Problem ?? "not run" : (outcome.Done ? "done" : "skipped") + " - " + outcome.Message)}");
                foreach (var c in plan.Changes)
                {
                    var choice = choices.TryGetValue(SyncRunner.ChoiceKey(plan.Product, c), out var r) ? r : c.DefaultResolution;
                    lines.Add($"  {c.Channel} {c.Label}: base \"{c.Base}\" | ArtGander \"{c.Local}\" | shop \"{c.Remote}\" -> {choice}");
                }
            }
            File.AppendAllLines(Path.Combine(folder, $"{DateTime.Now:yyyy-MM}.log"), lines);
        }
        catch (IOException) { /* the log is a convenience */ }
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

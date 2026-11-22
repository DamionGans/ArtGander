using System.Globalization;
using ArtGander.Core.Model;
using ArtGander.Core.Services;
using ArtGander.Core.Storage;

namespace ArtGander.Core.Integrations;

/// <summary>The Printify calls a sync needs (implemented by <see cref="PrintifyClient"/>; faked in tests).</summary>
public interface IPrintifyApi
{
    Task<RemoteProduct> GetProductAsync(long shopId, string productId, CancellationToken ct = default);
    Task UpdateProductAsync(long shopId, string productId, PrintifyUpdate update, CancellationToken ct = default);
    Task PublishAsync(long shopId, string productId, PublishParts parts, CancellationToken ct = default);
}

/// <summary>The Shopify calls a sync needs (implemented by <see cref="ShopifyClient"/>; faked in tests).</summary>
public interface IShopifyApi
{
    Task<RemoteProduct?> GetProductAsync(string id, CancellationToken ct = default);
    Task UpdateProductAsync(string productId, string? title, string? descriptionHtml, IReadOnlyList<string>? tags, string? status,
        IReadOnlyList<RemoteVariant>? variantPrices, CancellationToken ct = default);
    Task SetStockAsync(string locationId, IReadOnlyList<(string InventoryItemId, int Quantity, int? ExpectedCurrent)> items, CancellationToken ct = default);
    Task<string?> GetPrimaryLocationIdAsync(CancellationToken ct = default);
    Task<IReadOnlyList<string>> AddMediaAsync(string productId, IReadOnlyList<(string Path, string Alt)> pictures, CancellationToken ct = default);
    Task UpdateMediaAltAsync(IReadOnlyList<(string MediaId, string Alt)> changes, CancellationToken ct = default);
    Task RemoveMediaAsync(string productId, IReadOnlyList<string> mediaIds, CancellationToken ct = default);
    Task ReorderMediaAsync(string productId, IReadOnlyList<string> orderedMediaIds, CancellationToken ct = default);
}

public sealed record SyncOutcome(Product Product, bool Done, string Message);

/// <summary>
/// Carries out a sync you approved in the preview. For every product it first fetches the shops' current version again
/// and skips the product when something changed since the preview, so nothing that happened in between is overwritten.
/// Afterwards it records the new agreed values (the base); fields you chose to skip keep their old base, so they are
/// shown again next time instead of being decided silently.
/// </summary>
public sealed class SyncRunner(ProductCatalog catalog, IPrintifyApi? printify, IShopifyApi? shopify, PictureDownloader? download = null)
{
    private static readonly HashSet<string> TextFields = ["title", "description", "tags"];

    public async Task<List<SyncOutcome>> ApplyAsync(string root, IReadOnlyList<ProductSyncPlan> plans,
        IReadOnlyDictionary<string, SyncResolution> choices, CancellationToken ct = default)
    {
        var outcomes = new List<SyncOutcome>();
        using var libraryLock = LibraryLock.Acquire(root, "syncing with the shops");
        foreach (var plan in plans.Where(p => p.HasWork))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                outcomes.Add(await ApplyOneAsync(plan, choices, ct));
            }
            catch (Exception ex) when (ex is ApiException or ShopifyException or IOException or HttpRequestException)
            {
                outcomes.Add(new SyncOutcome(plan.Product, false, ex.Message));
            }
            libraryLock.Heartbeat();
        }
        return outcomes;
    }

    /// <summary>The key under which a choice for a field of a product is passed to <see cref="ApplyAsync"/>.</summary>
    public static string ChoiceKey(Product product, SyncFieldChange change) => product.Id + "|" + change.Key;

    private async Task<SyncOutcome> ApplyOneAsync(ProductSyncPlan plan, IReadOnlyDictionary<string, SyncResolution> choices, CancellationToken ct)
    {
        // 1. The newest ArtGander version: if another computer changed the product since the preview, check again first.
        var product = plan.Product;
        if (catalog.ChangedOnDisk(product))
            return new SyncOutcome(product, false, "Changed in ArtGander (on another computer?) since the check. Check again.");

        // 2. The shops' newest versions: anything changed since the preview is skipped.
        RemoteProduct? p = null, s = null;
        var pLink = product.Channels.Printify;
        var sLink = product.Channels.Shopify;
        if (plan.Printify is not null && printify is not null && pLink is not null)
        {
            p = await printify.GetProductAsync(pLink.ShopId, pLink.ProductId, ct);
            if (p.UpdatedAt != plan.Printify.UpdatedAt) return new SyncOutcome(product, false, "Changed in Printify since the check. Check again.");
            if (p.IsLocked) return new SyncOutcome(product, false, "Printify is still publishing it. Try again in a few minutes.");
        }
        if (plan.Shopify is not null && shopify is not null && sLink is not null)
        {
            s = await shopify.GetProductAsync(sLink.ProductId, ct);
            if (s is null) return new SyncOutcome(product, false, "Not found in Shopify anymore.");
            if (s.UpdatedAt != plan.Shopify.UpdatedAt) return new SyncOutcome(product, false, "Changed in Shopify since the check. Check again.");
        }

        // 3. Apply your choices: take over shop values into ArtGander, collect what to push.
        var push = new HashSet<(string Field, string? Variant)>();
        var skipped = new List<SyncFieldChange>();
        var pictures = SyncResolution.Skip;
        foreach (var change in plan.Changes)
        {
            var choice = choices.TryGetValue(ChoiceKey(product, change), out var r) ? r : change.DefaultResolution;
            if (change.Field == "pictures")
            {
                pictures = choice;
                if (choice == SyncResolution.Skip) skipped.Add(change);
                continue;
            }
            switch (choice)
            {
                case SyncResolution.TakeRemote:
                    SetLocal(product, change.Field, change.VariantKey, change.Remote);
                    // Taken over from Shopify on a Printify product: Printify must get it too, or its next publish undoes it.
                    if (change.Channel == "shopify" && p is not null && change.Field is not ("status" or "stock" or "pictures")) push.Add((change.Field, change.VariantKey));
                    break;
                case SyncResolution.Push:
                    push.Add((change.Field, change.VariantKey));
                    break;
                default:
                    skipped.Add(change);
                    break;
            }
        }

        // 4. Write to the owner of each field.
        var published = false;
        List<string>? picturesBase = null;
        if (p is not null && pLink is not null)
        {
            var texts = push.Where(x => TextFields.Contains(x.Field)).Select(x => x.Field).ToHashSet();
            var variantPush = push.Any(x => x.Field is "price" or "enabled");
            if (texts.Count > 0 || variantPush)
            {
                var variants = variantPush ? MergedVariants(product, p, push) : null;
                await printify!.UpdateProductAsync(pLink.ShopId, pLink.ProductId, new PrintifyUpdate(
                    texts.Contains("title") ? product.Name : null,
                    texts.Contains("description") ? product.Description : null,
                    texts.Contains("tags") ? [.. product.Tags] : null,
                    variants), ct);
                if (plan.Shopify is not null)
                {
                    await printify.PublishAsync(pLink.ShopId, pLink.ProductId,
                        new PublishParts(texts.Contains("title"), texts.Contains("description"), texts.Contains("tags"), variantPush), ct);
                    published = true;
                }
            }
        }
        if (s is not null && sLink is not null)
        {
            var direct = p is null; // self-made products: Shopify owns texts and prices
            string? title = direct && push.Contains(("title", null)) ? product.Name : null;
            string? description = direct && push.Contains(("description", null)) ? product.Description : null;
            IReadOnlyList<string>? tags = direct && push.Contains(("tags", null)) ? [.. product.Tags] : null;
            string? status = push.Contains(("status", null)) ? SyncPlanner.StatusOf(product) : null;
            var prices = direct
                ? s.Variants.Where(v => ProductImporter.VariantKeyFor(product, s, v) is { } key && push.Contains(("price", key)))
                    .Select(v => v with { Price = PriceFor(product, ProductImporter.VariantKeyFor(product, s, v)!) }).ToList()
                : [];
            if (title is not null || description is not null || tags is not null || status is not null || prices.Count > 0)
                await shopify!.UpdateProductAsync(sLink.ProductId, title, description, tags, status, prices, ct);

            var stock = direct
                ? s.Variants.Where(v => v.InventoryItemId is not null && ProductImporter.VariantKeyFor(product, s, v) is { } key && push.Contains(("stock", key)))
                    .Select(v => (v.InventoryItemId!, StockFor(product, ProductImporter.VariantKeyFor(product, s, v)!) ?? 0, v.Stock)).ToList()
                : [];
            if (stock.Count > 0)
            {
                sLink.InventoryLocationId ??= await shopify!.GetPrimaryLocationIdAsync(ct);
                if (sLink.InventoryLocationId is null) return new SyncOutcome(product, false, "Shopify has no location for stock.");
                await shopify!.SetStockAsync(sLink.InventoryLocationId, stock, ct);
            }

            if (pictures == SyncResolution.Push)
                picturesBase = await ShopPictures.PushAsync(product, sLink.ProductId, s, sLink.Base?.Pictures, shopify!, ct);
            else if (pictures == SyncResolution.TakeRemote)
            {
                if (download is null) return new SyncOutcome(product, false, "Can't download pictures here.");
                picturesBase = await ShopPictures.TakeOverAsync(product, s, download, ct);
            }
        }

        // 5. Record the new agreed values; skipped fields keep their old base so they come back next time.
        var now = DateTimeOffset.Now;
        if (p is not null && pLink is not null)
        {
            var fresh = await printify!.GetProductAsync(pLink.ShopId, pLink.ProductId, ct);
            var old = pLink.Base;
            pLink.Base = ProductImporter.Snapshot(fresh, product);
            KeepSkipped(pLink.Base, old, skipped.Where(c => c.Channel == "printify"));
            pLink.RemoteUpdatedAt = fresh.UpdatedAt;
            pLink.LastSyncedAt = now;
            pLink.PublishPending = published;
            if (!skipped.Any(c => c.Channel == "printify")) pLink.NeedsReview = false;
        }
        if (s is not null && sLink is not null)
        {
            var fresh = await shopify!.GetProductAsync(sLink.ProductId, ct) ?? s;
            var old = sLink.Base;
            sLink.Base = ProductImporter.Snapshot(fresh, product);
            if (published)
            {
                // Printify is publishing the new values to Shopify right now: expect them.
                foreach (var (field, variant) in push.Where(x => TextFields.Contains(x.Field) || x.Field == "price"))
                    SetBase(sLink.Base, field, variant, LocalValue(product, field, variant));
            }
            KeepSkipped(sLink.Base, old, skipped.Where(c => c.Channel == "shopify"));
            // Shopify sorts and processes new pictures in the background: expect what was sent, not what it shows yet.
            sLink.Base.Pictures = picturesBase ?? (pictures == SyncResolution.Skip ? old?.Pictures : sLink.Base.Pictures);
            sLink.RemoteUpdatedAt = fresh.UpdatedAt;
            sLink.LastSyncedAt = now;
            sLink.Handle = fresh.Handle;
            if (!skipped.Any(c => c.Channel == "shopify")) sLink.NeedsReview = false;
            if (fresh.Url.Length > 0) sLink.Url = fresh.Url;
        }

        catalog.Save(product);
        var sent = push.Count + (pictures == SyncResolution.Push ? 1 : 0);
        var taken = plan.Changes.Count - sent - skipped.Count;
        return new SyncOutcome(product, true, string.Join(", ", new[]
        {
            sent > 0 ? $"{sent} change(s) sent" + (published ? " (Printify publishes them to Shopify)" : "") : "",
            taken > 0 ? $"{taken} taken over" : "",
            skipped.Count > 0 ? $"{skipped.Count} left for later" : "",
        }.Where(x => x.Length > 0)));
    }

    /// <summary>All of Printify's enabled-or-tracked variants, with ArtGander's price/on-sale where those are pushed.</summary>
    private static List<RemoteVariant> MergedVariants(Product product, RemoteProduct remote, HashSet<(string Field, string? Variant)> push)
    {
        var list = new List<RemoteVariant>();
        foreach (var rv in remote.Variants)
        {
            var key = ProductImporter.VariantKeyFor(product, remote, rv);
            var local = key is null ? null : product.FindVariant(key);
            if (!rv.Enabled && local is null) continue; // leave variants ArtGander doesn't track alone
            list.Add(rv with
            {
                Price = key is not null && push.Contains(("price", key)) ? PriceFor(product, key) : rv.Price,
                Enabled = key is not null && local is not null && push.Contains(("enabled", key)) ? local.Enabled : rv.Enabled,
            });
        }
        return list;
    }

    private static decimal? PriceFor(Product product, string key) => product.FindVariant(key) is { } v ? product.PriceOf(v) : product.Price;
    private static int? StockFor(Product product, string key) => product.FindVariant(key)?.Stock ?? (product.Variants.Count <= 1 ? product.Stock : null);

    private static string? LocalValue(Product product, string field, string? variant) => field switch
    {
        "title" => product.Name,
        "description" => product.Description,
        "tags" => SyncPlanner.Tags(product.Tags),
        "price" => SyncPlanner.Money(PriceFor(product, variant ?? "")),
        _ => null,
    };

    private static void SetLocal(Product product, string field, string? variant, string? value)
    {
        var v = variant is null ? null : product.FindVariant(variant);
        switch (field)
        {
            case "title": product.Name = value ?? ""; break;
            case "description": product.Description = value ?? ""; break;
            case "tags":
                product.Tags.Clear();
                foreach (var t in (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) product.Tags.Add(t);
                break;
            case "status":
                product.Status = value switch { "draft" => ProductStatus.Draft, "archived" => ProductStatus.Archived, _ => ProductStatus.Active };
                break;
            case "price":
                var price = decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : (decimal?)null;
                if (v is not null) v.Price = price; else product.Price = price;
                break;
            case "enabled":
                if (v is not null) v.Enabled = value == "yes";
                break;
            case "stock":
                var stock = int.TryParse(value, CultureInfo.InvariantCulture, out var i) ? i : (int?)null;
                if (v is not null) v.Stock = stock; else product.Stock = stock;
                break;
        }
    }

    private static void KeepSkipped(SyncSnapshot snapshot, SyncSnapshot? old, IEnumerable<SyncFieldChange> skipped)
    {
        foreach (var c in skipped) SetBase(snapshot, c.Field, c.VariantKey, c.Base);
    }

    private static void SetBase(SyncSnapshot snap, string field, string? variant, string? value)
    {
        switch (field)
        {
            case "title": snap.Title = value; return;
            case "description": snap.Description = value; return;
            case "tags": snap.Tags = value is null ? null : [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]; return;
            case "status": snap.Status = value; return;
        }
        var v = snap.Variants.FirstOrDefault(x => x.Key == (variant ?? ""));
        if (v is null) snap.Variants.Add(v = new VariantSnapshot { Key = variant ?? "" });
        switch (field)
        {
            case "price": v.Price = decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : null; break;
            case "enabled": v.Enabled = value == "yes"; break;
            case "stock": v.Stock = int.TryParse(value, CultureInfo.InvariantCulture, out var i) ? i : null; break;
        }
    }
}

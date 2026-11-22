using ArtGander.Core.Model;
using ArtGander.Core.Services;
using ArtGander.Core.Storage;

namespace ArtGander.Core.Integrations;

/// <summary>
/// Puts new products in the shops:
/// <list type="bullet">
/// <item>Printify products: created in Printify from a template product (same blueprint, print provider, variants and
/// placement, with your art), then published to the connected Shopify store.</item>
/// <item>Products you make and ship yourselves: created directly in Shopify, as a draft.</item>
/// </list>
/// Every step is recorded on the product right away, so a retry after a failure continues instead of creating a
/// second product.
/// </summary>
public sealed class ProductCreator(Connections connections, ProductCatalog catalog)
{
    public static TimeSpan PublishWait { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>The Printify products that can serve as templates.</summary>
    public async Task<IReadOnlyList<RemoteProduct>> GetTemplatesAsync(LibrarySettings settings, CancellationToken ct)
    {
        var printify = await connections.PrintifyAsync() ?? throw new InvalidOperationException("Printify isn't connected on this computer (Settings → Shops).");
        var shopId = settings.Integrations.PrintifyShopId ?? throw new InvalidOperationException("Pick your Printify shop first (Settings → Shops).");
        return await printify.GetProductsAsync(shopId, ct);
    }

    /// <summary>
    /// Creates the product in Printify from <paramref name="templateId"/> with <paramref name="artFile"/>, then publishes it.
    /// Returns a message for the user. When the product already exists in Printify, it only (re)publishes.
    /// </summary>
    public async Task<string> CreateOnPrintifyAsync(string root, LibrarySettings settings, Product product, string templateId, string artFile, CancellationToken ct)
    {
        var printify = await connections.PrintifyAsync() ?? throw new InvalidOperationException("Printify isn't connected on this computer (Settings → Shops).");
        var shopId = settings.Integrations.PrintifyShopId ?? throw new InvalidOperationException("Pick your Printify shop first (Settings → Shops).");
        using var libraryLock = LibraryLock.Acquire(root, "creating a product");

        if (!product.IsOnPrintify)
        {
            var template = await printify.GetProductJsonAsync(shopId, templateId, ct) ?? throw new InvalidOperationException("The template product wasn't found in Printify.");
            var imageId = await printify.UploadImageAsync(Path.GetFileName(artFile), await File.ReadAllBytesAsync(artFile, ct), ct);
            var prices = product.Variants.Where(v => v.PrintifyVariantId is not null && product.PriceOf(v) is not null)
                .ToDictionary(v => v.PrintifyVariantId!.Value.ToString(), v => product.PriceOf(v)!.Value);
            if (prices.Count == 0 && product.Price is { } price)
                foreach (var v in template.Arr("variants").Where(v => v.Bool("is_enabled"))) prices[v.Str("id")] = price;

            var id = await printify.CreateFromTemplateAsync(shopId, template, imageId, product.Name, product.Description, [.. product.Tags], prices, ct);
            // Record it straight away: from now on a retry never creates a second product.
            product.Channels.Printify = new PrintifyLink { ShopId = shopId, ProductId = id, PublishPending = true, LastSyncedAt = DateTimeOffset.Now };
            product.Type = ProductType.PrintOnDemand;
            catalog.Save(product);
        }

        var link = product.Channels.Printify!;
        var created = await printify.GetProductAsync(link.ShopId, link.ProductId, ct);
        AdoptPrintifyVariants(product, created);
        link.BlueprintId = created.BlueprintId;
        link.PrintProviderId = created.PrintProviderId;
        link.Base = ProductImporter.Snapshot(created, product);
        link.RemoteUpdatedAt = created.UpdatedAt;

        if (!created.IsLocked && created.ExternalId is null)
        {
            await printify.PublishAsync(link.ShopId, link.ProductId, new PublishParts(true, true, true, true, Images: true), ct);
            link.PublishPending = true;
            link.LastSyncedAt = DateTimeOffset.Now;
        }
        catalog.Save(product);
        libraryLock.Heartbeat();

        return await FinishPublishingAsync(settings, product, waitUpTo: PublishWait, ct);
    }

    /// <summary>Links the Shopify listing that Printify created, once it exists. Safe to call again later.</summary>
    public async Task<string> FinishPublishingAsync(LibrarySettings settings, Product product, TimeSpan waitUpTo, CancellationToken ct)
    {
        var printify = await connections.PrintifyAsync() ?? throw new InvalidOperationException("Printify isn't connected on this computer.");
        var link = product.Channels.Printify ?? throw new InvalidOperationException("This product isn't in Printify yet.");
        var deadline = DateTimeOffset.Now + waitUpTo;
        RemoteProduct current;
        while (true)
        {
            current = await printify.GetProductAsync(link.ShopId, link.ProductId, ct);
            if (current.ExternalId is not null || DateTimeOffset.Now >= deadline) break;
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
        }
        if (current.ExternalId is null)
            return "Created in Printify; Printify is still publishing it to Shopify. Use \"Finish publishing\" in a few minutes to link the Shopify listing.";

        var shopify = await connections.ShopifyAsync(settings.Integrations);
        var listing = shopify is null ? null : await shopify.GetProductAsync(current.ExternalId, ct);
        product.Channels.Shopify = new ShopifyLink
        {
            ProductId = listing?.Id ?? Ids.ShopifyProductGid(current.ExternalId),
            Handle = listing?.Handle ?? current.Handle,
            Url = listing?.Url ?? "",
            RemoteUpdatedAt = listing?.UpdatedAt,
            LastSyncedAt = DateTimeOffset.Now,
        };
        if (listing is not null)
        {
            ProductImporter.MapVariants(product, listing);
            product.Channels.Shopify.Base = ProductImporter.Snapshot(listing, product);
        }
        link.PublishPending = false;
        link.RemoteUpdatedAt = current.UpdatedAt;
        link.Base = ProductImporter.Snapshot(current, product);
        catalog.Save(product);
        return listing is null
            ? "Published. Connect Shopify on this computer to link the listing's details."
            : $"Published to Shopify as \"{listing.Title}\".";
    }

    /// <summary>Creates a product you make yourselves in Shopify, as a draft, with its variants, prices and stock.</summary>
    public async Task<string> CreateOnShopifyAsync(string root, LibrarySettings settings, Product product, CancellationToken ct)
    {
        if (product.IsOnShopify) return "It's already in Shopify.";
        var shopify = await connections.ShopifyAsync(settings.Integrations) ?? throw new InvalidOperationException("Shopify isn't connected on this computer (Settings → Shops).");
        using var libraryLock = LibraryLock.Acquire(root, "creating a product");

        var enabled = product.Variants.Where(v => v.Enabled).ToList();
        var variants = enabled.Count == 0
            ? [("Default Title", "", product.Price ?? 0m)]
            : enabled.Select(v => (v.Title.Length > 0 ? v.Title : v.Key, v.Sku, product.PriceOf(v) ?? 0m)).ToList();
        var id = await shopify.CreateProductAsync(product.Name, product.Description, [.. product.Tags], variants, ct);
        product.Channels.Shopify = new ShopifyLink { ProductId = id, LastSyncedAt = DateTimeOffset.Now };
        product.Status = ProductStatus.Draft;
        catalog.Save(product); // recorded before anything else can fail

        var created = await shopify.GetProductAsync(id, ct) ?? throw new InvalidOperationException("Shopify didn't return the new product.");
        foreach (var (local, remote) in enabled.Zip(created.Variants)) local.ShopifyVariantId = remote.Id;
        if (enabled.Count == 0 && created.Variants.Count == 1)
        {
            product.Variants.Add(new ProductVariant { Key = Ids.Numeric(created.Variants[0].Id), Title = "Default", ShopifyVariantId = created.Variants[0].Id, Stock = product.Stock });
        }

        // Stock: set what you have on hand (Shopify starts at 0).
        var location = await shopify.GetPrimaryLocationIdAsync(ct);
        var stock = created.Variants
            .Select(rv => (rv, local: product.Variants.FirstOrDefault(v => v.ShopifyVariantId == rv.Id)))
            .Where(x => x.rv.InventoryItemId is not null && (x.local?.Stock ?? product.Stock) is not null)
            .Select(x => (x.rv.InventoryItemId!, x.local?.Stock ?? product.Stock ?? 0, (int?)null))
            .ToList();
        if (location is not null && stock.Count > 0) await shopify.SetStockAsync(location, stock, ct);

        // The product's own pictures go along straight away.
        List<string>? pictures = null;
        string picturesNote = "";
        if (ShopPictures.ShopImages(product).Count > 0)
        {
            try { pictures = await ShopPictures.PushAsync(product, id, created, [], shopify, ct); }
            catch (Exception ex) when (ex is ShopifyException or ApiException or HttpRequestException or IOException)
            {
                picturesNote = $" The pictures couldn't be uploaded ({ex.Message}); Sync tries again.";
            }
        }

        var fresh = await shopify.GetProductAsync(id, ct) ?? created;
        var link = product.Channels.Shopify;
        link.Handle = fresh.Handle;
        link.Url = fresh.Url;
        link.InventoryLocationId = location;
        link.RemoteUpdatedAt = fresh.UpdatedAt;
        link.Base = ProductImporter.Snapshot(fresh, product);
        link.Base.Pictures = pictures ?? [];
        catalog.Save(product);
        return "Created in Shopify as a draft" + (pictures is { Count: > 0 } ? $" with {pictures.Count} picture(s)" : "")
            + ". Set the product to Active in ArtGander and sync to put it on sale." + picturesNote;
    }

    /// <summary>Takes over the enabled Printify variants as the product's variants (keeping prices you already set).</summary>
    private static void AdoptPrintifyVariants(Product product, RemoteProduct remote)
    {
        foreach (var rv in remote.EnabledVariants)
        {
            var key = Ids.Numeric(rv.Id);
            var local = product.Variants.FirstOrDefault(v => v.PrintifyVariantId?.ToString() == key) ?? product.Variants.FirstOrDefault(v => v.Key == key);
            if (local is null)
            {
                local = new ProductVariant { Key = key, Title = rv.Title, Sku = rv.Sku, Price = rv.Price, Cost = rv.Cost };
                product.Variants.Add(local);
            }
            local.PrintifyVariantId = long.TryParse(key, out var id) ? id : null;
            local.Cost ??= rv.Cost;
            if (local.Sku.Length == 0) local.Sku = rv.Sku;
        }
        product.ProductionCost ??= remote.EnabledVariants.Select(v => v.Cost).Where(c => c is not null).DefaultIfEmpty(null).Min();
        product.Price ??= remote.EnabledVariants.Select(v => v.Price).Where(c => c is not null).DefaultIfEmpty(null).Min();
    }
}

/// <summary>Marks the items you ship yourselves as shipped in Shopify (Shopify then emails the customer the tracking).</summary>
public sealed class FulfillmentService(Connections connections)
{
    public async Task<string> MarkShippedAsync(LibrarySettings settings, Order order, CancellationToken ct)
    {
        if (order.Sources.Shopify is not { } source) throw new InvalidOperationException("This order isn't from Shopify.");
        if (string.IsNullOrWhiteSpace(order.TrackingCode)) throw new InvalidOperationException("Enter the tracking code first.");
        var shopify = await connections.ShopifyAsync(settings.Integrations) ?? throw new InvalidOperationException("Shopify isn't connected on this computer (Settings → Shops).");
        var lines = order.Lines.Where(l => l.Fulfillment == Fulfillment.Self && l.ShopifyLineId is not null).Select(l => l.ShopifyLineId!).ToList();
        if (lines.Count == 0) throw new InvalidOperationException("This order has no items that you ship yourselves (Printify ships its own items).");
        await shopify.FulfillAsync(source.Id, lines, order.Carrier, order.TrackingCode.Trim(), ct);
        return $"Marked {lines.Count} item(s) as shipped in Shopify. Shopify emails the customer the tracking code.";
    }
}

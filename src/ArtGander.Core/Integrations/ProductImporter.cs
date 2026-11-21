using System.Text.RegularExpressions;
using ArtGander.Core.Model;
using ArtGander.Core.Storage;

namespace ArtGander.Core.Integrations;

/// <summary>A product found in Printify and/or Shopify, ready to import (or already imported).</summary>
public sealed class ProductImportCandidate
{
    public RemoteProduct? Printify { get; init; }
    public RemoteProduct? Shopify { get; init; }
    /// <summary>The ArtGander product it is already linked to, if any.</summary>
    public Product? Existing { get; init; }
    public List<Guid> SuggestedPieces { get; init; } = [];

    public RemoteProduct Main => Printify ?? Shopify!;
    public string Title => Main.Title;
    public bool IsNew => Existing is null;
    /// <summary>Linked already, and nothing new to connect.</summary>
    public bool IsUpToDate => Existing is not null
        && (Printify is null || Existing.Channels.Printify?.ProductId == Printify.Id)
        && (Shopify is null || Ids.SameShopifyId(Existing.Channels.Shopify?.ProductId, Shopify.Id));
    public string Where => Printify is not null && Shopify is not null ? "Printify + Shopify" : Printify is not null ? "Printify only" : "Shopify only";
}

/// <summary>
/// Imports existing Printify and Shopify products into ArtGander. Printify products published to Shopify are paired
/// (Printify's <c>external.id</c> is the Shopify product id), so each becomes one ArtGander product with both links.
/// Already-linked products are never duplicated, and their ArtGander texts and prices are never overwritten here:
/// differences show up in the sync preview instead.
/// </summary>
public static class ProductImporter
{
    public static List<ProductImportCandidate> Plan(IReadOnlyList<RemoteProduct> printify, IReadOnlyList<RemoteProduct> shopify,
        IReadOnlyList<Product> existing, IEnumerable<ArtPieceDocument> docs)
    {
        var pieces = docs.Where(d => !d.Piece.IsProject || true).ToList();
        var result = new List<ProductImportCandidate>();
        var pairedShopify = new HashSet<string>();

        foreach (var p in printify)
        {
            var s = p.ExternalId is { } ext ? shopify.FirstOrDefault(x => Ids.SameShopifyId(x.Id, ext)) : null;
            if (s is not null) pairedShopify.Add(s.Id);
            var match = existing.FirstOrDefault(e => e.Channels.Printify?.ProductId == p.Id)
                        ?? (s is null ? null : existing.FirstOrDefault(e => Ids.SameShopifyId(e.Channels.Shopify?.ProductId, s.Id)));
            result.Add(new ProductImportCandidate { Printify = p, Shopify = s, Existing = match, SuggestedPieces = Suggest(p, match, pieces) });
        }
        foreach (var s in shopify.Where(s => !pairedShopify.Contains(s.Id)))
        {
            var match = existing.FirstOrDefault(e => Ids.SameShopifyId(e.Channels.Shopify?.ProductId, s.Id));
            result.Add(new ProductImportCandidate { Shopify = s, Existing = match, SuggestedPieces = Suggest(s, match, pieces) });
        }
        return result.OrderBy(c => c.IsUpToDate).ThenBy(c => c.Title, Naming.NameRules.NaturalComparer).ToList();
    }

    /// <summary>Suggests the art piece(s) a product shows: the piece whose title appears in the product title (longest wins).</summary>
    public static List<Guid> Suggest(RemoteProduct product, Product? existing, IReadOnlyList<ArtPieceDocument> docs)
    {
        if (existing is { Pieces.Count: > 0 }) return [.. existing.Pieces];
        var words = Words(product.Title + " " + string.Join(" ", product.Tags));
        var best = docs
            .Select(d => (Doc: d, Words: Words(d.Piece.Title)))
            .Where(x => x.Words.Count > 0 && x.Words.All(words.Contains))
            .OrderByDescending(x => x.Words.Count)
            .ThenByDescending(x => x.Doc.Piece.Title.Length)
            .FirstOrDefault();
        return best.Doc is null ? [] : [best.Doc.Piece.Id];
    }

    private static HashSet<string> Words(string text) =>
        Regex.Matches(text.ToLowerInvariant(), @"[\p{L}\p{N}]+").Select(m => m.Value).Where(w => w.Length > 1 || char.IsDigit(w[0])).ToHashSet();

    /// <summary>
    /// Creates the ArtGander product for a candidate, or connects the platform links to the existing one.
    /// The caller saves the returned product.
    /// </summary>
    public static Product Apply(ProductImportCandidate c, IReadOnlyList<Guid> pieces, string currency, string? shopifyLocationId = null)
    {
        var now = DateTimeOffset.Now;
        var product = c.Existing ?? NewProduct(c, currency);
        foreach (var id in pieces) if (!product.Pieces.Contains(id)) product.Pieces.Add(id);

        if (c.Printify is { } p)
        {
            MapVariants(product, p);
            product.Channels.Printify ??= new PrintifyLink();
            var link = product.Channels.Printify;
            link.ProductId = p.Id;
            link.ShopId = p.ShopId ?? link.ShopId;
            link.BlueprintId = p.BlueprintId;
            link.PrintProviderId = p.PrintProviderId;
            link.RemoteUpdatedAt = p.UpdatedAt;
            link.Base = Snapshot(p, product);
            link.LastSyncedAt = now;
        }
        if (c.Shopify is { } s)
        {
            MapVariants(product, s);
            product.Channels.Shopify ??= new ShopifyLink();
            var link = product.Channels.Shopify;
            link.ProductId = s.Id;
            link.Handle = s.Handle;
            link.Url = s.Url;
            link.InventoryLocationId ??= shopifyLocationId;
            link.RemoteUpdatedAt = s.UpdatedAt;
            link.Base = Snapshot(s, product);
            link.LastSyncedAt = now;
        }
        return product;
    }

    private static Product NewProduct(ProductImportCandidate c, string currency)
    {
        var main = c.Main;
        var isPrintify = c.Printify is not null;
        var product = new Product
        {
            Name = main.Title,
            Description = main.Description,
            Type = isPrintify ? ProductType.PrintOnDemand : ProductType.SelfPrint,
            Provider = isPrintify ? "Printify" : "",
            Status = c.Shopify?.Status switch
            {
                "draft" => ProductStatus.Draft,
                "archived" => ProductStatus.Archived,
                _ => ProductStatus.Active,
            },
            Currency = currency,
        };
        foreach (var t in main.Tags) product.Tags.Add(t);

        var variants = main.EnabledVariants.ToList();
        foreach (var v in variants)
        {
            product.Variants.Add(new ProductVariant
            {
                Key = Ids.Numeric(v.Id),
                Title = v.Title,
                Sku = v.Sku,
                Price = v.Price,
                Cost = v.Cost,
                Stock = isPrintify ? null : v.Stock,
                Enabled = true,
            });
        }
        product.Price = variants.Select(v => v.Price).Where(x => x is not null).DefaultIfEmpty(null).Min();
        product.ProductionCost = variants.Select(v => v.Cost).Where(x => x is not null).DefaultIfEmpty(null).Min();
        if (!isPrintify) product.Stock = variants.Sum(v => v.Stock ?? 0);
        return product;
    }

    /// <summary>Connects the platform's variant ids to the ArtGander variants (by id, SKU or title).</summary>
    public static void MapVariants(Product product, RemoteProduct remote)
    {
        var printify = remote.Channel == "printify";
        foreach (var rv in remote.Variants)
        {
            if (printify && !rv.Enabled) continue;
            var numeric = Ids.Numeric(rv.Id);
            var local = product.Variants.FirstOrDefault(v => printify ? v.PrintifyVariantId?.ToString() == numeric : v.ShopifyVariantId == rv.Id)
                        ?? product.Variants.FirstOrDefault(v => v.Key == numeric)
                        ?? (rv.Sku.Length > 0 ? product.Variants.FirstOrDefault(v => string.Equals(v.Sku, rv.Sku, StringComparison.OrdinalIgnoreCase)) : null)
                        ?? product.Variants.FirstOrDefault(v => string.Equals(v.Title, rv.Title, StringComparison.OrdinalIgnoreCase));
            if (local is null && !printify && product.Variants.Count == 0 && remote.Variants.Count == 1) continue; // simple product
            if (local is null) continue;
            if (printify) local.PrintifyVariantId = long.TryParse(numeric, out var id) ? id : null;
            else local.ShopifyVariantId = rv.Id;
        }
    }

    /// <summary>The remote values in ArtGander's terms (variant keys), used as the sync base.</summary>
    public static SyncSnapshot Snapshot(RemoteProduct remote, Product product)
    {
        var snap = new SyncSnapshot
        {
            Title = remote.Title,
            Description = remote.Description,
            Tags = [.. remote.Tags],
            Status = remote.Status,
        };
        foreach (var rv in remote.Variants)
        {
            var key = VariantKeyFor(product, remote, rv);
            if (key is null) continue;
            snap.Variants.Add(new VariantSnapshot { Key = key, Price = rv.Price, Enabled = rv.Enabled, Stock = remote.Channel == "shopify" ? rv.Stock : null });
        }
        return snap;
    }

    /// <summary>The ArtGander variant key for a platform variant; "" for the single variant of a simple product.</summary>
    public static string? VariantKeyFor(Product product, RemoteProduct remote, RemoteVariant rv)
    {
        var numeric = Ids.Numeric(rv.Id);
        var local = remote.Channel == "printify"
            ? product.Variants.FirstOrDefault(v => v.PrintifyVariantId?.ToString() == numeric)
            : product.Variants.FirstOrDefault(v => v.ShopifyVariantId == rv.Id);
        if (local is not null) return local.Key;
        return product.Variants.Count == 0 && remote.Variants.Count == 1 ? "" : null;
    }
}

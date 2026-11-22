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
    /// <summary>Why the Printify and Shopify products (or an existing ArtGander product) were combined, for the preview.</summary>
    public string MatchReason { get; init; } = "";

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
    /// <summary>
    /// Pairs everything found in the shops, so the same product never becomes two:
    /// <list type="number">
    /// <item>A Printify product and the Shopify listing it was published as (Printify's <c>external.id</c>).</item>
    /// <item>Otherwise a Printify product and a Shopify product with the same SKUs, or else the same title.</item>
    /// <item>Each pair (or single product) and the ArtGander product already linked to it; otherwise an ArtGander product
    /// that isn't linked to that shop yet and has the same SKUs or name (e.g. a product entered by hand before).</item>
    /// </list>
    /// </summary>
    public static List<ProductImportCandidate> Plan(IReadOnlyList<RemoteProduct> printify, IReadOnlyList<RemoteProduct> shopify,
        IReadOnlyList<Product> existing, IEnumerable<ArtPieceDocument> docs)
    {
        var pieces = docs.ToList();
        var result = new List<ProductImportCandidate>();
        var pairedShopify = new HashSet<string>();
        var claimed = new HashSet<Guid>(); // ArtGander products already matched by an earlier candidate

        // 1. Pairs that Printify itself knows about.
        var pairs = new List<(RemoteProduct P, RemoteProduct? S, string Reason)>();
        var unpaired = new List<RemoteProduct>();
        foreach (var p in printify)
        {
            var s = p.ExternalId is { } ext ? shopify.FirstOrDefault(x => Ids.SameShopifyId(x.Id, ext)) : null;
            if (s is null) { unpaired.Add(p); continue; }
            pairedShopify.Add(s.Id);
            pairs.Add((p, s, "published from Printify"));
        }
        // 2. Same SKUs, then the same title, among what's left.
        foreach (var p in unpaired)
        {
            var free = shopify.Where(s => !pairedShopify.Contains(s.Id)).ToList();
            var s = free.FirstOrDefault(x => SameSkus(p, x)) is { } bySku ? (bySku, "same SKUs")
                : free.Where(x => SameTitle(p.Title, x.Title)).ToList() is [var byTitle] ? (byTitle, "same title")
                : ((RemoteProduct?)null, "");
            if (s.Item1 is not null) pairedShopify.Add(s.Item1.Id);
            pairs.Add((p, s.Item1, s.Item2));
        }

        foreach (var (p, s, reason) in pairs)
        {
            var (match, why) = FindExisting(existing, claimed, p, s);
            result.Add(new ProductImportCandidate
            {
                Printify = p, Shopify = s, Existing = match, SuggestedPieces = Suggest(p, match, pieces),
                MatchReason = string.Join(" · ", new[] { s is null ? "" : "Printify + Shopify: " + reason, why }.Where(x => x.Length > 0)),
            });
        }
        foreach (var s in shopify.Where(s => !pairedShopify.Contains(s.Id)))
        {
            var (match, why) = FindExisting(existing, claimed, null, s);
            result.Add(new ProductImportCandidate { Shopify = s, Existing = match, SuggestedPieces = Suggest(s, match, pieces), MatchReason = why });
        }
        return result.OrderBy(c => c.IsUpToDate).ThenBy(c => c.Title, Naming.NameRules.NaturalComparer).ToList();
    }

    private static (Product? Product, string Reason) FindExisting(IReadOnlyList<Product> existing, HashSet<Guid> claimed, RemoteProduct? p, RemoteProduct? s)
    {
        Product? Claim(Product? x) { if (x is not null) claimed.Add(x.Id); return x; }

        // Already linked.
        if (Claim(existing.FirstOrDefault(e => p is not null && e.Channels.Printify?.ProductId == p.Id)
                  ?? existing.FirstOrDefault(e => s is not null && Ids.SameShopifyId(e.Channels.Shopify?.ProductId, s.Id))) is { } linked)
            return (linked, "");

        // Not linked yet: a product entered in ArtGander by hand (or from an older version) that's clearly the same one.
        var open = existing.Where(e => !claimed.Contains(e.Id)
                                       && (p is null || !e.IsOnPrintify)
                                       && (s is null || !e.IsOnShopify)).ToList();
        var remote = p ?? s!;
        if (open.FirstOrDefault(e => SameSkus(e, remote) || (s is not null && SameSkus(e, s))) is { } bySku)
            return (Claim(bySku), $"joins your product \"{bySku.Name}\" (same SKUs)");
        if (open.Where(e => SameTitle(e.Name, remote.Title) || (s is not null && SameTitle(e.Name, s.Title))).ToList() is [var byName])
            return (Claim(byName), $"joins your product \"{byName.Name}\" (same name)");
        return (null, "");
    }

    private static HashSet<string> Skus(IEnumerable<string> skus) =>
        skus.Select(s => s.Trim()).Where(s => s.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool SameSkus(RemoteProduct a, RemoteProduct b)
    {
        var x = Skus(a.EnabledVariants.Select(v => v.Sku));
        var y = Skus(b.EnabledVariants.Select(v => v.Sku));
        return x.Count > 0 && y.Count > 0 && x.Overlaps(y);
    }

    private static bool SameSkus(Product a, RemoteProduct b)
    {
        var x = Skus(a.Variants.Select(v => v.Sku));
        var y = Skus(b.EnabledVariants.Select(v => v.Sku));
        return x.Count > 0 && y.Count > 0 && x.Overlaps(y);
    }

    /// <summary>Titles match when their words are the same, ignoring case, punctuation and word order.</summary>
    private static bool SameTitle(string a, string b)
    {
        var x = Words(a);
        return x.Count > 0 && x.SetEquals(Words(b));
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

        var joined = c.Existing is not null;
        if (c.Printify is { } p)
        {
            MapVariants(product, p);
            if (joined && !product.IsOnPrintify) (product.Channels.Printify ??= new PrintifyLink()).NeedsReview = true;
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
            if (joined && !product.IsOnShopify) (product.Channels.Shopify ??= new ShopifyLink()).NeedsReview = true;
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
            Pictures = remote.Channel == "shopify" ? ShopPictures.RemoteSignature(remote) : null,
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

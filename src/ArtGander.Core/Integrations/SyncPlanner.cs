using System.Globalization;
using ArtGander.Core.Model;

namespace ArtGander.Core.Integrations;

public enum SyncChangeKind
{
    /// <summary>Changed in ArtGander only: send it to the shop.</summary>
    Push,
    /// <summary>Changed in the shop only: take it into ArtGander, or overwrite it with ArtGander's value.</summary>
    OutsideEdit,
    /// <summary>Changed on both sides, differently: you decide.</summary>
    Conflict,
    /// <summary>Changed in the shop and taken over automatically (stock levels change with every sale).</summary>
    AutoTake,
}

public enum SyncResolution { Skip, Push, TakeRemote }

/// <summary>One field of one product that differs between ArtGander and a shop.</summary>
public sealed record SyncFieldChange(
    string Channel, string Field, string? VariantKey, string Label,
    string? Base, string? Local, string? Remote, SyncChangeKind Kind)
{
    public string Key => $"{Channel}|{Field}|{VariantKey}";

    /// <summary>What happens when you don't choose: pushes are sent, outside edits are taken over, conflicts wait for you.</summary>
    public SyncResolution DefaultResolution => Kind switch
    {
        SyncChangeKind.Push => SyncResolution.Push,
        SyncChangeKind.OutsideEdit or SyncChangeKind.AutoTake => SyncResolution.TakeRemote,
        _ => SyncResolution.Skip,
    };
}

/// <summary>The sync preview for one product.</summary>
public sealed class ProductSyncPlan
{
    public required Product Product { get; init; }
    public RemoteProduct? Printify { get; init; }
    public RemoteProduct? Shopify { get; init; }
    public List<SyncFieldChange> Changes { get; } = [];
    /// <summary>Why this product can't be synced right now (e.g. Printify is still publishing it), or null.</summary>
    public string? Problem { get; set; }
    public bool HasWork => Changes.Count > 0 && Problem is null;
}

/// <summary>
/// Compares each linked product field by field: the value both sides agreed on at the last sync (the base),
/// ArtGander's value, and the shop's value now. Read-only: it only works out what a sync would do.
/// <para>
/// Each field has one owner. For Printify products, texts, tags and prices belong to Printify (which publishes them
/// to Shopify); for products you make yourselves they belong to Shopify. Shopify edits to Printify-owned fields are
/// still detected, because the next publish from Printify would overwrite them.
/// </para>
/// </summary>
public static class SyncPlanner
{
    public static TimeSpan PublishGrace { get; set; } = TimeSpan.FromMinutes(60);

    public static List<ProductSyncPlan> Plan(IEnumerable<Product> products, IReadOnlyList<RemoteProduct> printify, IReadOnlyList<RemoteProduct> shopify)
    {
        var plans = new List<ProductSyncPlan>();
        foreach (var product in products)
        {
            if (!product.IsOnPrintify && !product.IsOnShopify) continue;
            var p = product.Channels.Printify is { ProductId.Length: > 0 } pl ? printify.FirstOrDefault(r => r.Id == pl.ProductId) : null;
            var s = product.Channels.Shopify is { ProductId.Length: > 0 } sl ? shopify.FirstOrDefault(r => Ids.SameShopifyId(r.Id, sl.ProductId)) : null;
            var plan = new ProductSyncPlan { Product = product, Printify = p, Shopify = s };
            if (product.IsOnPrintify && p is null) plan.Problem = "Not found in Printify anymore (deleted there?).";
            else if (product.IsOnShopify && s is null) plan.Problem = "Not found in Shopify anymore (deleted there?).";
            else if (p?.IsLocked == true) plan.Problem = "Printify is still publishing this product. Try again in a few minutes.";
            else Compare(plan);
            if (plan.Changes.Count > 0 || plan.Problem is not null) plans.Add(plan);
        }
        return plans;
    }

    private static void Compare(ProductSyncPlan plan)
    {
        var product = plan.Product;
        var printifyOwned = plan.Printify is not null;
        var pendingPublish = product.Channels.Printify is { PublishPending: true } link
                             && link.LastSyncedAt is { } at && DateTimeOffset.Now - at < PublishGrace;

        if (plan.Printify is { } p)
            CompareChannel(plan, p, product.Channels.Printify!.Base, owner: true, includeStatus: false, includeStock: false, includeEnabled: true);

        if (plan.Shopify is { } s)
        {
            var baseSnap = product.Channels.Shopify!.Base;
            if (printifyOwned)
            {
                // Texts and prices go through Printify. Only report Shopify edits to them (a publish would overwrite those),
                // and not while a publish is still on its way to Shopify.
                if (!pendingPublish)
                    CompareChannel(plan, s, baseSnap, owner: false, includeStatus: false, includeStock: false, includeEnabled: false);
                CompareFields(plan, s, baseSnap, owner: true, onlyStatus: true);
            }
            else
            {
                CompareChannel(plan, s, baseSnap, owner: true, includeStatus: true, includeStock: true, includeEnabled: false);
            }
        }
    }

    private static void CompareFields(ProductSyncPlan plan, RemoteProduct remote, SyncSnapshot? snap, bool owner, bool onlyStatus)
    {
        if (onlyStatus) Add(plan, remote.Channel, "status", null, "Status", snap?.Status, StatusOf(plan.Product), remote.Status, owner);
    }

    private static void CompareChannel(ProductSyncPlan plan, RemoteProduct remote, SyncSnapshot? snap, bool owner, bool includeStatus, bool includeStock, bool includeEnabled)
    {
        var product = plan.Product;
        var c = remote.Channel;
        Add(plan, c, "title", null, "Title", snap?.Title, product.Name, remote.Title, owner);
        Add(plan, c, "description", null, "Description", snap?.Description, product.Description, remote.Description, owner);
        Add(plan, c, "tags", null, "Tags", Tags(snap?.Tags), Tags(product.Tags), Tags(remote.Tags), owner);
        if (includeStatus) Add(plan, c, "status", null, "Status", snap?.Status, StatusOf(product), remote.Status, owner);

        foreach (var rv in remote.Variants)
        {
            var key = ProductImporter.VariantKeyFor(product, remote, rv);
            if (key is null) continue; // a variant ArtGander doesn't track (e.g. disabled in Printify)
            var local = product.FindVariant(key);
            var baseVariant = snap?.Variants.FirstOrDefault(v => v.Key == key);
            var label = local is null || string.IsNullOrEmpty(local.Title) ? "" : local.Title + " ";
            var localPrice = local is null ? product.Price : product.PriceOf(local);
            if (localPrice is not null)
                Add(plan, c, "price", key, label + "price", Money(baseVariant?.Price), Money(localPrice), Money(rv.Price), owner);
            if (includeEnabled && local is not null)
                Add(plan, c, "enabled", key, label + "on sale", Bool(baseVariant?.Enabled), Bool(local.Enabled), Bool(rv.Enabled), owner);
            if (includeStock)
            {
                var localStock = local?.Stock ?? (product.Variants.Count <= 1 ? product.Stock : null);
                if (localStock is not null || rv.Stock is not null)
                    Add(plan, c, "stock", key, label + "stock", Int(baseVariant?.Stock), Int(localStock), Int(rv.Stock), owner, isStock: true);
            }
        }
    }

    private static void Add(ProductSyncPlan plan, string channel, string field, string? variant, string label,
        string? baseValue, string? local, string? remote, bool owner, bool isStock = false)
    {
        if (Same(local, remote)) return;
        baseValue ??= remote; // never synced: treat the shop's current value as the starting point
        var localChanged = !Same(local, baseValue);
        var remoteChanged = !Same(remote, baseValue);
        SyncChangeKind? kind = (localChanged, remoteChanged) switch
        {
            (true, false) => owner ? SyncChangeKind.Push : null,
            (false, true) => isStock ? SyncChangeKind.AutoTake : SyncChangeKind.OutsideEdit,
            (true, true) => isStock ? SyncChangeKind.AutoTake : SyncChangeKind.Conflict,
            _ => null,
        };
        if (kind is { } k) plan.Changes.Add(new SyncFieldChange(channel, field, variant, label, baseValue, local, remote, k));
    }

    public static string StatusOf(Product p) => p.Status switch
    {
        ProductStatus.Draft => "draft",
        ProductStatus.Archived => "archived",
        _ => "active",
    };

    public static string? Money(decimal? d) => d?.ToString("0.00", CultureInfo.InvariantCulture);
    private static string? Bool(bool? b) => b is null ? null : b.Value ? "yes" : "no";
    private static string? Int(int? i) => i?.ToString(CultureInfo.InvariantCulture);

    /// <summary>Tags compare as a set: order and letter case don't matter.</summary>
    public static string? Tags(IEnumerable<string>? tags) => tags is null ? null
        : string.Join(", ", tags.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase));

    private static bool Same(string? a, string? b) => string.Equals(a ?? "", b ?? "", StringComparison.Ordinal);
}

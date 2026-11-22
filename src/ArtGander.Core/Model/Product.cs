using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ArtGander.Core.Model;

/// <summary>
/// Something that gets sold: a Printify product, a self-made print, the original, a digital download…
/// Stored as its own file in <c>{ArtRoot}/.artgander/products/</c>. A product links to any number of art pieces
/// (and to specific files of those pieces), and an art piece can be in any number of products.
/// <para>
/// ArtGander is leading for the texts, tags, prices and variants. <see cref="Channels"/> holds the links to Printify
/// and Shopify plus the values of the last sync, which is how outside edits are detected instead of overwritten.
/// </para>
/// </summary>
public sealed partial class Product : ModelBase, IStoredRecord
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The product title, as shown in the shop.</summary>
    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string Description { get; set; } = "";
    public ObservableCollection<string> Tags { get; set; } = [];

    [ObservableProperty] public partial ProductType Type { get; set; } = ProductType.SelfPrint;
    [ObservableProperty] public partial ProductStatus Status { get; set; } = ProductStatus.Active;

    /// <summary>Who makes it, e.g. "Printify · Monster Digital", or "Ourselves".</summary>
    [ObservableProperty] public partial string Provider { get; set; } = "";
    /// <summary>A short description of the format, e.g. "A4 matte poster".</summary>
    [ObservableProperty] public partial string Variant { get; set; } = "";

    /// <summary>The art pieces (or projects) this product shows.</summary>
    public ObservableCollection<Guid> Pieces { get; set; } = [];
    /// <summary>The specific files used, e.g. the transparent version. Independent of the files' version labels.</summary>
    public ObservableCollection<ProductFile> Files { get; set; } = [];

    /// <summary>
    /// The product's own pictures (photos of the made product, mockups…), stored in the product's folder.
    /// In shop order: the first one is the main picture.
    /// </summary>
    public ObservableCollection<ProductImage> Images { get; set; } = [];

    /// <summary>Which pictures the shop shows: Printify's mockups (the default for Printify products) or your own pictures.</summary>
    [ObservableProperty] public partial PictureMode Pictures { get; set; } = PictureMode.PrintifyMockups;

    /// <summary>Self-made products always use their own pictures; Printify products use the chosen mode.</summary>
    [JsonIgnore] public PictureMode EffectivePictures => Type == ProductType.PrintOnDemand ? Pictures : PictureMode.Own;

    /// <summary>The default price. Variants without their own price use it.</summary>
    [ObservableProperty] public partial decimal? Price { get; set; }
    /// <summary>The default production cost. Variants without their own cost use it.</summary>
    [ObservableProperty] public partial decimal? ProductionCost { get; set; }
    [ObservableProperty] public partial string Currency { get; set; } = "EUR";
    public ObservableCollection<CostLine> CostBreakdown { get; set; } = [];
    [ObservableProperty] public partial int? EditionSize { get; set; }
    /// <summary>Stock for products you make yourself. For Shopify products this mirrors the Shopify inventory.</summary>
    [ObservableProperty] public partial int? Stock { get; set; }

    /// <summary>Sizes, colors and so on. Empty for a simple product.</summary>
    public ObservableCollection<ProductVariant> Variants { get; set; } = [];

    /// <summary>Other places where it's offered by hand (a fair, Etsy…). Printify and Shopify live in <see cref="Channels"/>.</summary>
    public ObservableCollection<Listing> Listings { get; set; } = [];

    /// <summary>Bundles only, e.g. "Set of 3 prints": the pieces (and optionally their products) it contains.</summary>
    public ObservableCollection<BundleItem> Includes { get; set; } = [];

    /// <summary>Related products, e.g. the same design as a poster and as a mug. Reserved for future features.</summary>
    public ObservableCollection<ProductRelation> Relations { get; set; } = [];

    public ChannelLinks Channels { get; set; } = new();

    [ObservableProperty] public partial string Notes { get; set; } = "";

    public DateTimeOffset? CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }

    /// <summary>Who ships it: Printify-made products are fulfilled by Printify, everything else by you.</summary>
    [JsonIgnore] public Fulfillment Fulfillment => Type == ProductType.PrintOnDemand ? Fulfillment.Printify : Type == ProductType.Digital ? Fulfillment.Digital : Fulfillment.Self;

    /// <summary>The price minus the production cost, or null when either is unknown.</summary>
    [JsonIgnore] public decimal? Margin => Price is { } p && ProductionCost is { } c ? p - c : null;

    [JsonIgnore] public bool IsOnPrintify => Channels.Printify is { ProductId.Length: > 0 };
    [JsonIgnore] public bool IsOnShopify => Channels.Shopify is { ProductId.Length: > 0 };

    [JsonIgnore] public string? StoredFileName { get; set; }
    [JsonIgnore] public string? StoredHash { get; set; }

    public decimal? PriceOf(ProductVariant variant) => variant.Price ?? Price;
    public decimal? CostOf(ProductVariant? variant) => variant?.Cost ?? ProductionCost;

    public ProductVariant? FindVariant(string? key) => key is null ? null : Variants.FirstOrDefault(v => v.Key == key);

    partial void OnPriceChanged(decimal? value) => OnPropertyChanged(nameof(Margin));
    partial void OnProductionCostChanged(decimal? value) => OnPropertyChanged(nameof(Margin));
    partial void OnTypeChanged(ProductType value) => OnPropertyChanged(nameof(Fulfillment));
}

/// <summary>A file of an art piece that a product uses.</summary>
public sealed partial class ProductFile : ModelBase
{
    [ObservableProperty] public partial Guid PieceId { get; set; }
    [ObservableProperty] public partial string FileName { get; set; } = "";
}

[JsonConverter(typeof(CamelCaseEnumConverter<PictureMode>))]
public enum PictureMode { PrintifyMockups, Own }

[JsonConverter(typeof(CamelCaseEnumConverter<PictureSource>))]
public enum PictureSource { Own, Printify, Shopify }

/// <summary>The kinds of product pictures.</summary>
public static class ProductImageKind
{
    public const string Photo = "photo";
    public const string Mockup = "mockup";
    public const string Lifestyle = "lifestyle";
    public const string Detail = "detail";
    public const string SizeChart = "size chart";
    public const string Other = "other";
    public static IReadOnlyList<string> All { get; } = [Photo, Mockup, Lifestyle, Detail, SizeChart, Other];
}

/// <summary>A picture of a product, in the product's folder: <c>{Product_Name}-{label}.{ext}</c>.</summary>
public sealed partial class ProductImage : ModelBase
{
    [ObservableProperty] public partial string FileName { get; set; } = "";
    /// <summary>The label in the file name (Name-<b>label</b>.ext).</summary>
    [ObservableProperty] public partial string Label { get; set; } = "";
    [ObservableProperty] public partial string Kind { get; set; } = ProductImageKind.Photo;
    /// <summary>The art piece or project this picture shows, or null.</summary>
    [ObservableProperty] public partial Guid? PieceId { get; set; }
    /// <summary>The description for people who can't see the picture (also used by the shop).</summary>
    [ObservableProperty] public partial string Alt { get; set; } = "";
    /// <summary>Where it came from: added here, or downloaded from Printify or Shopify.</summary>
    [ObservableProperty] public partial PictureSource Source { get; set; } = PictureSource.Own;
    /// <summary>The link to the picture in the Shopify product, once uploaded or imported.</summary>
    public ShopifyMediaLink? Shopify { get; set; }
    /// <summary>For Printify mockups: the address it was downloaded from, to recognize it on the next import.</summary>
    public string? PrintifySrc { get; set; }
    public DateTimeOffset? AddedAt { get; set; }
}

public sealed class ShopifyMediaLink
{
    public string MediaId { get; set; } = "";
    /// <summary>The file's content hash when it was uploaded or downloaded, to notice a changed picture.</summary>
    public string Hash { get; set; } = "";
    /// <summary>Uploaded by ArtGander (only those may ever be removed from Shopify by ArtGander).</summary>
    public bool UploadedByArtGander { get; set; }
    public string Alt { get; set; } = "";
}

/// <summary>One variant: a size, color or format with its own price.</summary>
public sealed partial class ProductVariant : ModelBase
{
    /// <summary>A stable key within the product. For imported variants it's the platform's variant id.</summary>
    public string Key { get; set; } = Guid.NewGuid().ToString("N")[..8];
    [ObservableProperty] public partial string Title { get; set; } = "";
    [ObservableProperty] public partial string Sku { get; set; } = "";
    /// <summary>Leave empty to use the product's default price.</summary>
    [ObservableProperty] public partial decimal? Price { get; set; }
    /// <summary>Leave empty to use the product's default production cost.</summary>
    [ObservableProperty] public partial decimal? Cost { get; set; }
    [ObservableProperty] public partial bool Enabled { get; set; } = true;
    [ObservableProperty] public partial int? Stock { get; set; }
    public long? PrintifyVariantId { get; set; }
    public string? ShopifyVariantId { get; set; }
}

/// <summary>Where a product is offered by hand: a fair, Etsy, a gallery…</summary>
public sealed partial class Listing : ModelBase
{
    [ObservableProperty] public partial string Channel { get; set; } = "other";
    [ObservableProperty] public partial string Url { get; set; } = "";
    [ObservableProperty] public partial string ExternalId { get; set; } = "";
    [ObservableProperty] public partial decimal? Price { get; set; }
    [ObservableProperty] public partial ListingStatus Status { get; set; } = ListingStatus.Active;
}

/// <summary>One art piece inside a bundle product.</summary>
public sealed partial class BundleItem : ModelBase
{
    [ObservableProperty] public partial Guid PieceId { get; set; }
    /// <summary>Optional: the product of that piece that's included (its production cost is used for "Use pieces' cost").</summary>
    [ObservableProperty] public partial Guid? ProductId { get; set; }
    [ObservableProperty] public partial int Quantity { get; set; } = 1;
}

/// <summary>A link to another product, e.g. "same design".</summary>
public sealed partial class ProductRelation : ModelBase
{
    [ObservableProperty] public partial Guid ProductId { get; set; }
    [ObservableProperty] public partial string Kind { get; set; } = "sameDesign";
}

/// <summary>The links to the online platforms. Each one also remembers the values of the last sync.</summary>
public sealed class ChannelLinks : ModelBase
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public PrintifyLink? Printify { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public ShopifyLink? Shopify { get; set; }
}

public abstract class ChannelLink : ModelBase
{
    public string ProductId { get; set; } = "";
    /// <summary>The platform's last-modified stamp at the last sync. A different value means it was edited outside ArtGander.</summary>
    public string? RemoteUpdatedAt { get; set; }
    public DateTimeOffset? LastSyncedAt { get; set; }
    /// <summary>The values both sides agreed on at the last sync.</summary>
    public SyncSnapshot? Base { get; set; }
    /// <summary>
    /// Set when an existing ArtGander product was connected to a shop product: they never agreed on anything yet, so every
    /// difference is shown as a choice in the next sync instead of being sent automatically.
    /// </summary>
    public bool NeedsReview { get; set; }
}

public sealed class PrintifyLink : ChannelLink
{
    public long ShopId { get; set; }
    public long? BlueprintId { get; set; }
    public long? PrintProviderId { get; set; }
    /// <summary>Set while a create/publish is in progress, so a retry never creates a second product.</summary>
    public bool PublishPending { get; set; }
}

public sealed class ShopifyLink : ChannelLink
{
    public string Handle { get; set; } = "";
    public string Url { get; set; } = "";
    /// <summary>The Shopify location used for stock of self-made products.</summary>
    public string? InventoryLocationId { get; set; }
}

/// <summary>The synced values of a product at a point in time. Compared field by field: base vs. ArtGander vs. the platform.</summary>
public sealed class SyncSnapshot : ModelBase
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    public List<string>? Tags { get; set; }
    public string? Status { get; set; }
    public List<VariantSnapshot> Variants { get; set; } = [];
}

public sealed class VariantSnapshot : ModelBase
{
    public string Key { get; set; } = "";
    public decimal? Price { get; set; }
    public bool Enabled { get; set; } = true;
    public int? Stock { get; set; }
}

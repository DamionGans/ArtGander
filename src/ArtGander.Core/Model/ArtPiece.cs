using System.Collections.ObjectModel;
using ArtGander.Core.Naming;
using ArtGander.Core.Schema;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ArtGander.Core.Model;

/// <summary>The root object of an art piece's <c>artgander.json</c> file.</summary>
public sealed partial class ArtPiece : ModelBase
{
    /// <summary>The file's structure version. Upgraded automatically when the file is loaded.</summary>
    public int SchemaVersion { get; set; } = CurrentSchema.Version;

    /// <summary>A stable identity that survives folder renames.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    [ObservableProperty] public partial string Title { get; set; } = "";
    [ObservableProperty] public partial string Description { get; set; } = "";
    public ObservableCollection<string> Tags { get; set; } = [];
    public ObservableCollection<string> Collections { get; set; } = [];

    [ObservableProperty] public partial DateOnly? CreatedOn { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
    public string? UpdatedWith { get; set; }

    public Artwork Artwork { get; set; } = new();
    public Copyright Copyright { get; set; } = new();
    public ObservableCollection<FileEntry> Files { get; set; } = [];
    public ObservableCollection<Product> Products { get; set; } = [];
    public ObservableCollection<Sale> Sales { get; set; } = [];

    [ObservableProperty] public partial string Notes { get; set; } = "";

    public FileEntry? FindFile(string fileName) =>
        Files.FirstOrDefault(f => NameRules.NamesEqual(f.FileName, fileName));

    public Product? FindProduct(Guid? id) => id is null ? null : Products.FirstOrDefault(p => p.Id == id);
}

/// <summary>The physical (or original digital) work itself.</summary>
public sealed partial class Artwork : ModelBase
{
    [ObservableProperty] public partial string Medium { get; set; } = "";
    [ObservableProperty] public partial string Dimensions { get; set; } = "";
    [ObservableProperty] public partial bool IsDigitalOnly { get; set; }
    [ObservableProperty] public partial OriginalStatus OriginalStatus { get; set; } = OriginalStatus.Available;
    [ObservableProperty] public partial decimal? OriginalPrice { get; set; }
    [ObservableProperty] public partial string SoldTo { get; set; } = "";
    [ObservableProperty] public partial DateOnly? SoldOn { get; set; }
}

public sealed partial class Copyright : ModelBase
{
    [ObservableProperty] public partial string Owner { get; set; } = "";
    [ObservableProperty] public partial int? Year { get; set; }
    [ObservableProperty] public partial CopyrightStatus Status { get; set; } = CopyrightStatus.Unregistered;
    [ObservableProperty] public partial string RegistrationNumber { get; set; } = "";
    [ObservableProperty] public partial DateOnly? RegisteredOn { get; set; }
    [ObservableProperty] public partial string Office { get; set; } = "";
    [ObservableProperty] public partial string Notes { get; set; } = "";
}

/// <summary>A file in the piece folder. <see cref="Version"/> is the label in the file name (Name-<b>version</b>.ext).</summary>
public sealed partial class FileEntry : ModelBase
{
    [ObservableProperty] public partial string FileName { get; set; } = "";
    [ObservableProperty] public partial string Version { get; set; } = "";
    [ObservableProperty] public partial FileKind Kind { get; set; } = FileKind.Other;
    [ObservableProperty] public partial string Notes { get; set; } = "";
    public DateTimeOffset? AddedAt { get; set; }
}

/// <summary>
/// Something that gets sold: the original, a self-printed copy, a Printify product, and so on.
/// It links to any number of files by file name, independently of those files' version labels.
/// </summary>
public sealed partial class Product : ModelBase
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial ProductType Type { get; set; } = ProductType.SelfPrint;
    [ObservableProperty] public partial string Provider { get; set; } = "";
    [ObservableProperty] public partial string Variant { get; set; } = "";
    public ObservableCollection<string> Files { get; set; } = [];
    [ObservableProperty] public partial decimal? ProductionCost { get; set; }
    [ObservableProperty] public partial decimal? Price { get; set; }
    [ObservableProperty] public partial string Currency { get; set; } = "EUR";
    public ObservableCollection<CostLine> CostBreakdown { get; set; } = [];
    [ObservableProperty] public partial int? EditionSize { get; set; }
    [ObservableProperty] public partial int? Stock { get; set; }
    public ObservableCollection<Listing> Listings { get; set; } = [];
    [ObservableProperty] public partial string Notes { get; set; } = "";

    /// <summary>The price minus the production cost, or null when either is unknown.</summary>
    public decimal? Margin => Price is { } p && ProductionCost is { } c ? p - c : null;

    partial void OnPriceChanged(decimal? value) => OnPropertyChanged(nameof(Margin));
    partial void OnProductionCostChanged(decimal? value) => OnPropertyChanged(nameof(Margin));
}

/// <summary>Where a product is offered: a Shopify listing, a Printify product, an in-person fair, and so on.</summary>
public sealed partial class Listing : ModelBase
{
    [ObservableProperty] public partial string Channel { get; set; } = "shopify";
    [ObservableProperty] public partial string Url { get; set; } = "";
    [ObservableProperty] public partial string ExternalId { get; set; } = "";
    [ObservableProperty] public partial decimal? Price { get; set; }
    [ObservableProperty] public partial ListingStatus Status { get; set; } = ListingStatus.Active;
}

/// <summary>One order line. When an order spans several pieces, each line carries the same <see cref="OrderRef"/>.</summary>
public sealed partial class Sale : ModelBase
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [ObservableProperty] public partial DateOnly Date { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    [ObservableProperty] public partial string OrderRef { get; set; } = "";
    [ObservableProperty] public partial string Channel { get; set; } = "shopify";
    [ObservableProperty] public partial Guid? ProductId { get; set; }
    [ObservableProperty] public partial int Quantity { get; set; } = 1;
    [ObservableProperty] public partial decimal UnitPrice { get; set; }
    [ObservableProperty] public partial decimal Fees { get; set; }
    /// <summary>The cost per unit at the time of the sale. Left empty, the product's production cost is used.</summary>
    [ObservableProperty] public partial decimal? UnitCost { get; set; }
    [ObservableProperty] public partial string Currency { get; set; } = "EUR";
    [ObservableProperty] public partial string Customer { get; set; } = "";
    [ObservableProperty] public partial string Notes { get; set; } = "";
}

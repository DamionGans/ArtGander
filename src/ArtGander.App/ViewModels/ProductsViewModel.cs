using System.Collections.ObjectModel;
using ArtGander.App.Services;
using ArtGander.Core.Model;
using ArtGander.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ArtGander.App.ViewModels;

/// <summary>A product in the Products list.</summary>
public sealed partial class ProductTile(Product product, string piecesText, string? previewPath) : ObservableObject
{
    public Product Product { get; } = product;
    public string Title => string.IsNullOrWhiteSpace(Product.Name) ? "(unnamed product)" : Product.Name;
    public string PiecesText { get; } = piecesText;
    public string PriceText => Product.Price is { } p ? $"{p:N2} {Product.Currency}" : "no price";
    public string TypeText => Converters.HumanizeConverter.Humanize(Product.Type.ToString())
        + (Product.Variants.Count > 0 ? $" · {Product.Variants.Count} variants" : "")
        + (Product.Status != ProductStatus.Active ? " · " + Converters.HumanizeConverter.Humanize(Product.Status.ToString()) : "");
    public bool OnPrintify => Product.IsOnPrintify;
    public bool OnShopify => Product.IsOnShopify;
    public string? PreviewPath { get; } = previewPath;

    [ObservableProperty] public partial ImageSource? Thumbnail { get; set; }

    public async Task LoadThumbnailAsync(ThumbnailService thumbs)
    {
        var path = await thumbs.GetAsync(PreviewPath);
        if (path is not null) MainThread.BeginInvokeOnMainThread(() => Thumbnail = ImageSource.FromFile(path));
    }
}

/// <summary>Every product in the library, with filters.</summary>
public sealed partial class ProductsViewModel : ObservableObject
{
    public const string All = "All";
    public static IReadOnlyList<string> TypeFilters { get; } = [All, .. Enum.GetValues<ProductType>().Select(t => Converters.HumanizeConverter.Humanize(t.ToString()))];
    public static IReadOnlyList<string> StatusFilters { get; } = [All, .. Enum.GetValues<ProductStatus>().Select(t => Converters.HumanizeConverter.Humanize(t.ToString()))];
    public static IReadOnlyList<string> ChannelFilters { get; } = [All, "On Printify", "On Shopify", "Not online"];

    private readonly AppState _app;
    private readonly ThumbnailService _thumbs;

    public ProductsViewModel(AppState app, ThumbnailService thumbs)
    {
        _app = app;
        _thumbs = thumbs;
        _app.LibraryChanged += (_, _) => Rebuild();
    }

    public ObservableCollection<ProductTile> Products { get; } = [];

    [ObservableProperty] public partial string SearchText { get; set; } = "";
    [ObservableProperty] public partial string TypeFilter { get; set; } = All;
    [ObservableProperty] public partial string StatusFilter { get; set; } = All;
    [ObservableProperty] public partial string ChannelFilter { get; set; } = All;
    [ObservableProperty] public partial string SummaryText { get; set; } = "";

    partial void OnSearchTextChanged(string value) => Rebuild();
    partial void OnTypeFilterChanged(string value) => Rebuild();
    partial void OnStatusFilterChanged(string value) => Rebuild();
    partial void OnChannelFilterChanged(string value) => Rebuild();

    public void Rebuild()
    {
        var docs = _app.Documents.ToDictionary(d => d.Piece.Id);
        var q = SearchText.Trim();
        bool Has(string text) => text.Contains(q, StringComparison.CurrentCultureIgnoreCase);

        Products.Clear();
        foreach (var p in _app.Catalog.All)
        {
            var pieces = p.Pieces.Select(id => docs.TryGetValue(id, out var d) ? d : null).OfType<Core.Storage.ArtPieceDocument>().ToList();
            var piecesText = string.Join(", ", pieces.Select(d => d.DisplayTitle));
            if (TypeFilter != All && Converters.HumanizeConverter.Humanize(p.Type.ToString()) != TypeFilter) continue;
            if (StatusFilter != All && Converters.HumanizeConverter.Humanize(p.Status.ToString()) != StatusFilter) continue;
            if (ChannelFilter == "On Printify" && !p.IsOnPrintify) continue;
            if (ChannelFilter == "On Shopify" && !p.IsOnShopify) continue;
            if (ChannelFilter == "Not online" && (p.IsOnPrintify || p.IsOnShopify)) continue;
            if (q.Length > 0 && !Has(p.Name) && !Has(piecesText) && !p.Tags.Any(Has) && !Has(p.Provider) && !p.Variants.Any(v => Has(v.Sku) || Has(v.Title))) continue;

            var tile = new ProductTile(p, piecesText.Length > 0 ? piecesText : "No art pieces linked", PreviewFor(p, docs));
            Products.Add(tile);
            _ = tile.LoadThumbnailAsync(_thumbs);
        }
        var total = _app.Catalog.All.Count;
        SummaryText = total == 0
            ? "No products yet. Create one here or from an art piece's Products tab."
            : Products.Count == total ? $"{total} product{(total == 1 ? "" : "s")}" : $"{Products.Count} of {total} products";
    }

    /// <summary>The first linked file, or else the first linked piece's preview.</summary>
    private string? PreviewFor(Product p, Dictionary<Guid, Core.Storage.ArtPieceDocument> docs)
    {
        foreach (var f in p.Files)
        {
            if (docs.TryGetValue(f.PieceId, out var d) && File.Exists(d.GetFilePath(f.FileName))) return d.GetFilePath(f.FileName);
        }
        foreach (var id in p.Pieces.Concat(p.Includes.Select(i => i.PieceId)))
        {
            if (docs.TryGetValue(id, out var d) && _app.FindFolder(d.FolderPath)?.PreviewFilePath is { } preview) return preview;
        }
        return null;
    }

    [RelayCommand]
    private void Open(ProductTile? tile) => Ui.Fire(() => tile is null ? Task.CompletedTask : OpenProductAsync(tile.Product.Id));

    [RelayCommand]
    private Task NewProductAsync() => _app.HasRoot ? OpenNewProductAsync(null) : Dialogs.AlertAsync("No art folder", "Choose your art folder first.");

    public static Task OpenProductAsync(Guid id) =>
        Shell.Current.GoToAsync(AppShell.ProductRoute, new ShellNavigationQueryParameters { ["id"] = id.ToString() });

    /// <summary>Opens a new product, optionally already linked to an art piece.</summary>
    public static Task OpenNewProductAsync(Guid? pieceId) =>
        Shell.Current.GoToAsync(AppShell.ProductRoute, new ShellNavigationQueryParameters { ["newFor"] = pieceId?.ToString() ?? "" });
}

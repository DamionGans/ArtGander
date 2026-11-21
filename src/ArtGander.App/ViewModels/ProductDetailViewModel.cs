using System.Collections.ObjectModel;
using ArtGander.App.Services;
using ArtGander.Core.Model;
using ArtGander.Core.Naming;
using ArtGander.Core.Schema;
using ArtGander.Core.Services;
using ArtGander.Core.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ArtGander.App.ViewModels;

/// <summary>A checkbox that links a product to one file of an art piece.</summary>
public sealed partial class ProductFileToggle : ObservableObject
{
    private readonly Product _product;

    public ProductFileToggle(Product product, Guid pieceId, FileEntry file)
    {
        _product = product;
        PieceId = pieceId;
        File = file;
        IsLinked = product.Files.Any(Matches);
    }

    public Guid PieceId { get; }
    public FileEntry File { get; }
    public string Label => $"{File.Version} ({Path.GetExtension(File.FileName).TrimStart('.')})";

    [ObservableProperty] public partial bool IsLinked { get; set; }

    private bool Matches(ProductFile f) => f.PieceId == PieceId && NameRules.NamesEqual(f.FileName, File.FileName);

    partial void OnIsLinkedChanged(bool value)
    {
        var existing = _product.Files.FirstOrDefault(Matches);
        if (value && existing is null) _product.Files.Add(new ProductFile { PieceId = PieceId, FileName = File.FileName });
        else if (!value && existing is not null) _product.Files.Remove(existing);
    }
}

/// <summary>An art piece linked to the product, with its files to choose from.</summary>
public sealed partial class ProductPieceRow(ArtPieceDocument doc, string? previewPath) : ObservableObject
{
    public ArtPieceDocument Doc { get; } = doc;
    public string Title => Doc.DisplayTitle;
    public string? PreviewPath { get; } = previewPath;
    public ObservableCollection<ProductFileToggle> Files { get; } = [];
    [ObservableProperty] public partial ImageSource? Thumbnail { get; set; }

    public async Task LoadThumbnailAsync(ThumbnailService thumbs)
    {
        var path = await thumbs.GetAsync(PreviewPath);
        if (path is not null) MainThread.BeginInvokeOnMainThread(() => Thumbnail = ImageSource.FromFile(path));
    }
}

/// <summary>One product inside a bundle.</summary>
public sealed partial class BundleRow : ObservableObject
{
    public BundleRow(BundleItem item, IReadOnlyList<Product> candidates)
    {
        Item = item;
        Candidates = candidates;
        Selected = candidates.FirstOrDefault(p => p.Id == item.ProductId);
    }

    public BundleItem Item { get; }
    public IReadOnlyList<Product> Candidates { get; }
    [ObservableProperty] public partial Product? Selected { get; set; }

    partial void OnSelectedChanged(Product? value)
    {
        if (value is null) return;
        Item.ProductId = value.Id;
        Item.PieceId = value.Pieces.FirstOrDefault();
    }
}

/// <summary>
/// The editor for one product. Edits are made on a copy and written on Save; a product changed on another computer
/// (or by a sync) in the meantime is never overwritten without asking.
/// </summary>
public sealed partial class ProductDetailViewModel(AppState app, ThumbnailService thumbs) : ObservableObject, IQueryAttributable
{
    public static IReadOnlyList<ProductType> ProductTypes { get; } = Enum.GetValues<ProductType>();
    public static IReadOnlyList<ProductStatus> ProductStatuses { get; } = Enum.GetValues<ProductStatus>();
    public static IReadOnlyList<ListingStatus> ListingStatuses { get; } = Enum.GetValues<ListingStatus>();

    private string _savedJson = "";
    private string _savedTags = "";

    [ObservableProperty] public partial Product Product { get; private set; } = new();
    [ObservableProperty] public partial bool IsNew { get; private set; }
    [ObservableProperty] public partial string Tab { get; set; } = "overview";
    [ObservableProperty] public partial string TagsText { get; set; } = "";
    [ObservableProperty] public partial string SalesSummary { get; set; } = "";
    [ObservableProperty] public partial string? Notice { get; set; }
    [ObservableProperty] public partial Core.Storage.ArtPieceDocument? SelectedAvailablePiece { get; set; }

    public ObservableCollection<ProductPieceRow> LinkedPieces { get; } = [];
    public ObservableCollection<ArtPieceDocument> AvailablePieces { get; } = [];
    public ObservableCollection<BundleRow> Bundle { get; } = [];
    public ObservableCollection<SaleRow> Sales { get; } = [];

    public IReadOnlyList<string> Channels => app.Settings.Channels;
    public string Currency => app.Currency;
    public bool IsReadOnly => Records.Products.IsReadOnly(Product);

    public bool IsOverview => Tab == "overview";
    public bool IsArt => Tab == "art";
    public bool IsPrices => Tab == "prices";
    public bool IsChannels => Tab == "channels";
    public bool IsSales => Tab == "sales";

    public string PrintifyText => Product.Channels.Printify is { } p
        ? $"Linked to Printify product {p.ProductId}" + (p.LastSyncedAt is { } t ? $" · last synced {t:g}" : "")
        : Product.Fulfillment == Fulfillment.Printify ? "Not on Printify yet." : "Not a Printify product (you make and ship it yourselves).";
    public string ShopifyText => Product.Channels.Shopify is { } s
        ? $"Linked to Shopify product {s.Handle}" + (s.LastSyncedAt is { } t ? $" · last synced {t:g}" : "")
        : "Not in the Shopify store yet.";
    public string? ShopifyUrl => Product.Channels.Shopify?.Url is { Length: > 0 } u ? u : null;
    public bool HasShopifyUrl => ShopifyUrl is not null;

    public bool IsLeaveConfirmed { get; set; }

    public bool HasUnsavedChanges => !IsReadOnly && (ArtGanderJson.Serialize(Product) != _savedJson || TagsText != _savedTags);

    partial void OnTabChanged(string value)
    {
        OnPropertyChanged(nameof(IsOverview));
        OnPropertyChanged(nameof(IsArt));
        OnPropertyChanged(nameof(IsPrices));
        OnPropertyChanged(nameof(IsChannels));
        OnPropertyChanged(nameof(IsSales));
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("tab", out var t) && t is string tab) Tab = tab;
        if (query.TryGetValue("id", out var id) && Guid.TryParse(id as string, out var productId))
        {
            Load(app.Catalog.Find(productId));
            return;
        }
        var product = new Product { Currency = Currency, Type = ProductType.PrintOnDemand, Status = ProductStatus.Draft };
        if (query.TryGetValue("newFor", out var pf) && Guid.TryParse(pf as string, out var pieceId)
            && app.Documents.FirstOrDefault(d => d.Piece.Id == pieceId) is { } doc)
        {
            product.Pieces.Add(pieceId);
            product.Name = doc.Piece.Title;
            product.Description = doc.Piece.Description;
            foreach (var tag in doc.Piece.Tags) product.Tags.Add(tag);
            // Use the most likely print file: transparent, then print, then master.
            var file = doc.Piece.Files.FirstOrDefault(f => f.Kind == FileKind.Transparent)
                       ?? doc.Piece.Files.FirstOrDefault(f => f.Kind == FileKind.Print)
                       ?? doc.Piece.Files.FirstOrDefault(f => f.Kind == FileKind.Master);
            if (file is not null) product.Files.Add(new ProductFile { PieceId = pieceId, FileName = file.FileName });
        }
        Load(null, product);
    }

    private void Load(Product? existing, Product? fresh = null)
    {
        IsNew = existing is null;
        Product = existing is not null ? Copy(existing) : fresh ?? new Product { Currency = Currency };
        _savedJson = existing is null ? "" : ArtGanderJson.Serialize(Product);
        _savedTags = TagsText = string.Join(", ", Product.Tags);
        if (existing is null && fresh is not null) _savedTags = "\0"; // a new product counts as unsaved
        IsLeaveConfirmed = false;
        Notice = existing is null && fresh is null ? null : IsReadOnly ? "This product was saved by a newer ArtGander. Please update to change it." : null;
        OnPropertyChanged(nameof(IsReadOnly));
        RebuildAll();
    }

    private static Product Copy(Product p)
    {
        var copy = ArtGanderJson.DeserializeProduct(ArtGanderJson.Serialize(p))!;
        copy.StoredFileName = p.StoredFileName;
        copy.StoredHash = p.StoredHash;
        return copy;
    }

    private void RebuildAll()
    {
        RebuildPieces();
        RebuildBundle();
        RebuildSales();
        OnPropertyChanged(nameof(PrintifyText));
        OnPropertyChanged(nameof(ShopifyText));
        OnPropertyChanged(nameof(ShopifyUrl));
        OnPropertyChanged(nameof(HasShopifyUrl));
    }

    private void RebuildPieces()
    {
        LinkedPieces.Clear();
        AvailablePieces.Clear();
        var docs = app.Documents.ToList();
        foreach (var id in Product.Pieces)
        {
            if (docs.FirstOrDefault(d => d.Piece.Id == id) is not { } doc) continue;
            var row = new ProductPieceRow(doc, app.FindFolder(doc.FolderPath)?.PreviewFilePath);
            foreach (var f in doc.Piece.Files) row.Files.Add(new ProductFileToggle(Product, doc.Piece.Id, f));
            LinkedPieces.Add(row);
            _ = row.LoadThumbnailAsync(thumbs);
        }
        foreach (var d in docs.Where(d => !Product.Pieces.Contains(d.Piece.Id)).OrderBy(d => d.DisplayTitle, NameRules.NaturalComparer))
            AvailablePieces.Add(d);
        SelectedAvailablePiece = null;
    }

    private void RebuildBundle()
    {
        Bundle.Clear();
        var candidates = app.Catalog.All.Where(p => p.Id != Product.Id).ToList();
        foreach (var item in Product.Includes) Bundle.Add(new BundleRow(item, candidates));
    }

    private void RebuildSales()
    {
        Sales.Clear();
        foreach (var r in SalesReport.ForProduct(app.SaleRows, Product.Id)) Sales.Add(r);
        var qty = Sales.Sum(r => r.Line.Quantity);
        SalesSummary = qty == 0 ? "Not sold yet." : $"{qty} sold · revenue {Sales.Sum(r => r.Revenue):N2} · profit {Sales.Sum(r => r.Profit):N2} {Currency}";
    }

    [RelayCommand]
    private void SelectTab(string tab) => Tab = tab;

    // ---- Art pieces ---------------------------------------------------------------------------------

    [RelayCommand]
    private void LinkPiece()
    {
        if (SelectedAvailablePiece is not { } doc) return;
        Product.Pieces.Add(doc.Piece.Id);
        RebuildPieces();
    }

    [RelayCommand]
    private void UnlinkPiece(ProductPieceRow? row)
    {
        if (row is null) return;
        Product.Pieces.Remove(row.Doc.Piece.Id);
        foreach (var f in Product.Files.Where(f => f.PieceId == row.Doc.Piece.Id).ToList()) Product.Files.Remove(f);
        RebuildPieces();
    }

    [RelayCommand]
    private void OpenPiece(ProductPieceRow? row) => Ui.Fire(() => row is null ? Task.CompletedTask : LibraryViewModel.OpenPieceAsync(row.Doc.FolderPath, "products"));

    // ---- Bundle -------------------------------------------------------------------------------------

    [RelayCommand]
    private void AddBundleItem()
    {
        Product.Includes.Add(new BundleItem());
        RebuildBundle();
    }

    [RelayCommand]
    private void RemoveBundleItem(BundleRow? row)
    {
        if (row is null) return;
        Product.Includes.Remove(row.Item);
        RebuildBundle();
    }

    [RelayCommand]
    private void UseBundleCost()
    {
        if (SalesReport.BundleCost(Product, app.Catalog.ById) is { } cost) Product.ProductionCost = cost;
    }

    // ---- Prices, variants and costs -------------------------------------------------------------------

    [RelayCommand]
    private void AddVariant() => Product.Variants.Add(new ProductVariant { Title = $"Variant {Product.Variants.Count + 1}" });

    [RelayCommand]
    private void RemoveVariant(ProductVariant? v)
    {
        if (v is not null) Product.Variants.Remove(v);
    }

    [RelayCommand]
    private void AddCostLine() => Product.CostBreakdown.Add(new CostLine());

    [RelayCommand]
    private void RemoveCostLine(CostLine? line)
    {
        if (line is not null) Product.CostBreakdown.Remove(line);
    }

    [RelayCommand]
    private void SumCosts() => Product.ProductionCost = Product.CostBreakdown.Sum(c => c.Amount);

    [RelayCommand]
    private void AddListing() => Product.Listings.Add(new Listing { Channel = Channels.FirstOrDefault(c => c is not ("shopify" or "printify")) ?? "other", Price = Product.Price });

    [RelayCommand]
    private void RemoveListing(Listing? listing)
    {
        if (listing is not null) Product.Listings.Remove(listing);
    }

    [RelayCommand]
    private void OpenUrl(string? url) => Ui.Fire(() => OpenUrlAsync(url));

    private static async Task OpenUrlAsync(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        if (!url.Contains("://", StringComparison.Ordinal)) url = "https://" + url;
        try { await Launcher.Default.OpenAsync(new Uri(url)); }
        catch (Exception ex) { await Dialogs.ErrorAsync(ex); }
    }

    // ---- Sales --------------------------------------------------------------------------------------

    [RelayCommand]
    private void OpenOrder(SaleRow? row) => Ui.Fire(() => row is null ? Task.CompletedTask : OrdersViewModel.OpenOrderAsync(row.Order.Id));

    // ---- Saving -------------------------------------------------------------------------------------

    [RelayCommand]
    private Task<bool> SaveAsync() => TrySaveAsync();

    public Task<bool> TrySaveAsync() => SaveAsync(force: false);

    private async Task<bool> SaveAsync(bool force)
    {
        if (!app.HasRoot || IsReadOnly) return false;
        Product.Name = Product.Name.Trim();
        if (Product.Name.Length == 0) { await Dialogs.AlertAsync("Name missing", "Please give this product a name, e.g. \"Goose poster A3\"."); return false; }
        var tags = TagsText.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (!Product.Tags.SequenceEqual(tags)) { Product.Tags.Clear(); foreach (var t in tags) Product.Tags.Add(t); }
        try
        {
            using (app.SuspendWatching()) app.Catalog.Save(Product, force);
            var saved = Product;
            Product = Copy(saved); // keep editing a copy; the catalog holds the saved one
            _savedJson = ArtGanderJson.Serialize(Product);
            _savedTags = TagsText = string.Join(", ", Product.Tags);
            IsNew = false;
            Notice = null;
            await app.RefreshAsync();
            RebuildAll();
            return true;
        }
        catch (ConcurrentEditException)
        {
            const string reload = "Reload (discard my changes)";
            const string overwrite = "Overwrite with my version";
            var choice = await Dialogs.ChooseAsync("This product was changed on another computer (or by a sync) since you opened it.", reload, overwrite);
            if (choice == overwrite) return await SaveAsync(force: true);
            if (choice == reload) { Load(app.Catalog.Reload(Product) ?? Product); return true; }
            return false;
        }
        catch (Exception ex)
        {
            await Dialogs.ErrorAsync(ex);
            return false;
        }
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (IsNew || !app.HasRoot) { IsLeaveConfirmed = true; await Shell.Current.GoToAsync(".."); return; }
        var online = Product.IsOnPrintify || Product.IsOnShopify
            ? " It is only removed from ArtGander, not from Printify or Shopify." : "";
        var msg = (Sales.Count > 0 ? $"{Sales.Count} sold item(s) refer to it. They are kept, but lose their product link." : "The product is deleted.") + online;
        if (!await Dialogs.ConfirmAsync($"Delete \"{Product.Name}\"?", msg, "Delete")) return;
        using (app.SuspendWatching())
        {
            if (app.Catalog.Find(Product.Id) is { } stored) app.Catalog.Delete(stored);
        }
        IsLeaveConfirmed = true;
        await app.RefreshAsync();
        await Shell.Current.GoToAsync("..");
    }
}

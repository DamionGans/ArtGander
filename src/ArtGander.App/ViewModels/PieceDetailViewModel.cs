using System.Collections.ObjectModel;
using ArtGander.App.Services;
using ArtGander.Core.Library;
using ArtGander.Core.Model;
using ArtGander.Core.Naming;
using ArtGander.Core.Services;
using ArtGander.Core.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ArtGander.App.ViewModels;

/// <summary>
/// The editor for one art piece. Text edits stay in memory until Save. File operations (import, rename,
/// track, untrack) change the disk straight away and save the details file together with them.
/// </summary>
public sealed partial class PieceDetailViewModel(
    AppState app,
    ArtPieceStore store,
    PieceService pieces,
    IPlatformServices platform,
    ThumbnailService thumbs) : ObservableObject, IQueryAttributable
{
    public static IReadOnlyList<ProductType> ProductTypes { get; } = Enum.GetValues<ProductType>();
    public static IReadOnlyList<OriginalStatus> OriginalStatuses { get; } = Enum.GetValues<OriginalStatus>();
    public static IReadOnlyList<CopyrightStatus> CopyrightStatuses { get; } = Enum.GetValues<CopyrightStatus>();
    public static IReadOnlyList<ListingStatus> ListingStatuses { get; } = Enum.GetValues<ListingStatus>();

    private IDispatcherTimer? _diskCheck;
    private string _savedCollections = "";
    private string _savedTags = "";

    [ObservableProperty] public partial ArtPieceDocument? Doc { get; private set; }
    [ObservableProperty] public partial string EditTitle { get; set; } = "";
    [ObservableProperty] public partial string CollectionsText { get; set; } = "";
    [ObservableProperty] public partial string TagsText { get; set; } = "";
    [ObservableProperty] public partial string Tab { get; set; } = "overview";
    [ObservableProperty] public partial ImageSource? Thumbnail { get; set; }
    [ObservableProperty] public partial bool ChangedElsewhere { get; set; }
    [ObservableProperty] public partial string? Notice { get; set; }
    [ObservableProperty] public partial string FolderPreview { get; set; } = "";

    public ArtPiece? Piece => Doc?.Piece;
    public bool IsReadOnly => Doc?.IsReadOnly == true;
    public bool IsEditable => Doc is { IsReadOnly: false };
    public string? ReadOnlyReason => Doc?.ReadOnlyReason;
    public string FolderPath => Doc?.FolderPath ?? "";
    public IReadOnlyList<string> Channels => app.Settings.Channels;

    /// <summary>The library's file kinds (Settings), plus any other kind this piece already uses so it still shows.</summary>
    public IReadOnlyList<string> FileKinds =>
        (app.Settings.FileKinds.Count > 0 ? app.Settings.FileKinds : FileKind.Defaults)
            .Concat(Piece?.Files.Select(f => f.Kind) ?? [])
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    public string Currency => app.Currency;
    public IReadOnlyList<string> KnownCollections => app.AllCollections;

    public ObservableCollection<FileRowViewModel> Files { get; } = [];
    public ObservableCollection<UntrackedFileViewModel> Untracked { get; } = [];
    public ObservableCollection<ProductViewModel> Products { get; } = [];
    public ObservableCollection<SaleViewModel> Sales { get; } = [];

    [ObservableProperty] public partial string SalesSummary { get; set; } = "";

    /// <summary>Set once the user agreed to leave without saving, so the navigation guard doesn't ask twice.</summary>
    public bool IsLeaveConfirmed { get; set; }

    public bool HasUnsavedChanges =>
        Doc is { IsReadOnly: false } doc
        && (doc.HasUnsavedChanges || EditTitle.Trim() != doc.Piece.Title || CollectionsText != _savedCollections || TagsText != _savedTags);

    public bool IsOverview => Tab == "overview";
    public bool IsFiles => Tab == "files";
    public bool IsProducts => Tab == "products";
    public bool IsSales => Tab == "sales";
    public bool IsCopyright => Tab == "copyright";

    partial void OnTabChanged(string value)
    {
        OnPropertyChanged(nameof(IsOverview));
        OnPropertyChanged(nameof(IsFiles));
        OnPropertyChanged(nameof(IsProducts));
        OnPropertyChanged(nameof(IsSales));
        OnPropertyChanged(nameof(IsCopyright));
    }

    partial void OnEditTitleChanged(string value)
    {
        var folder = NameRules.ToFolderName(value);
        FolderPreview = Doc is not null && folder != Doc.FolderName ? $"Saving will rename the folder and its files to \"{folder}\"." : "";
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("tab", out var tab) && tab is string t) Tab = t;
        if (query.TryGetValue("folder", out var folder) && folder is string path) Load(path);
    }

    private void Load(string folderPath)
    {
        try
        {
            var doc = store.Load(folderPath);
            Bind(doc);
            if (doc.UpgradedFromVersion is { } v)
                Notice = $"This piece's details file was upgraded from format version {v}. A backup was kept as .artgander.json.v{v}.bak.";
        }
        catch (Exception ex)
        {
            Notice = ex.Message;
        }
    }

    /// <param name="rebuildLists">False after a save of the same document: keeps the product and sale editors in place.</param>
    private void Bind(ArtPieceDocument doc, bool rebuildLists = true)
    {
        Doc = doc;
        IsLeaveConfirmed = false;
        ChangedElsewhere = false;
        EditTitle = doc.Piece.Title;
        _savedCollections = CollectionsText = string.Join(", ", doc.Piece.Collections);
        _savedTags = TagsText = string.Join(", ", doc.Piece.Tags);
        OnPropertyChanged(nameof(Piece));
        OnPropertyChanged(nameof(IsReadOnly));
        OnPropertyChanged(nameof(IsEditable));
        OnPropertyChanged(nameof(ReadOnlyReason));
        OnPropertyChanged(nameof(FolderPath));
        OnPropertyChanged(nameof(KnownCollections));
        OnPropertyChanged(nameof(FileKinds));
        RebuildFiles();
        OnEditTitleChanged(EditTitle);
        if (rebuildLists) { RebuildProducts(); RebuildSales(); }
        else UpdateSalesSummary();
    }

    private void RebuildFiles()
    {
        if (Doc is null) return;
        var folder = PieceFolder.From(Doc.FolderPath, Doc);
        Files.Clear();
        foreach (var f in Doc.Piece.Files)
        {
            var row = new FileRowViewModel(f, Doc.GetFilePath(f.FileName), folder.MissingFiles.Contains(f.FileName), folder.MisnamedFiles.Contains(f.FileName));
            Files.Add(row);
            _ = row.LoadThumbnailAsync(thumbs);
        }
        Untracked.Clear();
        foreach (var name in folder.UntrackedFiles)
        {
            var version = FileNamer.SuggestVersion(Doc.FolderName, name);
            Untracked.Add(new UntrackedFileViewModel(name, version, PieceService.GuessKind(version, Path.GetExtension(name), app.Settings.FileKinds)));
        }
        _ = LoadThumbnailAsync(folder.PreviewFilePath);
        foreach (var p in Products) p.RebuildLinks(Doc.Piece.Files);
    }

    private async Task LoadThumbnailAsync(string? path)
    {
        var thumb = await thumbs.GetAsync(path);
        MainThread.BeginInvokeOnMainThread(() => Thumbnail = thumb is null ? null : ImageSource.FromFile(thumb));
    }

    private void RebuildProducts()
    {
        Products.Clear();
        if (Doc is null) return;
        foreach (var p in Doc.Piece.Products) Products.Add(new ProductViewModel(p, Doc.Piece.Files));
    }

    private void RebuildSales()
    {
        Sales.Clear();
        if (Doc is null) return;
        foreach (var s in Doc.Piece.Sales.OrderByDescending(s => s.Date)) Sales.Add(new SaleViewModel(s, Doc.Piece.Products));
        UpdateSalesSummary();
    }

    private void UpdateSalesSummary()
    {
        if (Doc is null) return;
        var rows = SalesReport.Rows((ArtPieceDocument[])[Doc]);
        var qty = rows.Sum(r => r.Sale.Quantity);
        SalesSummary = qty == 0
            ? "No sales recorded yet."
            : $"{qty} sold · revenue {rows.Sum(r => r.Revenue):N2} · profit {rows.Sum(r => r.Profit):N2} {Currency}";
    }

    // ---- Lifecycle ---------------------------------------------------------------------------------

    public void OnAppearing()
    {
        _diskCheck ??= Application.Current!.Dispatcher.CreateTimer();
        _diskCheck.Interval = TimeSpan.FromSeconds(5);
        _diskCheck.Tick -= OnDiskCheck;
        _diskCheck.Tick += OnDiskCheck;
        _diskCheck.Start();
    }

    public void OnDisappearing() => _diskCheck?.Stop();

    /// <summary>Picks up edits made on another computer. Reloads silently when nothing is pending here.</summary>
    private void OnDiskCheck(object? sender, EventArgs e)
    {
        if (Doc is null || ChangedElsewhere) return;
        bool changed;
        try { changed = store.ChangedOnDisk(Doc); } catch (Exception) { return; }
        if (!changed) return;
        if (!Directory.Exists(Doc.FolderPath)) { Notice = "This piece's folder was moved or renamed outside ArtGander. Go back to the library to find it."; return; }
        if (HasUnsavedChanges) ChangedElsewhere = true;
        else Load(Doc.FolderPath);
    }

    // ---- Saving ------------------------------------------------------------------------------------

    private void ApplyListEdits()
    {
        if (Piece is null) return;
        ReplaceAll(Piece.Collections, SplitList(CollectionsText));
        ReplaceAll(Piece.Tags, SplitList(TagsText));
    }

    private static List<string> SplitList(string text) =>
        text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static void ReplaceAll(ObservableCollection<string> target, List<string> values)
    {
        if (target.SequenceEqual(values)) return;
        target.Clear();
        foreach (var v in values) target.Add(v);
    }

    [RelayCommand]
    private Task SaveAsync() => SaveAsync(force: false);

    /// <summary>Saves and reports whether it worked (used by the leave-page guard).</summary>
    public Task<bool> TrySaveAsync() => SaveAsync(force: false);

    private async Task<bool> SaveAsync(bool force)
    {
        if (Doc is null || Doc.IsReadOnly) return false;
        ApplyListEdits();
        try
        {
            using (app.SuspendWatching())
            {
                if (force) store.Save(Doc, force: true);
                var title = EditTitle.Trim();
                if (title.Length == 0) { await Dialogs.AlertAsync("Title missing", "Please give this art piece a title."); return false; }
                if (title != Doc.Piece.Title) pieces.RenamePiece(Doc, title);
                else store.Save(Doc, force);
            }
            Bind(Doc, rebuildLists: false);
            Notice = null;
            _ = app.RefreshAsync();
            return true;
        }
        catch (ConcurrentEditException)
        {
            return await ResolveConflictAsync();
        }
        catch (Exception ex)
        {
            await Dialogs.ErrorAsync(ex);
            return false;
        }
    }

    private async Task<bool> ResolveConflictAsync()
    {
        const string reload = "Reload (discard my changes)";
        const string overwrite = "Overwrite with my version";
        var choice = await Dialogs.ChooseAsync("This piece was changed on another computer since you opened it.", reload, overwrite);
        if (choice == reload) { Load(Doc!.FolderPath); return true; }
        if (choice == overwrite) return await SaveAsync(force: true);
        return false;
    }

    [RelayCommand]
    private async Task ReloadAsync()
    {
        if (Doc is null) return;
        if (HasUnsavedChanges && !await Dialogs.ConfirmAsync("Reload from disk?", "Your unsaved changes to this piece will be lost.", "Reload"))
            return;
        Load(Doc.FolderPath);
    }

    [RelayCommand]
    private Task OverwriteAsync() => SaveAsync(force: true);

    [RelayCommand]
    private void SelectTab(string tab) => Tab = tab;

    [RelayCommand]
    private void RevealFolder()
    {
        if (Doc is not null) platform.RevealInFileManager(Doc.FolderPath);
    }

    [RelayCommand]
    private void AddCollection(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var list = SplitList(CollectionsText);
        if (!list.Contains(name, StringComparer.OrdinalIgnoreCase)) list.Add(name);
        CollectionsText = string.Join(", ", list);
    }

    // ---- Files -------------------------------------------------------------------------------------

    /// <summary>Runs a file operation. File operations save immediately, so pending text edits are applied first.</summary>
    private async Task FileOpAsync(Action op)
    {
        if (Doc is null || Doc.IsReadOnly) return;
        ApplyListEdits();
        try
        {
            using (app.SuspendWatching()) op();
            _savedCollections = CollectionsText;
            _savedTags = TagsText;
        }
        catch (ConcurrentEditException)
        {
            await Dialogs.AlertAsync("Changed on another computer", "This piece was changed elsewhere. It will be reloaded, then please try again.");
            Load(Doc.FolderPath);
            return;
        }
        catch (Exception ex)
        {
            await Dialogs.ErrorAsync(ex);
        }
        RebuildFiles();
        _ = app.RefreshAsync();
    }

    [RelayCommand]
    private async Task AddFilesAsync()
    {
        if (Doc is null) return;
        IReadOnlyList<string> picked;
        try { picked = await platform.PickFilesAsync(Doc.FolderPath); } // opens in this piece's folder
        catch (Exception ex) { await Dialogs.ErrorAsync(ex); return; }
        await ImportPathsAsync(picked);
    }

    public async Task ImportPathsAsync(IReadOnlyList<string> paths)
    {
        if (Doc is null || paths.Count == 0) return;
        foreach (var path in paths)
        {
            if (!File.Exists(path)) continue;

            // A file that is already in this piece's folder is tracked (and renamed) in place, never copied.
            if (NameRules.NamesEqual(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFullPath(Doc.FolderPath)))
            {
                var name = Path.GetFileName(path);
                if (Doc.Piece.FindFile(name) is not null) { await Dialogs.AlertAsync("Already added", $"\"{name}\" is already part of this art piece."); continue; }
                var inPlaceVersion = await Dialogs.PromptAsync("Version name",
                    $"Version label for \"{name}\".\nIt is renamed to {NameRules.ToFileBaseName(Doc.FolderName)}-<version>{Path.GetExtension(name).ToLowerInvariant()}",
                    FileNamer.SuggestVersion(Doc.FolderName, name), "Add", "e.g. master, transparent, print-a4");
                if (string.IsNullOrWhiteSpace(inPlaceVersion)) continue;
                var inPlaceKind = PieceService.GuessKind(inPlaceVersion, Path.GetExtension(name), app.Settings.FileKinds);
                await FileOpAsync(() => pieces.TrackFile(Doc, name, inPlaceVersion, inPlaceKind, renameToConvention: true));
                continue;
            }

            var suggestion = FileNamer.SuggestVersion(Doc.FolderName, Path.GetFileName(path));
            var version = await Dialogs.PromptAsync("Version name",
                $"Version label for \"{Path.GetFileName(path)}\".\nIt is saved as {NameRules.ToFileBaseName(Doc.FolderName)}-<version>{Path.GetExtension(path).ToLowerInvariant()}",
                suggestion, "Add", "e.g. master, transparent, print-a4");
            if (string.IsNullOrWhiteSpace(version)) continue;
            var kind = PieceService.GuessKind(version, Path.GetExtension(path), app.Settings.FileKinds);
            await FileOpAsync(() => pieces.ImportFile(Doc, path, version, kind));
        }
    }

    [RelayCommand]
    private void OpenFile(FileRowViewModel? row) => Ui.Fire(() => OpenFileAsync(row));

    private async Task OpenFileAsync(FileRowViewModel? row)
    {
        if (row is null || row.IsMissing) return;
        try { await Launcher.Default.OpenAsync(new OpenFileRequest(row.Entry.FileName, new ReadOnlyFile(row.FullPath))); }
        catch (Exception ex) { await Dialogs.ErrorAsync(ex); }
    }

    [RelayCommand]
    private void RevealFile(FileRowViewModel? row)
    {
        if (row is not null) platform.RevealInFileManager(row.IsMissing ? FolderPath : row.FullPath);
    }

    [RelayCommand]
    private void RenameVersion(FileRowViewModel? row) => Ui.Fire(() => RenameVersionAsync(row));

    private Task RenameVersionAsync(FileRowViewModel? row) =>
        row is null || !row.VersionChanged ? Task.CompletedTask : FileOpAsync(() => pieces.ChangeFileVersion(Doc!, row.Entry, row.EditVersion));

    [RelayCommand]
    private void ApplyConvention(FileRowViewModel? row) => Ui.Fire(() => ApplyConventionAsync(row));

    private Task ApplyConventionAsync(FileRowViewModel? row) =>
        row is null ? Task.CompletedTask : FileOpAsync(() => pieces.ApplyConvention(Doc!, row.Entry));

    [RelayCommand]
    private void UntrackFile(FileRowViewModel? row) => Ui.Fire(() => UntrackFileAsync(row));

    private async Task UntrackFileAsync(FileRowViewModel? row)
    {
        if (row is null) return;
        var msg = row.IsMissing
            ? $"Remove the missing file \"{row.Entry.FileName}\" from this piece's list?"
            : $"Stop tracking \"{row.Entry.FileName}\"? The file stays in the folder, and product links to it are removed.";
        if (!await Dialogs.ConfirmAsync("Remove from piece", msg, "Remove")) return;
        await FileOpAsync(() => pieces.UntrackFile(Doc!, row.Entry));
        foreach (var p in Products) p.RebuildLinks(Doc!.Piece.Files);
    }

    [RelayCommand]
    private void TrackFile(UntrackedFileViewModel? file) => Ui.Fire(() => TrackFileAsync(file));

    private Task TrackFileAsync(UntrackedFileViewModel? file) =>
        file is null ? Task.CompletedTask : FileOpAsync(() => pieces.TrackFile(Doc!, file.FileName, file.Version, file.Kind, renameToConvention: true));

    [RelayCommand]
    private void TrackFileAsIs(UntrackedFileViewModel? file) => Ui.Fire(() => TrackFileAsIsAsync(file));

    private Task TrackFileAsIsAsync(UntrackedFileViewModel? file) =>
        file is null ? Task.CompletedTask : FileOpAsync(() => pieces.TrackFile(Doc!, file.FileName, file.Version, file.Kind, renameToConvention: false));

    // ---- Products ----------------------------------------------------------------------------------

    [RelayCommand]
    private void AddProduct()
    {
        if (Piece is null) return;
        var product = new Product { Name = "", Currency = Currency, Type = ProductType.SelfPrint };
        Piece.Products.Add(product);
        Products.Add(new ProductViewModel(product, Piece.Files));
    }

    [RelayCommand]
    private void RemoveProduct(ProductViewModel? vm) => Ui.Fire(() => RemoveProductAsync(vm));

    private async Task RemoveProductAsync(ProductViewModel? vm)
    {
        if (vm is null || Piece is null) return;
        var used = Piece.Sales.Count(s => s.ProductId == vm.Product.Id);
        var msg = used > 0 ? $"{used} recorded sale(s) refer to this product. They are kept, but lose their product link." : "This product will be removed when you save.";
        if (!await Dialogs.ConfirmAsync($"Remove \"{vm.Header}\"?", msg, "Remove")) return;
        Piece.Products.Remove(vm.Product);
        Products.Remove(vm);
    }

    [RelayCommand]
    private void AddListing(ProductViewModel? vm) =>
        vm?.Product.Listings.Add(new Listing { Channel = Channels.FirstOrDefault() ?? "shopify", Price = vm.Product.Price });

    [RelayCommand]
    private void RemoveListing(Listing? listing)
    {
        if (listing is null || Piece is null) return;
        foreach (var p in Piece.Products) if (p.Listings.Remove(listing)) return;
    }

    [RelayCommand]
    private void AddCostLine(ProductViewModel? vm) => vm?.Product.CostBreakdown.Add(new CostLine());

    [RelayCommand]
    private void RemoveCostLine(CostLine? line)
    {
        if (line is null || Piece is null) return;
        foreach (var p in Piece.Products) if (p.CostBreakdown.Remove(line)) return;
    }

    [RelayCommand]
    private void SumCosts(ProductViewModel? vm)
    {
        if (vm is not null) vm.Product.ProductionCost = vm.Product.CostBreakdown.Sum(c => c.Amount);
    }

    [RelayCommand]
    private void OpenUrl(string? url) => Ui.Fire(() => OpenUrlAsync(url));

    private async Task OpenUrlAsync(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        if (!url.Contains("://", StringComparison.Ordinal)) url = "https://" + url;
        try { await Launcher.Default.OpenAsync(new Uri(url)); }
        catch (Exception ex) { await Dialogs.ErrorAsync(ex); }
    }

    // ---- Sales -------------------------------------------------------------------------------------

    [RelayCommand]
    private void AddSale()
    {
        if (Piece is null) return;
        var product = Piece.Products.FirstOrDefault();
        var sale = new Sale
        {
            Currency = Currency,
            Channel = Channels.FirstOrDefault() ?? "shopify",
            ProductId = product?.Id,
            UnitPrice = product?.Price ?? 0,
        };
        Piece.Sales.Add(sale);
        Sales.Insert(0, new SaleViewModel(sale, Piece.Products));
        UpdateSalesSummary();
    }

    [RelayCommand]
    private void RemoveSale(SaleViewModel? vm) => Ui.Fire(() => RemoveSaleAsync(vm));

    private async Task RemoveSaleAsync(SaleViewModel? vm)
    {
        if (vm is null || Piece is null) return;
        if (!await Dialogs.ConfirmAsync("Remove this sale?", $"{vm.Sale.Date:d} · {vm.Sale.OrderRef} · {vm.Revenue:N2} {vm.Sale.Currency}", "Remove")) return;
        Piece.Sales.Remove(vm.Sale);
        Sales.Remove(vm);
        UpdateSalesSummary();
    }

    [RelayCommand]
    private void RefreshSalesSummary() => UpdateSalesSummary();
}

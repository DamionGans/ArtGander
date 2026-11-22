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
    ProductCatalog catalog,
    IPlatformServices platform,
    ThumbnailService thumbs,
    ProtectionService protection) : ObservableObject, IQueryAttributable
{
    public static IReadOnlyList<OriginalStatus> OriginalStatuses { get; } = Enum.GetValues<OriginalStatus>();
    public static IReadOnlyList<CopyrightStatus> CopyrightStatuses { get; } = Enum.GetValues<CopyrightStatus>();

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
    public ObservableCollection<LinkedProductRow> LinkedProducts { get; } = [];
    public ObservableCollection<Product> AvailableProducts { get; } = [];
    [ObservableProperty] public partial Product? SelectedAvailableProduct { get; set; }
    public bool HasLinkedProducts => LinkedProducts.Count > 0;
    public ObservableCollection<SaleRow> Sales { get; } = [];

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
        OnPropertyChanged(nameof(IsPieces));
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
                Notice = $"This item's details file was upgraded from format version {v}. A backup was kept as .artgander.json.v{v}.bak.";
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
        BindProject();
        UpdateSalesSummary();
    }

    private void RebuildFiles()
    {
        if (Doc is null) return;
        var folder = PieceFolder.From(Doc.FolderPath, Doc);
        var previous = Files.ToList();
        Files.Clear();
        foreach (var f in Doc.Piece.Files)
        {
            var row = new FileRowViewModel(f, Doc.GetFilePath(f.FileName), folder.MissingFiles.Contains(f.FileName), folder.MisnamedFiles.Contains(f.FileName));
            CarryOver(previous.FirstOrDefault(p => string.Equals(p.Entry.FileName, f.FileName, StringComparison.OrdinalIgnoreCase)), row);
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
    }

    private async Task LoadThumbnailAsync(string? path)
    {
        var thumb = await thumbs.GetAsync(path);
        MainThread.BeginInvokeOnMainThread(() => Thumbnail = thumb is null ? null : ImageSource.FromFile(thumb));
    }

    // ---- Lifecycle ---------------------------------------------------------------------------------

    public void OnAppearing()
    {
        _diskCheck ??= Application.Current!.Dispatcher.CreateTimer();
        _diskCheck.Interval = TimeSpan.FromSeconds(5);
        _diskCheck.Tick -= OnDiskCheck;
        _diskCheck.Tick += OnDiskCheck;
        _diskCheck.Start();
        if (Doc is not null) { BindProject(); RebuildProducts(); RebuildSales(); } // e.g. back from a product or a piece of this project
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
            : $"Stop tracking \"{row.Entry.FileName}\"? The file stays in the folder, and products that use it no longer link to it.";
        if (!await Dialogs.ConfirmAsync("Remove from piece", msg, "Remove")) return;
        await FileOpAsync(() => pieces.UntrackFile(Doc!, row.Entry));
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

    private void RebuildProducts()
    {
        LinkedProducts.Clear();
        AvailableProducts.Clear();
        if (Doc is null) return;
        var linked = catalog.ForPiece(Doc.Piece.Id);
        foreach (var p in linked) LinkedProducts.Add(new LinkedProductRow(p, Doc.Piece.Id, ProductCatalog.Shows(p, Doc.Piece.Id) && !p.Pieces.Contains(Doc.Piece.Id)));
        foreach (var p in catalog.All.Except(linked)) AvailableProducts.Add(p);
        SelectedAvailableProduct = null;
        OnPropertyChanged(nameof(HasLinkedProducts));
    }

    private void RebuildSales()
    {
        Sales.Clear();
        if (Doc is null) return;
        foreach (var r in SalesReport.ForPiece(app.SaleRows, Doc.Piece.Id)) Sales.Add(r);
        UpdateSalesSummary();
    }

    private void UpdateSalesSummary()
    {
        if (Doc is null) return;
        var qty = Sales.Sum(r => r.Line.Quantity);
        SalesSummary = (qty == 0
            ? "No sales recorded yet."
            : $"{qty} sold · revenue {Sales.Sum(r => r.Revenue):N2} · profit {Sales.Sum(r => r.Profit):N2} {Currency}")
            + (ProjectRollupText() is { Length: > 0 } rollup ? Environment.NewLine + rollup : "");
    }

    [RelayCommand]
    private void NewProduct() => Ui.Fire(NewProductAsync);

    private async Task NewProductAsync()
    {
        if (Doc is null) return;
        if (HasUnsavedChanges && !await TrySaveAsync()) return;
        await ProductsViewModel.OpenNewProductAsync(Doc.Piece.Id);
    }

    [RelayCommand]
    private void LinkProduct() => Ui.Fire(LinkProductAsync);

    private async Task LinkProductAsync()
    {
        if (Doc is null || SelectedAvailableProduct is not { } product) { await Dialogs.AlertAsync("Pick a product", "Choose an existing product to link to this art piece."); return; }
        try
        {
            using (app.SuspendWatching()) catalog.Link(product, Doc.Piece.Id);
            await ProductsViewModel.OfferMoveAsync(app, product);
        }
        catch (Exception ex) { await Dialogs.ErrorAsync(ex); }
        RebuildProducts();
        RebuildSales();
    }

    [RelayCommand]
    private void OpenProduct(LinkedProductRow? row) => Ui.Fire(() => row is null ? Task.CompletedTask : ProductsViewModel.OpenProductAsync(row.Product.Id));

    [RelayCommand]
    private void UnlinkProduct(LinkedProductRow? row) => Ui.Fire(() => UnlinkProductAsync(row));

    private async Task UnlinkProductAsync(LinkedProductRow? row)
    {
        if (row is null || Doc is null) return;
        if (!await Dialogs.ConfirmAsync($"Unlink \"{row.Product.Name}\"?", "The product itself is kept, it just no longer shows this art piece.", "Unlink")) return;
        try
        {
            using (app.SuspendWatching()) catalog.Unlink(row.Product, Doc.Piece.Id);
            await ProductsViewModel.OfferMoveAsync(app, row.Product);
        }
        catch (Exception ex) { await Dialogs.ErrorAsync(ex); }
        RebuildProducts();
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

    // ---- Sales -------------------------------------------------------------------------------------

    [RelayCommand]
    private void OpenOrder(SaleRow? row) => Ui.Fire(() => row is null ? Task.CompletedTask : OrdersViewModel.OpenOrderAsync(row.Order.Id));

    [RelayCommand]
    private void RefreshSalesSummary() => RebuildSales();
}

/// <summary>A product shown on an art piece's Products tab.</summary>
public sealed class LinkedProductRow(Product product, Guid pieceId, bool viaBundleOrFile)
{
    public Product Product { get; } = product;
    public string Title => string.IsNullOrWhiteSpace(Product.Name) ? "(unnamed product)" : Product.Name;
    public string Details => string.Join(" · ", new[]
    {
        Converters.HumanizeConverter.Humanize(Product.Type.ToString()),
        Product.Price is { } p ? $"{p:N2} {Product.Currency}" : "",
        Product.Variants.Count > 0 ? $"{Product.Variants.Count} variants" : "",
        Product.Pieces.Count > 1 ? $"{Product.Pieces.Count} art pieces" : "",
        viaBundleOrFile ? "part of a bundle" : "",
        Converters.HumanizeConverter.Humanize(Product.Status.ToString()),
    }.Where(x => x.Length > 0));
    public string Channels => string.Join("  ", new[] { Product.IsOnPrintify ? "Printify" : "", Product.IsOnShopify ? "Shopify" : "" }.Where(x => x.Length > 0));
    public Guid PieceId { get; } = pieceId;
}

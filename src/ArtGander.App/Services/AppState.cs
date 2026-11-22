using ArtGander.Core.Library;
using ArtGander.Core.Model;
using ArtGander.Core.Services;
using ArtGander.Core.Storage;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ArtGander.App.Services;

/// <summary>
/// The currently opened art library. It owns the scan results and the watcher that picks up changes made by
/// Explorer/Finder or by ArtGander on another computer.
/// </summary>
public sealed partial class AppState : ObservableObject, IDisposable
{
    private const string RootPreferenceKey = "artRoot";
    private readonly LibraryScanner _scanner;
    private readonly ArtPieceStore _store;
    private LibraryWatcher? _watcher;
    private CancellationTokenSource? _scanCts;
    private volatile Dictionary<Guid, string> _groupNames = [];

    public AppState(LibraryScanner scanner, ArtPieceStore store, ProductCatalog catalog)
    {
        _scanner = scanner;
        _store = store;
        Catalog = catalog;
        // ARTGANDER_LIBRARY opens a specific art folder for this run only, without changing the saved choice.
        // Useful for testing against sample data.
        var overridePath = Environment.GetEnvironmentVariable("ARTGANDER_LIBRARY");
        RootPath = !string.IsNullOrWhiteSpace(overridePath) ? overridePath : Preferences.Default.Get<string?>(RootPreferenceKey, null);
    }

    [ObservableProperty] public partial string? RootPath { get; private set; }
    [ObservableProperty] public partial IReadOnlyList<PieceFolder> Pieces { get; private set; } = [];
    [ObservableProperty] public partial LibrarySettings Settings { get; private set; } = new();
    [ObservableProperty] public partial IReadOnlyList<Order> Orders { get; private set; } = [];

    /// <summary>The library's products (shared with the services).</summary>
    public ProductCatalog Catalog { get; }

    /// <summary>Every order with its lines resolved to products and pieces.</summary>
    [ObservableProperty] public partial IReadOnlyList<OrderSummary> OrderSummaries { get; private set; } = [];

    /// <summary>Every order line, newest first.</summary>
    public IReadOnlyList<SaleRow> SaleRows => OrderSummaries.SelectMany(o => o.Lines).ToList();
    [ObservableProperty] public partial bool IsScanning { get; private set; }
    [ObservableProperty] public partial string? LastError { get; private set; }
    [ObservableProperty] public partial string? MigrationNotice { get; set; }

    public bool HasRoot => !string.IsNullOrEmpty(RootPath) && Directory.Exists(RootPath);

    /// <summary>Raised on the UI thread after every rescan.</summary>
    public event EventHandler? LibraryChanged;

    /// <summary>Every folder: top-level pieces and projects, plus the pieces inside projects.</summary>
    public IEnumerable<PieceFolder> AllFolders => Pieces.SelectMany(p => p.Children.Prepend(p));

    /// <summary>Every tracked piece and project, including pieces inside projects.</summary>
    public IEnumerable<ArtPieceDocument> Documents => AllFolders.Select(p => p.Document).OfType<ArtPieceDocument>();

    /// <summary>Finds the scanned folder (piece or project, at any level) for a path.</summary>
    public PieceFolder? FindFolder(string folderPath) =>
        AllFolders.FirstOrDefault(f => Core.Naming.NameRules.NamesEqual(Path.GetFullPath(f.FolderPath), Path.GetFullPath(folderPath)));

    public string Currency => string.IsNullOrWhiteSpace(Settings.DefaultCurrency) ? "EUR" : Settings.DefaultCurrency;

    /// <summary>Every collection name: those defined in library.json plus those used by any piece.</summary>
    public IReadOnlyList<string> AllCollections =>
        Settings.Collections.Select(c => c.Name)
            .Concat(Documents.SelectMany(d => d.Piece.Collections))
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    public async Task OpenAsync(string rootPath)
    {
        RootPath = rootPath;
        Preferences.Default.Set(RootPreferenceKey, rootPath);
        StartWatching();
        await RefreshAsync();
    }

    public async Task InitializeAsync()
    {
        if (!HasRoot) return;
        StartWatching();
        await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (!HasRoot)
        {
            Pieces = [];
            LibraryChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        _scanCts?.Cancel();
        var cts = _scanCts = new CancellationTokenSource();
        IsScanning = true;
        try
        {
            var root = RootPath!;
            var (pieces, settings, orders, products, migration, folders) = await Task.Run(() =>
            {
                var settings = LibrarySettingsStore.Load(root);
                var scanned = _scanner.Scan(root, cts.Token);
                var docs = scanned.SelectMany(p => p.Children.Prepend(p)).Select(p => p.Document).OfType<ArtPieceDocument>().ToList();
                // Products of one art piece or project are grouped in a folder named after it.
                _groupNames = docs.GroupBy(d => d.Piece.Id).ToDictionary(g => g.Key, g => g.First().FolderName);
                Catalog.GroupNameFor = id => _groupNames.TryGetValue(id, out var name) ? name : null;

                // The products folder must not be an art piece's folder (e.g. a piece called "Products").
                var productsFolder = ProductFolderMigrator.ChooseProductsFolder(root, settings.ProductsFolder);
                if (!Core.Naming.NameRules.NamesEqual(productsFolder, settings.ProductsFolder) && !ProductFolderMigrator.NeedsMigration(root))
                {
                    settings.ProductsFolder = productsFolder;
                    try { LibrarySettingsStore.Save(root, settings); } catch (IOException) { }
                }
                if (Catalog.RootPath != root) Catalog.SetLoaded(root, [], settings.ProductsFolder);

                LibraryMigrator.Result? migrated = null;
                ProductFolderMigrator.Result? moved = null;
                using (SuspendWatching())
                {
                    // Products and sales from ArtGander 0.2 move to their own files (once).
                    if (LibraryMigrator.NeedsMigration(docs))
                    {
                        Catalog.Load(root, settings.ProductsFolder);
                        migrated = LibraryMigrator.Run(root, docs, _store, Catalog);
                    }
                    // Products from ArtGander 0.3 move into their own folders with their pictures (once, or again
                    // when an older ArtGander on another computer saved one the old way).
                    if (ProductFolderMigrator.NeedsMigration(root))
                    {
                        Catalog.Load(root, settings.ProductsFolder);
                        moved = ProductFolderMigrator.Run(root, Catalog, settings);
                    }
                }
                var products = new ProductStore(root, settings.ProductsFolder).LoadAll();
                return (scanned, settings, Records.Orders.LoadAll(root), products, migrated, moved);
            }, cts.Token);
            if (cts.IsCancellationRequested) return;
            Pieces = pieces;
            if (_watcher is not null) _watcher.ProjectFolders = pieces.Where(p => p.IsProject).Select(p => p.FolderPath).ToList();
            Settings = settings;
            Orders = orders;
            Catalog.SetLoaded(root, products, settings.ProductsFolder);
            OrderSummaries = SalesReport.Orders(orders, Catalog.All, Documents);
            if (migration is { Pieces: > 0 } or { Problems.Count: > 0 })
                MigrationNotice = $"Upgraded the library: {migration.Products} products and {migration.OrderLines} sold items moved to their own files."
                    + (migration.Problems.Count > 0 ? " Not done yet (will retry): " + string.Join("; ", migration.Problems) : "");
            if (folders is { Moved: > 0 } or { Updated: > 0 } or { Problems.Count: > 0 })
                MigrationNotice = $"Upgraded the library: every product now has its own folder in \"{folders.ProductsFolder}\" (grouped per art piece), where its pictures go too. "
                    + "The old product files were kept in .artgander/backup. Please update ArtGander on your other computers."
                    + (folders.Problems.Count > 0 ? " Not done yet (will retry): " + string.Join("; ", folders.Problems) : "");
            LastError = null;
            LibraryChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LastError = $"Could not read the art folder: {ex.Message}";
        }
        finally
        {
            if (_scanCts == cts) IsScanning = false;
        }
    }

    public void SaveSettings()
    {
        if (HasRoot) LibrarySettingsStore.Save(RootPath!, Settings);
    }

    /// <summary>Suspends change notifications while ArtGander itself writes to disk.</summary>
    public IDisposable SuspendWatching() => _watcher?.Suspend() ?? new NoopDisposable();

    private void StartWatching()
    {
        _watcher?.Dispose();
        _watcher = null;
        if (!HasRoot) return;
        _watcher = new LibraryWatcher(RootPath!);
        _watcher.Changed += (_, _) => MainThread.BeginInvokeOnMainThread(async () => await RefreshAsync());
    }

    public void Dispose() => _watcher?.Dispose();

    private sealed class NoopDisposable : IDisposable { public void Dispose() { } }
}

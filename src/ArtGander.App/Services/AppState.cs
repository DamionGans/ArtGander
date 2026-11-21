using ArtGander.Core.Library;
using ArtGander.Core.Model;
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
    private LibraryWatcher? _watcher;
    private CancellationTokenSource? _scanCts;

    public AppState(LibraryScanner scanner)
    {
        _scanner = scanner;
        // ARTGANDER_LIBRARY opens a specific art folder for this run only, without changing the saved choice.
        // Useful for testing against sample data.
        var overridePath = Environment.GetEnvironmentVariable("ARTGANDER_LIBRARY");
        RootPath = !string.IsNullOrWhiteSpace(overridePath) ? overridePath : Preferences.Default.Get<string?>(RootPreferenceKey, null);
    }

    [ObservableProperty] public partial string? RootPath { get; private set; }
    [ObservableProperty] public partial IReadOnlyList<PieceFolder> Pieces { get; private set; } = [];
    [ObservableProperty] public partial LibrarySettings Settings { get; private set; } = new();
    [ObservableProperty] public partial IReadOnlyList<Order> Orders { get; private set; } = [];
    [ObservableProperty] public partial bool IsScanning { get; private set; }
    [ObservableProperty] public partial string? LastError { get; private set; }

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
            var (pieces, settings, orders) = await Task.Run(() => (_scanner.Scan(root, cts.Token), LibrarySettingsStore.Load(root), OrderStore.LoadAll(root)), cts.Token);
            if (cts.IsCancellationRequested) return;
            Pieces = pieces;
            if (_watcher is not null) _watcher.ProjectFolders = pieces.Where(p => p.IsProject).Select(p => p.FolderPath).ToList();
            Settings = settings;
            Orders = orders;
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

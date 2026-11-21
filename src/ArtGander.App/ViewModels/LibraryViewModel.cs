using System.Collections.ObjectModel;
using ArtGander.App.Services;
using ArtGander.Core.Library;
using ArtGander.Core.Naming;
using ArtGander.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ArtGander.App.ViewModels;

public sealed partial class LibraryViewModel : ObservableObject
{
    public const string AllCollections = "All collections";
    private readonly AppState _app;
    private readonly PieceService _pieces;
    private readonly IPlatformServices _platform;
    private readonly ThumbnailService _thumbs;

    public LibraryViewModel(AppState app, PieceService pieces, IPlatformServices platform, ThumbnailService thumbs)
    {
        _app = app;
        _pieces = pieces;
        _platform = platform;
        _thumbs = thumbs;
        _app.LibraryChanged += (_, _) => Rebuild();
        _app.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AppState.IsScanning)) OnPropertyChanged(nameof(IsScanning));
            if (e.PropertyName is nameof(AppState.RootPath)) OnPropertyChanged(nameof(HasRoot));
        };
    }

    public ObservableCollection<PieceItemViewModel> Items { get; } = [];
    public ObservableCollection<string> CollectionFilters { get; } = [AllCollections];

    [ObservableProperty] public partial string SearchText { get; set; } = "";
    [ObservableProperty] public partial string SelectedCollection { get; set; } = AllCollections;
    [ObservableProperty] public partial bool AttentionOnly { get; set; }
    [ObservableProperty] public partial string Summary { get; set; } = "";

    public bool IsScanning => _app.IsScanning;
    public bool HasRoot => _app.HasRoot;
    public bool IsEmpty => _app.HasRoot && !_app.IsScanning && _app.Pieces.Count == 0;
    public bool NoMatches => _app.Pieces.Count > 0 && Items.Count == 0;

    // ---- Selection ---------------------------------------------------------------------------------

    /// <summary>In selection mode, clicking a tile selects it instead of opening it.</summary>
    [ObservableProperty] public partial bool IsSelecting { get; set; }
    [ObservableProperty] public partial string SelectionText { get; set; } = "";

    // ---- Tile shape --------------------------------------------------------------------------------

    public static IReadOnlyList<string> TileShapes { get; } = (string[])["Portrait (3:4)", "Square", "Landscape (4:3)"];

    /// <summary>The shape of the library tiles; remembered on this computer.</summary>
    [ObservableProperty] public partial string TileShape { get; set; } = Preferences.Default.Get("tileShape", TileShapes[0]);

    /// <summary>Tile width, set by the page whenever the window resizes.</summary>
    [ObservableProperty] public partial double TileWidth { get; set; } = 220;

    public double TileHeight => Math.Round(TileWidth * TileShape switch
    {
        "Square" => 1.0,
        "Landscape (4:3)" => 0.75,
        _ => 4.0 / 3.0,
    });

    partial void OnTileWidthChanged(double value) => OnPropertyChanged(nameof(TileHeight));

    partial void OnTileShapeChanged(string value)
    {
        Preferences.Default.Set("tileShape", value);
        OnPropertyChanged(nameof(TileHeight));
    }
    [ObservableProperty] public partial int UntrackedSelectedCount { get; set; }
    [ObservableProperty] public partial int TrackedSelectedCount { get; set; }

    public bool CanTrackSelected => UntrackedSelectedCount > 0;
    public bool CanAddSelectedToCollection => TrackedSelectedCount > 0;
    public string TrackSelectedText => $"Start tracking ({UntrackedSelectedCount})";
    public string AddSelectedText => $"Add to collection… ({TrackedSelectedCount})";

    private IEnumerable<PieceItemViewModel> Selected => _all.Where(i => i.IsSelected);

    partial void OnIsSelectingChanged(bool value)
    {
        if (!value) foreach (var i in _all) i.IsSelected = false;
        foreach (var i in _all) i.IsSelecting = value;
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        var selected = Selected.ToList();
        UntrackedSelectedCount = selected.Count(i => !i.Folder.IsAdopted && i.Folder.Error is null);
        TrackedSelectedCount = selected.Count(i => i.Folder.Document is { IsReadOnly: false });
        SelectionText = selected.Count == 0 ? "Click art pieces to select them" : $"{selected.Count} selected";
        OnPropertyChanged(nameof(CanTrackSelected));
        OnPropertyChanged(nameof(CanAddSelectedToCollection));
        OnPropertyChanged(nameof(TrackSelectedText));
        OnPropertyChanged(nameof(AddSelectedText));
    }

    [RelayCommand]
    private void ToggleSelecting() => IsSelecting = !IsSelecting;

    [RelayCommand]
    private void SelectAll()
    {
        var allVisibleSelected = Items.All(i => i.IsSelected);
        foreach (var i in Items) i.IsSelected = !allVisibleSelected;
        UpdateSelection();
    }

    [RelayCommand]
    private void TrackSelected() => Ui.Fire(TrackSelectedAsync);

    private async Task TrackSelectedAsync()
    {
        var folders = Selected.Where(i => !i.Folder.IsAdopted && i.Folder.Error is null).Select(i => i.Folder).ToList();
        if (folders.Count == 0) return;
        var ok = await Dialogs.ConfirmAsync($"Start tracking {folders.Count} folder{(folders.Count == 1 ? "" : "s")}?",
            "ArtGander adds a hidden .artgander.json details file to each folder. Your files are not renamed or moved.", "Start tracking");
        if (!ok) return;

        var failed = new List<string>();
        using (_app.SuspendWatching())
        {
            foreach (var folder in folders)
            {
                try { _pieces.AdoptFolder(folder.FolderPath, _app.Settings); }
                catch (Exception ex) { failed.Add($"{folder.FolderName}: {ex.Message}"); }
            }
        }
        await _app.RefreshAsync();
        UpdateSelection(); // the same tiles stay selected, now tracked, ready for "Add to collection"
        if (failed.Count > 0) await Dialogs.AlertAsync("Some folders were not tracked", string.Join(Environment.NewLine, failed));
    }

    [RelayCommand]
    private void AddSelectedToCollection() => Ui.Fire(AddSelectedToCollectionAsync);

    private async Task AddSelectedToCollectionAsync()
    {
        var pieces = Selected.Where(i => i.Folder.Document is { IsReadOnly: false }).ToList();
        if (pieces.Count == 0) return;

        const string newCollection = "New collection…";
        var options = _app.AllCollections.Append(newCollection).ToArray();
        var choice = await Dialogs.ChooseAsync($"Add {pieces.Count} art piece{(pieces.Count == 1 ? "" : "s")} to collection", options);
        if (string.IsNullOrEmpty(choice) || choice == "Cancel") return;
        if (choice == newCollection)
        {
            choice = await Dialogs.PromptAsync("New collection", "Name of the collection:", accept: "Add");
            if (string.IsNullOrWhiteSpace(choice)) return;
            choice = choice.Trim();
            if (!_app.Settings.Collections.Any(c => NameRules.NamesEqual(c.Name, choice)))
            {
                _app.Settings.Collections.Add(new Core.Model.CollectionInfo { Name = choice });
                _app.SaveSettings();
            }
        }

        var failed = new List<string>();
        var added = 0;
        using (_app.SuspendWatching())
        {
            foreach (var piece in pieces)
            {
                try { if (_pieces.AddToCollection(piece.Folder.FolderPath, choice)) added++; }
                catch (Exception ex) { failed.Add($"{piece.Title}: {ex.Message}"); }
            }
        }
        IsSelecting = false;
        await _app.RefreshAsync();
        if (failed.Count > 0) await Dialogs.AlertAsync("Some pieces were not updated", string.Join(Environment.NewLine, failed));
        else Summary = $"Added {added} art piece{(added == 1 ? "" : "s")} to \"{choice}\" · " + Summary;
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnSelectedCollectionChanged(string value) => ApplyFilter();
    partial void OnAttentionOnlyChanged(bool value) => ApplyFilter();

    public void ShowCollection(string collection)
    {
        if (!CollectionFilters.Contains(collection)) CollectionFilters.Add(collection);
        SearchText = "";
        SelectedCollection = collection;
    }

    private List<PieceItemViewModel> _all = [];

    private void Rebuild()
    {
        var previous = _all.ToDictionary(i => i.Folder.FolderPath, StringComparer.Ordinal);
        _all = _app.Pieces.Select(p =>
        {
            var vm = new PieceItemViewModel(p, _app.Currency) { IsSelecting = IsSelecting };
            if (previous.TryGetValue(p.FolderPath, out var prev)) vm.IsSelected = prev.IsSelected; // keep selection across refreshes
            if (previous.TryGetValue(p.FolderPath, out var old) && old.PreviewSource == p.PreviewFilePath) vm.Thumbnail = old.Thumbnail;
            else _ = vm.LoadThumbnailAsync(_thumbs);
            return vm;
        }).ToList();

        var selected = SelectedCollection;
        CollectionFilters.Clear();
        CollectionFilters.Add(AllCollections);
        foreach (var c in _app.AllCollections) CollectionFilters.Add(c);
        SelectedCollection = CollectionFilters.Contains(selected) ? selected : AllCollections;

        var attention = _app.Pieces.Count(p => p.NeedsAttention);
        Summary = $"{_app.Pieces.Count} art piece{(_app.Pieces.Count == 1 ? "" : "s")}" + (attention > 0 ? $" · {attention} need attention" : "");
        ApplyFilter();
        UpdateSelection();
        OnPropertyChanged(nameof(HasRoot));
    }

    private void ApplyFilter()
    {
        var query = SearchText.Trim();
        var filtered = _all.Where(i =>
            (query.Length == 0 || i.Matches(query))
            && (SelectedCollection == AllCollections || i.InCollection(SelectedCollection))
            && (!AttentionOnly || i.NeedsAttention));

        Items.Clear();
        foreach (var item in filtered) Items.Add(item);
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(NoMatches));
    }

    [RelayCommand]
    private async Task ChooseFolderAsync()
    {
        var path = await _platform.PickFolderAsync();
        if (path is not null) await _app.OpenAsync(path);
    }

    [RelayCommand]
    private Task RefreshAsync() => _app.RefreshAsync();

    [RelayCommand]
    private async Task NewPieceAsync()
    {
        if (!_app.HasRoot) { await ChooseFolderAsync(); if (!_app.HasRoot) return; }
        var title = await Dialogs.PromptAsync("New art piece", "What is the title of the new piece?", accept: "Create", placeholder: "e.g. Goose On The Loose");
        if (string.IsNullOrWhiteSpace(title)) return;
        try
        {
            using (_app.SuspendWatching())
            {
                var doc = _pieces.CreatePiece(_app.RootPath!, title, _app.Settings);
                await _app.RefreshAsync();
                await OpenPieceAsync(doc.FolderPath);
            }
        }
        catch (Exception ex) { await Dialogs.ErrorAsync(ex); }
    }

    [RelayCommand]
    private void Open(PieceItemViewModel? item) => Ui.Fire(() => OpenAsync(item));

    private async Task OpenAsync(PieceItemViewModel? item)
    {
        if (item is null) return;
        if (IsSelecting)
        {
            item.IsSelected = !item.IsSelected;
            UpdateSelection();
            return;
        }
        if (!item.Folder.IsAdopted)
        {
            if (item.Folder.Error is { } error)
            {
                await Dialogs.AlertAsync("Can't open this piece", $"{error}\n\nFix or remove the hidden .artgander.json file in:\n{item.Folder.FolderPath}");
                return;
            }
            var files = item.Folder.UntrackedFiles.Count;
            var ok = await Dialogs.ConfirmAsync("Start tracking this folder?",
                $"\"{item.Folder.FolderName}\" has no ArtGander details yet ({files} file{(files == 1 ? "" : "s")}). " +
                "ArtGander will add a hidden .artgander.json details file to it. Your files are not renamed or moved.", "Track folder");
            if (!ok) return;
            try
            {
                using (_app.SuspendWatching()) _pieces.AdoptFolder(item.Folder.FolderPath, _app.Settings);
                await _app.RefreshAsync();
            }
            catch (Exception ex) { await Dialogs.ErrorAsync(ex); return; }
        }
        await OpenPieceAsync(item.Folder.FolderPath);
    }

    public static Task OpenPieceAsync(string folderPath, string? tab = null) =>
        Shell.Current.GoToAsync(AppShell.PieceRoute, new ShellNavigationQueryParameters
        {
            ["folder"] = folderPath,
            ["tab"] = tab ?? "overview",
        });
}

public sealed partial class PieceItemViewModel(PieceFolder folder, string currency) : ObservableObject
{
    public PieceFolder Folder { get; } = folder;
    public string Title => Folder.Title;
    public string? PreviewSource => Folder.PreviewFilePath;

    [ObservableProperty] public partial ImageSource? Thumbnail { get; set; }
    [ObservableProperty] public partial bool IsSelected { get; set; }
    [ObservableProperty] public partial bool IsSelecting { get; set; }

    public bool NeedsAttention => Folder.NeedsAttention;

    public string Subtitle
    {
        get
        {
            if (Folder.Error is not null) return "Can't read its details file";
            if (!Folder.IsAdopted) return "Not tracked yet: click to start";
            var p = Folder.Document!.Piece;
            var parts = new List<string>();
            if (p.Collections.Count > 0) parts.Add(string.Join(", ", p.Collections));
            parts.Add($"{p.Files.Count} file{(p.Files.Count == 1 ? "" : "s")}");
            if (p.Products.Count > 0) parts.Add($"{p.Products.Count} product{(p.Products.Count == 1 ? "" : "s")}");
            return string.Join(" · ", parts);
        }
    }

    public string AttentionText
    {
        get
        {
            if (Folder.Error is not null) return "⚠ Error";
            if (!Folder.IsAdopted) return "";
            var parts = new List<string>();
            if (Folder.UntrackedFiles.Count > 0) parts.Add($"{Folder.UntrackedFiles.Count} new");
            if (Folder.MissingFiles.Count > 0) parts.Add($"{Folder.MissingFiles.Count} missing");
            if (Folder.MisnamedFiles.Count > 0) parts.Add($"{Folder.MisnamedFiles.Count} misnamed");
            return parts.Count == 0 ? "" : "⚠ " + string.Join(", ", parts);
        }
    }

    public string StatusText
    {
        get
        {
            var p = Folder.Document?.Piece;
            if (p is null) return "";
            var sold = p.Sales.Sum(s => s.Quantity);
            return sold > 0 ? $"{sold} sold" : "";
        }
    }

    public string Currency { get; } = currency;

    public bool Matches(string query)
    {
        if (Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)) return true;
        var p = Folder.Document?.Piece;
        if (p is null) return Folder.FolderName.Contains(query, StringComparison.CurrentCultureIgnoreCase);
        return p.Description.Contains(query, StringComparison.CurrentCultureIgnoreCase)
               || p.Tags.Any(t => t.Contains(query, StringComparison.CurrentCultureIgnoreCase))
               || p.Collections.Any(c => c.Contains(query, StringComparison.CurrentCultureIgnoreCase))
               || p.Products.Any(pr => pr.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase));
    }

    public bool InCollection(string collection) =>
        Folder.Document?.Piece.Collections.Any(c => NameRules.NamesEqual(c, collection)) == true;

    public async Task LoadThumbnailAsync(ThumbnailService thumbs)
    {
        var path = await thumbs.GetAsync(PreviewSource);
        if (path is not null) MainThread.BeginInvokeOnMainThread(() => Thumbnail = ImageSource.FromFile(path));
    }
}

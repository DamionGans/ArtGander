using System.Collections.ObjectModel;
using ArtGander.App.Services;
using ArtGander.Core.Library;
using ArtGander.Core.Model;
using ArtGander.Core.Naming;
using ArtGander.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ArtGander.App.ViewModels;

/// <summary>
/// The library grid: art pieces and projects. Pieces inside a project are shown when you open the project,
/// and also appear in search results (labeled with their project).
/// </summary>
public sealed partial class LibraryViewModel : ObservableObject
{
    public const string AllCollections = "All collections";
    public static IReadOnlyList<string> TypeFilters { get; } = (string[])["Pieces & projects", "Art pieces", "Projects"];
    public static IReadOnlyList<string> ViewModes { get; } = (string[])["Raster", "Large rows", "Details"];
    public const string AnyStatus = "Any status";
    public static IReadOnlyList<string> StatusFilters { get; } =
        [AnyStatus, "Not tracked", .. Enum.GetValues<WorkStatus>().Select(s => Converters.HumanizeConverter.Humanize(s.ToString()))];
    public static IReadOnlyList<string> SortOptions { get; } = (string[])["Title", "Type", "Status", "Collections", "Files", "Products", "Sold", "Created", "Changed"];

    private readonly AppState _app;
    private readonly PieceService _pieces;
    private readonly IPlatformServices _platform;
    private readonly ThumbnailService _thumbs;
    private List<PieceItemViewModel> _all = [];

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
    [ObservableProperty] public partial string TypeFilter { get; set; } = TypeFilters[0];
    [ObservableProperty] public partial bool AttentionOnly { get; set; }
    [ObservableProperty] public partial string Summary { get; set; } = "";

    public bool IsScanning => _app.IsScanning;
    public bool HasRoot => _app.HasRoot;
    public bool IsEmpty => _app.HasRoot && !_app.IsScanning && _app.Pieces.Count == 0;
    public bool NoMatches => _app.Pieces.Count > 0 && Items.Count == 0;

    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnSelectedCollectionChanged(string value) => ApplyFilter();
    partial void OnTypeFilterChanged(string value) => ApplyFilter();
    partial void OnAttentionOnlyChanged(bool value) => ApplyFilter();

    public void ShowCollection(string collection)
    {
        if (!CollectionFilters.Contains(collection)) CollectionFilters.Add(collection);
        SearchText = "";
        SelectedCollection = collection;
    }

    // ---- Views: raster, large rows, details -----------------------------------------------------------

    /// <summary>How the library is shown; remembered on this computer.</summary>
    [ObservableProperty] public partial string ViewMode { get; set; } = Preferences.Default.Get("libraryView", "Raster");
    [ObservableProperty] public partial string StatusFilter { get; set; } = AnyStatus;
    [ObservableProperty] public partial string SortBy { get; set; } = Preferences.Default.Get("librarySort", "Title");
    [ObservableProperty] public partial bool SortDescending { get; set; } = Preferences.Default.Get("librarySortDesc", false);

    /// <summary>The raster's row height (the "zoom"); remembered on this computer.</summary>
    [ObservableProperty] public partial double RasterHeight { get; set; } = Preferences.Default.Get("rasterHeight", 200.0);

    /// <summary>The width the raster can use, set by the page whenever the window resizes.</summary>
    [ObservableProperty] public partial double AvailableWidth { get; set; }

    public bool IsRaster => ViewMode == ViewModes[0];
    public bool IsRows => ViewMode == ViewModes[1];
    public bool IsDetails => ViewMode == ViewModes[2];

    public ObservableCollection<RasterRow> RasterRows { get; } = [];

    public const double RasterSpacing = 8;
    private IDispatcherTimer? _relayoutTimer;

    partial void OnViewModeChanged(string value)
    {
        Preferences.Default.Set("libraryView", value);
        OnPropertyChanged(nameof(IsRaster));
        OnPropertyChanged(nameof(IsRows));
        OnPropertyChanged(nameof(IsDetails));
        RelayoutRaster();
    }

    partial void OnStatusFilterChanged(string value) => ApplyFilter();

    partial void OnSortByChanged(string value)
    {
        Preferences.Default.Set("librarySort", value);
        ApplyFilter();
        OnPropertyChanged(nameof(SortIndicator));
    }

    partial void OnSortDescendingChanged(bool value)
    {
        Preferences.Default.Set("librarySortDesc", value);
        ApplyFilter();
        OnPropertyChanged(nameof(SortIndicator));
    }

    partial void OnRasterHeightChanged(double value)
    {
        Preferences.Default.Set("rasterHeight", value);
        ScheduleRelayout();
    }

    partial void OnAvailableWidthChanged(double value) => ScheduleRelayout();

    /// <summary>"Title ▲" etc., for the details table header.</summary>
    public string SortIndicator => SortBy + (SortDescending ? " ▼" : " ▲");

    /// <summary>Clicking a column header sorts by it; clicking it again reverses the order.</summary>
    [RelayCommand]
    private void SortColumn(string column)
    {
        if (SortBy == column) SortDescending = !SortDescending;
        else { SortDescending = column is "Sold" or "Products" or "Files" or "Created" or "Changed"; SortBy = column; }
    }

    [RelayCommand]
    private void ToggleSortDirection() => SortDescending = !SortDescending;

    [RelayCommand]
    private void SetViewMode(string mode) => ViewMode = mode;

    private IEnumerable<PieceItemViewModel> Sort(IEnumerable<PieceItemViewModel> items)
    {
        var natural = NameRules.NaturalComparer;
        IOrderedEnumerable<PieceItemViewModel> sorted = SortBy switch
        {
            "Type" => items.OrderBy(i => i.TypeText),
            "Status" => items.OrderBy(i => i.StatusOrder),
            "Collections" => items.OrderBy(i => i.CollectionsText.Length == 0).ThenBy(i => i.CollectionsText, StringComparer.CurrentCultureIgnoreCase),
            "Files" => items.OrderBy(i => i.FileCount),
            "Products" => items.OrderBy(i => i.Stats.Products),
            "Sold" => items.OrderBy(i => i.Stats.Sold),
            "Created" => items.OrderBy(i => i.Created ?? DateOnly.MinValue),
            "Changed" => items.OrderBy(i => i.Changed ?? DateTimeOffset.MinValue),
            _ => items.OrderBy(i => i.Title, natural),
        };
        var list = sorted.ThenBy(i => i.Title, natural).ToList();
        if (SortDescending) list.Reverse();
        return list;
    }

    private void ScheduleRelayout()
    {
        if (Application.Current?.Dispatcher is not { } dispatcher) { RelayoutRaster(); return; }
        _relayoutTimer ??= dispatcher.CreateTimer();
        _relayoutTimer.Interval = TimeSpan.FromMilliseconds(120);
        _relayoutTimer.IsRepeating = false;
        _relayoutTimer.Tick -= OnRelayoutTick;
        _relayoutTimer.Tick += OnRelayoutTick;
        _relayoutTimer.Stop();
        _relayoutTimer.Start();
    }

    private void OnRelayoutTick(object? sender, EventArgs e) => RelayoutRaster();

    /// <summary>Packs the visible items into rows that fill the width, each tile in its image's own shape.</summary>
    private void RelayoutRaster()
    {
        if (!IsRaster || AvailableWidth <= 0) { RasterRows.Clear(); return; }
        var items = Items.ToList();
        var rows = JustifiedLayout.Rows(items.Select(i => i.Aspect).ToList(), AvailableWidth, RasterHeight, RasterSpacing);
        RasterRows.Clear();
        foreach (var row in rows)
            RasterRows.Add(new RasterRow(row.Select(c => new RasterCell(items[c.Index], c.Width, c.Height)).ToList()));
    }

    // ---- Building the grid -------------------------------------------------------------------------

    private PieceStats StatsFor(PieceFolder folder, IReadOnlyList<SaleRow> rows)
    {
        if (folder.Document?.Piece is not { } piece) return PieceStats.Empty;
        var ids = folder.Children.Select(c => c.Document?.Piece.Id).OfType<Guid>().Prepend(piece.Id).ToList();
        var products = _app.Catalog.ForPiece(piece.Id);
        var sold = rows.Where(r => ids.Any(id => r.Line.PieceId == id || (r.Product is { } pr && ProductCatalog.Shows(pr, id)))).Sum(r => r.Line.Quantity);
        return new PieceStats(products.Count, sold, products.Select(pr => pr.Name).ToList());
    }

    private void Rebuild()
    {
        var previous = _all.ToDictionary(i => i.Folder.FolderPath, StringComparer.Ordinal);
        var rows = _app.SaleRows;
        _all = _app.AllFolders.Select(p =>
        {
            var vm = new PieceItemViewModel(p, StatsFor(p, rows)) { IsSelecting = IsSelecting };
            if (previous.TryGetValue(p.FolderPath, out var prev))
            {
                vm.IsSelected = prev.IsSelected; // keep the selection across refreshes
                if (prev.PreviewSource == p.PreviewFilePath) { vm.Thumbnail = prev.Thumbnail; vm.Aspect = prev.Aspect; }
            }
            if (vm.Thumbnail is null) _ = vm.LoadThumbnailAsync(_thumbs);
            vm.AspectChanged += (_, _) => { if (IsRaster) ScheduleRelayout(); };
            return vm;
        }).ToList();

        var selected = SelectedCollection;
        CollectionFilters.Clear();
        CollectionFilters.Add(AllCollections);
        foreach (var c in _app.AllCollections) CollectionFilters.Add(c);
        SelectedCollection = CollectionFilters.Contains(selected) ? selected : AllCollections;

        var projects = _app.Pieces.Count(p => p.IsProject);
        var pieces = _app.AllFolders.Count(p => !p.IsProject);
        var attention = _app.Pieces.Count(p => p.NeedsAttention);
        Summary = $"{pieces} art piece{(pieces == 1 ? "" : "s")}"
                  + (projects > 0 ? $" · {projects} project{(projects == 1 ? "" : "s")}" : "")
                  + (attention > 0 ? $" · {attention} need attention" : "");
        ApplyFilter();
        UpdateSelection();
        OnPropertyChanged(nameof(HasRoot));
    }

    private void ApplyFilter()
    {
        var query = SearchText.Trim();
        var filtered = _all.Where(i =>
            // Pieces inside projects only show up when searching; otherwise you see them by opening the project.
            (i.Folder.Parent is null || query.Length > 0)
            && (query.Length == 0 || i.Matches(query))
            && (TypeFilter == TypeFilters[0] || (TypeFilter == TypeFilters[2]) == i.Folder.IsProject)
            && (SelectedCollection == AllCollections || i.InCollection(SelectedCollection))
            && (StatusFilter == AnyStatus || i.StatusFilterText == StatusFilter)
            && (!AttentionOnly || i.NeedsAttention));

        Items.Clear();
        foreach (var item in Sort(filtered)) Items.Add(item);
        RelayoutRaster();
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(NoMatches));
    }

    // ---- Selection ---------------------------------------------------------------------------------

    /// <summary>In selection mode, clicking a tile selects it instead of opening it.</summary>
    [ObservableProperty] public partial bool IsSelecting { get; set; }
    [ObservableProperty] public partial string SelectionText { get; set; } = "";
    [ObservableProperty] public partial int UntrackedSelectedCount { get; set; }
    [ObservableProperty] public partial int TrackedSelectedCount { get; set; }
    [ObservableProperty] public partial int MovablePiecesSelectedCount { get; set; }

    public bool CanTrackSelected => UntrackedSelectedCount > 0;
    public bool CanAddSelectedToCollection => TrackedSelectedCount > 0;
    public bool CanGroupSelected => MovablePiecesSelectedCount > 0;
    public string TrackSelectedText => $"Start tracking ({UntrackedSelectedCount})";
    public string AddSelectedText => $"Add to collection… ({TrackedSelectedCount})";
    public string MakeProjectText => $"Make project… ({MovablePiecesSelectedCount})";
    public string MoveToProjectText => $"Move to project… ({MovablePiecesSelectedCount})";

    private IEnumerable<PieceItemViewModel> Selected => _all.Where(i => i.IsSelected);

    /// <summary>Tracked art pieces (not projects) that can be moved into a project.</summary>
    private List<PieceItemViewModel> SelectedMovablePieces =>
        Selected.Where(i => i.Folder.Document is { IsReadOnly: false } && !i.Folder.IsProject).ToList();

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
        MovablePiecesSelectedCount = SelectedMovablePieces.Count;
        SelectionText = selected.Count == 0 ? "Click art pieces or projects to select them" : $"{selected.Count} selected";
        foreach (var name in new[] { nameof(CanTrackSelected), nameof(CanAddSelectedToCollection), nameof(CanGroupSelected),
                     nameof(TrackSelectedText), nameof(AddSelectedText), nameof(MakeProjectText), nameof(MoveToProjectText) })
            OnPropertyChanged(name);
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
        var projects = folders.Count(f => f.SuggestedType == ItemType.Project);
        var pieces = folders.Count - projects;
        var detail = projects == 0 ? "" : $"\n\n{pieces} will be tracked as art piece{(pieces == 1 ? "" : "s")} and {projects} as project{(projects == 1 ? "" : "s")} " +
                                          "(folders with several art subfolders). You can change this later with \"Convert to project\".";
        var ok = await Dialogs.ConfirmAsync($"Start tracking {folders.Count} folder{(folders.Count == 1 ? "" : "s")}?",
            "ArtGander adds a hidden .artgander.json details file to each folder. Your files are not renamed or moved." + detail, "Start tracking");
        if (!ok) return;

        var failed = new List<string>();
        using (_app.SuspendWatching())
        {
            foreach (var folder in folders)
            {
                try { _pieces.AdoptFolder(folder.FolderPath, _app.Settings, folder.SuggestedType); }
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
        var items = Selected.Where(i => i.Folder.Document is { IsReadOnly: false }).ToList();
        if (items.Count == 0) return;

        const string newCollection = "New collection…";
        var options = _app.AllCollections.Append(newCollection).ToArray();
        var choice = await Dialogs.ChooseAsync($"Add {items.Count} item{(items.Count == 1 ? "" : "s")} to collection", options);
        if (string.IsNullOrEmpty(choice) || choice == "Cancel") return;
        if (choice == newCollection)
        {
            choice = await Dialogs.PromptAsync("New collection", "Name of the collection:", accept: "Add");
            if (string.IsNullOrWhiteSpace(choice)) return;
            choice = choice.Trim();
            if (!_app.Settings.Collections.Any(c => NameRules.NamesEqual(c.Name, choice)))
            {
                _app.Settings.Collections.Add(new CollectionInfo { Name = choice });
                _app.SaveSettings();
            }
        }

        var failed = new List<string>();
        var added = 0;
        using (_app.SuspendWatching())
        {
            foreach (var item in items)
            {
                try { if (_pieces.AddToCollection(item.Folder.FolderPath, choice)) added++; }
                catch (Exception ex) { failed.Add($"{item.Title}: {ex.Message}"); }
            }
        }
        IsSelecting = false;
        await _app.RefreshAsync();
        if (failed.Count > 0) await Dialogs.AlertAsync("Some items were not updated", string.Join(Environment.NewLine, failed));
        else Summary = $"Added {added} item{(added == 1 ? "" : "s")} to \"{choice}\" · " + Summary;
    }

    [RelayCommand]
    private void MakeProject() => Ui.Fire(MakeProjectAsync);

    private async Task MakeProjectAsync()
    {
        var pieces = SelectedMovablePieces;
        if (pieces.Count == 0 || !_app.HasRoot) return;
        var title = await Dialogs.PromptAsync("Make project",
            $"The {pieces.Count} selected art piece{(pieces.Count == 1 ? "" : "s")} move into a new project folder. Project title:",
            accept: "Make project", placeholder: "e.g. The Goose Book");
        if (string.IsNullOrWhiteSpace(title)) return;

        string projectFolder;
        using (_app.SuspendWatching())
            projectFolder = _pieces.GroupIntoProject(_app.RootPath!, title, pieces.Select(p => p.Folder.FolderPath).ToList(), _app.Settings).FolderPath;
        IsSelecting = false;
        await _app.RefreshAsync();
        await OpenPieceAsync(projectFolder, "pieces");
    }

    [RelayCommand]
    private void MoveToProject() => Ui.Fire(MoveToProjectAsync);

    private async Task MoveToProjectAsync()
    {
        var pieces = SelectedMovablePieces;
        if (pieces.Count == 0 || !_app.HasRoot) return;
        const string library = "No project (back to the library)";
        var projects = _app.Pieces.Where(p => p is { IsProject: true, Document.IsReadOnly: false }).ToList();
        var choice = await Dialogs.ChooseAsync($"Move {pieces.Count} art piece{(pieces.Count == 1 ? "" : "s")} to",
            projects.Select(p => p.Title).Append(library).ToArray());
        if (string.IsNullOrEmpty(choice) || choice == "Cancel") return;
        var target = choice == library ? _app.RootPath! : projects.First(p => p.Title == choice).FolderPath;

        var failed = new List<string>();
        using (_app.SuspendWatching())
        {
            foreach (var piece in pieces)
            {
                try { _pieces.MovePiece(piece.Folder.Document!, target); }
                catch (Exception ex) { failed.Add($"{piece.Title}: {ex.Message}"); }
            }
        }
        IsSelecting = false;
        await _app.RefreshAsync();
        if (failed.Count > 0) await Dialogs.AlertAsync("Some pieces were not moved", string.Join(Environment.NewLine, failed));
    }

    // ---- Commands ----------------------------------------------------------------------------------

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
        const string piece = "Art piece", project = "Project (e.g. a book: several art pieces together)";
        var kind = await Dialogs.ChooseAsync("Create new", piece, project);
        if (kind is not (piece or project)) return;
        var isProject = kind == project;
        var title = await Dialogs.PromptAsync(isProject ? "New project" : "New art piece",
            isProject ? "What is the title of the new project?" : "What is the title of the new piece?",
            accept: "Create", placeholder: isProject ? "e.g. The Goose Book" : "e.g. Goose On The Loose");
        if (string.IsNullOrWhiteSpace(title)) return;
        try
        {
            using (_app.SuspendWatching())
            {
                var doc = isProject ? _pieces.CreateProject(_app.RootPath!, title, _app.Settings) : _pieces.CreatePiece(_app.RootPath!, title, _app.Settings);
                await _app.RefreshAsync();
                await OpenPieceAsync(doc.FolderPath, isProject ? "pieces" : null);
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
                await Dialogs.AlertAsync("Can't open this item", $"{error}\n\nFix or remove the hidden .artgander.json file in:\n{item.Folder.FolderPath}");
                return;
            }
            var type = await AskTrackTypeAsync(item.Folder);
            if (type is null) return;
            try
            {
                using (_app.SuspendWatching()) _pieces.AdoptFolder(item.Folder.FolderPath, _app.Settings, type);
                await _app.RefreshAsync();
            }
            catch (Exception ex) { await Dialogs.ErrorAsync(ex); return; }
        }
        await OpenPieceAsync(item.Folder.FolderPath);
    }

    /// <summary>Asks how to track a folder, with the smart suggestion first.</summary>
    private static async Task<ItemType?> AskTrackTypeAsync(PieceFolder folder)
    {
        var asPiece = "Track as art piece";
        var asProject = $"Track as project ({folder.ArtSubfolderCount} art subfolder{(folder.ArtSubfolderCount == 1 ? "" : "s")} become its pieces)";
        var suggestedProject = folder.SuggestedType == ItemType.Project;
        string[] options = suggestedProject ? [asProject + " (suggested)", asPiece] : [asPiece + " (suggested)", asProject];
        var files = folder.UntrackedFiles.Count;
        var choice = await Dialogs.ChooseAsync(
            $"Start tracking \"{folder.FolderName}\"? ({files} file{(files == 1 ? "" : "s")}; a hidden details file is added, nothing is renamed or moved)",
            options);
        if (string.IsNullOrEmpty(choice) || choice == "Cancel") return null;
        return choice.StartsWith(asProject, StringComparison.Ordinal) ? ItemType.Project : ItemType.Piece;
    }

    public static Task OpenPieceAsync(string folderPath, string? tab = null) =>
        Shell.Current.GoToAsync(AppShell.PieceRoute, new ShellNavigationQueryParameters
        {
            ["folder"] = folderPath,
            ["tab"] = tab ?? "overview",
        });
}

/// <summary>Product and sales numbers for a library item.</summary>
public sealed record PieceStats(int Products, int Sold, IReadOnlyList<string> ProductNames)
{
    public static PieceStats Empty { get; } = new(0, 0, []);
}

public sealed partial class PieceItemViewModel(PieceFolder folder, PieceStats? stats = null) : ObservableObject
{
    public PieceFolder Folder { get; } = folder;
    public PieceStats Stats { get; } = stats ?? PieceStats.Empty;

    /// <summary>The preview's width divided by its height; 0 until the thumbnail is loaded.</summary>
    public double Aspect { get; set; }
    public event EventHandler? AspectChanged;

    public string TypeText => !Folder.IsAdopted ? "Folder" : Folder.IsProject ? "Project" : "Art piece";
    public string WorkStatusText => Folder.Document?.Piece is { } p ? Converters.HumanizeConverter.Humanize(p.Status.ToString()) : "Not tracked";
    public string StatusFilterText => WorkStatusText;
    public int StatusOrder => Folder.Document?.Piece is { } p ? (int)p.Status : -1;
    public string CollectionsText => Folder.Document?.Piece is { } p ? string.Join(", ", p.Collections) : "";
    public int FileCount => Folder.Document?.Piece.Files.Count ?? Folder.UntrackedFiles.Count;
    public string FilesText => Folder.IsProject ? $"{Folder.Children.Count} pieces" : FileCount.ToString();
    public string ProductsText => Stats.Products > 0 ? Stats.Products.ToString() : "";
    public string SoldText => Stats.Sold > 0 ? Stats.Sold.ToString() : "";
    public DateOnly? Created => Folder.Document?.Piece.CreatedOn;
    public DateTimeOffset? Changed => Folder.Document?.Piece.UpdatedAt;
    public string CreatedText => Created?.ToString("d") ?? "";
    public string ChangedText => Changed?.ToString("d") ?? "";
    public string Description => Folder.Document?.Piece.Description is { Length: > 0 } d ? d.ReplaceLineEndings(" ") : "";
    public string Title => Folder.Parent is null ? Folder.Title : Folder.DisplayPath;
    public string? PreviewSource => Folder.PreviewFilePath;
    public bool IsProject => Folder.IsProject;

    [ObservableProperty] public partial ImageSource? Thumbnail { get; set; }
    [ObservableProperty] public partial bool IsSelected { get; set; }
    [ObservableProperty] public partial bool IsSelecting { get; set; }

    public bool NeedsAttention => Folder.NeedsAttention;

    /// <summary>A short badge in the tile's corner: "PROJECT" or the work status when it isn't finished.</summary>
    public string Badge => Folder.IsProject ? "PROJECT"
        : Folder.Document?.Piece.Status is WorkStatus.Planned or WorkStatus.Idea or WorkStatus.InProgress
            ? Converters.HumanizeConverter.Humanize(Folder.Document.Piece.Status.ToString()).ToUpperInvariant()
            : "";

    public string Subtitle
    {
        get
        {
            if (Folder.Error is not null) return "Can't read its details file";
            if (!Folder.IsAdopted)
                return Folder.SuggestedType == ItemType.Project
                    ? $"Not tracked yet · looks like a project ({Folder.ArtSubfolderCount} pieces)"
                    : "Not tracked yet: click to start";
            var p = Folder.Document!.Piece;
            var parts = new List<string>();
            if (p.IsProject)
            {
                parts.Add($"{Folder.Children.Count} piece{(Folder.Children.Count == 1 ? "" : "s")}");
                parts.Add(Converters.HumanizeConverter.Humanize(p.Status.ToString()));
            }
            if (p.Collections.Count > 0) parts.Add(string.Join(", ", p.Collections));
            if (!p.IsProject) parts.Add($"{p.Files.Count} file{(p.Files.Count == 1 ? "" : "s")}");
            if (Stats.Products > 0) parts.Add($"{Stats.Products} product{(Stats.Products == 1 ? "" : "s")}");
            return string.Join(" · ", parts);
        }
    }

    public string AttentionText
    {
        get
        {
            if (Folder.Error is not null) return "⚠ Error";
            if (!Folder.IsAdopted) return "";
            if (Folder.LooksLikeProject) return "⚠ Looks like a project";
            var parts = new List<string>();
            if (Folder.UntrackedFiles.Count > 0) parts.Add($"{Folder.UntrackedFiles.Count} new");
            if (Folder.MissingFiles.Count > 0) parts.Add($"{Folder.MissingFiles.Count} missing");
            if (Folder.MisnamedFiles.Count > 0) parts.Add($"{Folder.MisnamedFiles.Count} misnamed");
            var childIssues = Folder.Children.Count(c => c.NeedsAttention);
            if (childIssues > 0) parts.Add($"{childIssues} piece{(childIssues == 1 ? "" : "s")}");
            return parts.Count == 0 ? "" : "⚠ " + string.Join(", ", parts);
        }
    }

    public string StatusText
    {
        get
        {
            var p = Folder.Document?.Piece;
            if (p is null) return "";
            return Stats.Sold > 0 ? $"{Stats.Sold} sold" : "";
        }
    }

    public bool Matches(string query)
    {
        if (Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)) return true;
        var p = Folder.Document?.Piece;
        if (p is null) return Folder.FolderName.Contains(query, StringComparison.CurrentCultureIgnoreCase);
        return p.Description.Contains(query, StringComparison.CurrentCultureIgnoreCase)
               || p.Tags.Any(t => t.Contains(query, StringComparison.CurrentCultureIgnoreCase))
               || p.Collections.Any(c => c.Contains(query, StringComparison.CurrentCultureIgnoreCase))
               || Stats.ProductNames.Any(n => n.Contains(query, StringComparison.CurrentCultureIgnoreCase));
    }

    public bool InCollection(string collection) =>
        Folder.Document?.Piece.Collections.Any(c => NameRules.NamesEqual(c, collection)) == true;

    public async Task LoadThumbnailAsync(ThumbnailService thumbs)
    {
        var path = await thumbs.GetAsync(PreviewSource);
        if (path is null) return;
        var size = await Task.Run(() => ImageHeader.TryGetSize(path));
        MainThread.BeginInvokeOnMainThread(() =>
        {
            Thumbnail = ImageSource.FromFile(path);
            if (size is { } sz)
            {
                Aspect = (double)sz.Width / sz.Height;
                AspectChanged?.Invoke(this, EventArgs.Empty);
            }
        });
    }
}

/// <summary>One tile in the raster view, sized to its image's shape.</summary>
public sealed record RasterCell(PieceItemViewModel Item, double Width, double Height);

/// <summary>One row of the raster view.</summary>
public sealed record RasterRow(IReadOnlyList<RasterCell> Cells)
{
    public double Height => Cells.Count == 0 ? 0 : Cells[0].Height;
}

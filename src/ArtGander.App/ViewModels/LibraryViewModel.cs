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
            var vm = new PieceItemViewModel(p, _app.Currency);
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

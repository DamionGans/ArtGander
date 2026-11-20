using System.Collections.ObjectModel;
using ArtGander.App.Services;
using ArtGander.Core.Model;
using ArtGander.Core.Naming;
using ArtGander.Core.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ArtGander.App.ViewModels;

public sealed partial class CollectionRow(string name, string description, int count) : ObservableObject
{
    public string Name { get; } = name;
    [ObservableProperty] public partial string Description { get; set; } = description;
    public int Count { get; } = count;
    public string CountText => Count == 1 ? "1 piece" : $"{Count} pieces";
}

/// <summary>
/// Collections live on each art piece (its <c>collections</c> list). <c>library.json</c> only adds descriptions
/// and remembers collections that are still empty.
/// </summary>
public sealed partial class CollectionsViewModel : ObservableObject
{
    private readonly AppState _app;
    private readonly ArtPieceStore _store;
    private readonly LibraryViewModel _library;

    public CollectionsViewModel(AppState app, ArtPieceStore store, LibraryViewModel library)
    {
        _app = app;
        _store = store;
        _library = library;
        _app.LibraryChanged += (_, _) => Rebuild();
        Rebuild();
    }

    public ObservableCollection<CollectionRow> Collections { get; } = [];

    public void Rebuild()
    {
        Collections.Clear();
        foreach (var name in _app.AllCollections)
        {
            var info = _app.Settings.Collections.FirstOrDefault(c => NameRules.NamesEqual(c.Name, name));
            var count = _app.Documents.Count(d => d.Piece.Collections.Any(c => NameRules.NamesEqual(c, name)));
            Collections.Add(new CollectionRow(name, info?.Description ?? "", count));
        }
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        if (!_app.HasRoot) return;
        var name = await Dialogs.PromptAsync("New collection", "Name of the collection:", accept: "Add");
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim();
        if (!_app.Settings.Collections.Any(c => NameRules.NamesEqual(c.Name, name)))
        {
            _app.Settings.Collections.Add(new CollectionInfo { Name = name });
            Save();
        }
        Rebuild();
    }

    [RelayCommand]
    private void SaveDescription(CollectionRow? row)
    {
        if (row is null) return;
        var info = _app.Settings.Collections.FirstOrDefault(c => NameRules.NamesEqual(c.Name, row.Name));
        if (info is null) _app.Settings.Collections.Add(info = new CollectionInfo { Name = row.Name });
        if (info.Description == row.Description) return;
        info.Description = row.Description;
        Save();
    }

    [RelayCommand]
    private async Task RenameAsync(CollectionRow? row)
    {
        if (row is null) return;
        var newName = await Dialogs.PromptAsync("Rename collection", $"New name for \"{row.Name}\". All {row.CountText} will be updated.", row.Name, "Rename");
        if (string.IsNullOrWhiteSpace(newName) || newName.Trim() == row.Name) return;
        await UpdatePiecesAsync(row.Name, newName.Trim());
        var info = _app.Settings.Collections.FirstOrDefault(c => NameRules.NamesEqual(c.Name, row.Name));
        if (info is not null) info.Name = newName.Trim();
        Save();
        await _app.RefreshAsync();
    }

    [RelayCommand]
    private async Task DeleteAsync(CollectionRow? row)
    {
        if (row is null) return;
        var msg = row.Count > 0 ? $"It is removed from {row.CountText}. The art pieces themselves are kept." : "This collection is empty.";
        if (!await Dialogs.ConfirmAsync($"Delete collection \"{row.Name}\"?", msg, "Delete")) return;
        await UpdatePiecesAsync(row.Name, null);
        var info = _app.Settings.Collections.FirstOrDefault(c => NameRules.NamesEqual(c.Name, row.Name));
        if (info is not null) _app.Settings.Collections.Remove(info);
        Save();
        await _app.RefreshAsync();
    }

    [RelayCommand]
    private async Task ShowAsync(CollectionRow? row)
    {
        if (row is null) return;
        _library.ShowCollection(row.Name);
        await Shell.Current.GoToAsync("//library");
    }

    /// <summary>Renames (or removes, when <paramref name="newName"/> is null) a collection on every piece that uses it.</summary>
    private async Task UpdatePiecesAsync(string oldName, string? newName)
    {
        var failed = new List<string>();
        using (_app.SuspendWatching())
        {
            foreach (var doc in _app.Documents.Where(d => !d.IsReadOnly && d.Piece.Collections.Any(c => NameRules.NamesEqual(c, oldName))).ToList())
            {
                try
                {
                    var fresh = _store.Load(doc.FolderPath); // always edit the latest version on disk
                    var idx = fresh.Piece.Collections.ToList().FindIndex(c => NameRules.NamesEqual(c, oldName));
                    if (idx < 0) continue;
                    if (newName is null) fresh.Piece.Collections.RemoveAt(idx);
                    else fresh.Piece.Collections[idx] = newName;
                    _store.Save(fresh);
                }
                catch (Exception) { failed.Add(doc.Piece.Title); }
            }
        }
        if (failed.Count > 0)
            await Dialogs.AlertAsync("Some pieces were not updated", string.Join("\n", failed));
    }

    private void Save()
    {
        try { _app.SaveSettings(); }
        catch (Exception ex) { _ = Dialogs.ErrorAsync(ex); }
    }
}

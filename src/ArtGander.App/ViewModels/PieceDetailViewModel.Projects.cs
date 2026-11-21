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
/// Project features of the editor: the Pieces tab (order, new and planned pieces, moving pieces in and out),
/// the "Part of" breadcrumb for pieces inside a project, Convert to project, and bundle products.
/// </summary>
public sealed partial class PieceDetailViewModel
{
    public static IReadOnlyList<WorkStatus> WorkStatuses { get; } = Enum.GetValues<WorkStatus>();

    public bool IsProject => Piece?.IsProject == true;
    public bool IsPiece => !IsProject;
    public bool IsPieces => Tab == "pieces";
    public string KindLabel => IsProject ? "project" : "art piece";

    /// <summary>For a piece inside a project: the project's folder and title (the "Part of" breadcrumb).</summary>
    [ObservableProperty] public partial string? ParentFolder { get; private set; }
    [ObservableProperty] public partial string? ParentTitle { get; private set; }
    public bool HasParent => ParentFolder is not null;
    partial void OnParentFolderChanged(string? value) => OnPropertyChanged(nameof(HasParent));

    /// <summary>A stand-alone piece with several art subfolders: offer "Convert to project".</summary>
    [ObservableProperty] public partial bool LooksLikeProject { get; private set; }

    public ObservableCollection<ProjectPieceRow> ProjectPieces { get; } = [];
    [ObservableProperty] public partial string ProjectPiecesSummary { get; set; } = "";

    private IReadOnlyList<PieceFolder> _children = [];

    /// <summary>Called from Bind(): refreshes everything that depends on the item's place in the library.</summary>
    private void BindProject()
    {
        foreach (var name in new[] { nameof(IsProject), nameof(IsPiece), nameof(KindLabel) }) OnPropertyChanged(name);
        if (Doc is null) return;

        var parent = app.FindFolder(Path.GetDirectoryName(Doc.FolderPath)!);
        ParentFolder = parent is { IsProject: true } ? parent.FolderPath : null;
        ParentTitle = parent is { IsProject: true } ? parent.Title : null;
        Doc.ProjectTitle = ParentTitle;
        LooksLikeProject = app.FindFolder(Doc.FolderPath)?.LooksLikeProject == true;
        RebuildProjectPieces();
    }

    public void RebuildProjectPieces()
    {
        ProjectPieces.Clear();
        _children = IsProject && Doc is not null ? app.FindFolder(Doc.FolderPath)?.Children ?? [] : [];
        var position = 1;
        foreach (var child in _children)
        {
            var row = new ProjectPieceRow(child, position++);
            ProjectPieces.Add(row);
            _ = row.LoadThumbnailAsync(thumbs);
        }
        var planned = _children.Count(c => c.Document?.Piece.Status is WorkStatus.Planned or WorkStatus.Idea);
        ProjectPiecesSummary = _children.Count == 0
            ? "No pieces yet. Add new ones, or move existing art pieces into this project."
            : $"{_children.Count} piece{(_children.Count == 1 ? "" : "s")}" + (planned > 0 ? $" · {planned} planned" : "") + " · in this order (e.g. the page order of a book)";
    }

    /// <summary>The project's own sales plus its pieces' sales.</summary>
    private string ProjectRollupText()
    {
        if (!IsProject || Doc is null || _children.Count == 0) return "";
        var ids = _children.Select(c => c.Document?.Piece.Id).OfType<Guid>().Prepend(Doc.Piece.Id).ToList();
        var rows = app.SaleRows.Where(r => ids.Any(id => SalesReport.ForPiece([r], id).Count > 0)).ToList();
        var qty = rows.Sum(r => r.Line.Quantity);
        return qty == 0 ? "" : $"Including its pieces: {qty} sold · revenue {rows.Sum(r => r.Revenue):N2} · profit {rows.Sum(r => r.Profit):N2} {Currency}";
    }

    /// <summary>
    /// Runs an operation that changes the library structure. Unsaved edits are saved first, and the item is
    /// reloaded afterwards, because these operations also update the project's details file on disk.
    /// </summary>
    private async Task ProjectOpAsync(Action op)
    {
        if (Doc is null || Doc.IsReadOnly) return;
        if (HasUnsavedChanges && !await TrySaveAsync()) return;
        try
        {
            using (app.SuspendWatching()) op();
        }
        catch (Exception ex)
        {
            await Dialogs.ErrorAsync(ex);
        }
        await app.RefreshAsync();
        if (Doc is not null && Directory.Exists(Doc.FolderPath)) Load(Doc.FolderPath);
    }

    [RelayCommand]
    private Task OpenParentAsync() => ParentFolder is null ? Task.CompletedTask : LibraryViewModel.OpenPieceAsync(ParentFolder, "pieces");

    [RelayCommand]
    private void OpenChild(ProjectPieceRow? row)
    {
        if (row is not null) Ui.Fire(() => LibraryViewModel.OpenPieceAsync(row.Folder.FolderPath));
    }

    [RelayCommand]
    private void NewChildPiece() => Ui.Fire(() => NewChildAsync(planned: false));

    [RelayCommand]
    private void NewPlannedPiece() => Ui.Fire(() => NewChildAsync(planned: true));

    private async Task NewChildAsync(bool planned)
    {
        if (Doc is null || !IsProject) return;
        var title = await Dialogs.PromptAsync(planned ? "New planned piece" : "New art piece",
            planned ? "A placeholder for a piece that isn't made yet (e.g. a page still to draw). Title:" : "Title of the new piece in this project:",
            accept: "Create", placeholder: planned ? "e.g. Page 12" : "e.g. Goose Wakes Up");
        if (string.IsNullOrWhiteSpace(title)) return;
        await ProjectOpAsync(() => pieces.CreatePieceInProject(Doc, title, app.Settings, planned));
    }

    [RelayCommand]
    private void AddExistingPiece() => Ui.Fire(AddExistingPieceAsync);

    private async Task AddExistingPieceAsync()
    {
        if (Doc is null || !IsProject) return;
        var candidates = app.Pieces.Where(p => p is { IsProject: false, Document.IsReadOnly: false }).ToList();
        if (candidates.Count == 0) { await Dialogs.AlertAsync("No art pieces to add", "All tracked art pieces already belong to a project."); return; }
        var choice = await Dialogs.ChooseAsync("Move an art piece into this project (its folder moves along)", candidates.Select(c => c.Title).ToArray());
        var picked = candidates.FirstOrDefault(c => c.Title == choice);
        if (picked is null) return;
        await ProjectOpAsync(() => pieces.MovePiece(picked.Document!, Doc.FolderPath));
    }

    [RelayCommand]
    private void RemoveFromProject(ProjectPieceRow? row) => Ui.Fire(() => RemoveFromProjectAsync(row));

    private async Task RemoveFromProjectAsync(ProjectPieceRow? row)
    {
        if (row?.Folder.Document is not { } child || !app.HasRoot) return;
        if (!await Dialogs.ConfirmAsync($"Remove \"{row.Title}\" from this project?",
                "Its folder moves back to the art folder as a stand-alone art piece. Nothing is deleted.", "Move out")) return;
        await ProjectOpAsync(() => pieces.MovePiece(child, app.RootPath!));
    }

    [RelayCommand]
    private void MoveUp(ProjectPieceRow? row) => Ui.Fire(() => MoveAsync(row, -1));

    [RelayCommand]
    private void MoveDown(ProjectPieceRow? row) => Ui.Fire(() => MoveAsync(row, +1));

    private async Task MoveAsync(ProjectPieceRow? row, int delta)
    {
        if (row is null || Doc is null) return;
        var order = ProjectPieces.Select(r => r.Id).OfType<Guid>().ToList();
        if (row.Id is not { } id) return;
        var i = order.IndexOf(id);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= order.Count) return;
        (order[i], order[j]) = (order[j], order[i]);
        await ProjectOpAsync(() => pieces.ReorderPieces(Doc, order));
    }

    [RelayCommand]
    private void ConvertToProject() => Ui.Fire(ConvertToProjectAsync);

    private async Task ConvertToProjectAsync()
    {
        if (Doc is null || IsProject) return;
        if (!await Dialogs.ConfirmAsync("Convert to project?",
                "This item becomes a project. Its own files become project files, and its art subfolders are tracked as the project's pieces. " +
                "Nothing is renamed or moved.", "Convert")) return;
        await ProjectOpAsync(() => pieces.ConvertToProject(Doc, app.Settings));
        Tab = "pieces";
    }

}

/// <summary>A piece on a project's Pieces tab.</summary>
public sealed partial class ProjectPieceRow(PieceFolder folder, int position) : ObservableObject
{
    public PieceFolder Folder { get; } = folder;
    public int Position { get; } = position;
    public Guid? Id => Folder.Document?.Piece.Id;
    public string Title => Folder.Title;

    public string StatusText => Folder.Document?.Piece is { } p
        ? Converters.HumanizeConverter.Humanize(p.Status.ToString()) + $" · {p.Files.Count} file{(p.Files.Count == 1 ? "" : "s")}"
        : "Not tracked yet: open to start";

    [ObservableProperty] public partial ImageSource? Thumbnail { get; set; }

    public async Task LoadThumbnailAsync(ThumbnailService thumbs)
    {
        var path = await thumbs.GetAsync(Folder.PreviewFilePath);
        if (path is not null) MainThread.BeginInvokeOnMainThread(() => Thumbnail = ImageSource.FromFile(path));
    }
}

using System.Collections.ObjectModel;
using ArtGander.App.Services;
using ArtGander.Core.Imaging;
using ArtGander.Core.Model;
using ArtGander.Core.Naming;
using ArtGander.Core.Services;
using CommunityToolkit.Mvvm.Input;

namespace ArtGander.App.ViewModels;

/// <summary>
/// The cover of a piece or project: the picture named after it without a version ({Art_Piece_Name}.{ext}), listed on
/// its own. New versions are made from it: a copy, or a CMYK, Adobe RGB or sRGB version.
/// </summary>
public sealed partial class PieceDetailViewModel
{
    public ObservableCollection<CoverRow> Covers { get; } = [];
    public bool HasCovers => Covers.Count > 0;

    /// <summary>A file named after the piece without a version, that is a picture.</summary>
    private bool IsCoverFile(string fileName) =>
        Doc is not null && FileNamer.IsCoverName(Doc.FolderName, fileName) && IsPicture(fileName);

    private static bool IsPicture(string fileName) =>
        ProductImageService.IsImage(fileName) || Path.GetExtension(fileName).ToLowerInvariant() is ".psd" or ".psb";

    private void RebuildCovers(IEnumerable<string> fileNames)
    {
        var previous = Covers.ToList();
        Covers.Clear();
        if (Doc is null) return;
        foreach (var name in fileNames.Where(IsCoverFile).Distinct(NameRules.Comparer).Order(NameRules.NaturalComparer))
        {
            var row = new CoverRow(Doc.GetFilePath(name));
            row.CarryOverCover(previous.FirstOrDefault(p => NameRules.NamesEqual(p.FullPath, row.FullPath)));
            row.RefreshProtectedCopy();
            Covers.Add(row);
            _ = row.LoadThumbnailAsync(thumbs);
        }
        OnPropertyChanged(nameof(HasCovers));
    }

    private CoverRow? CoverFor(string path) => Covers.FirstOrDefault(c => NameRules.NamesEqual(c.FullPath, path));

    [RelayCommand]
    private void AddCoverVersion(CoverRow? row) => Ui.Fire(() => AddCoverVersionAsync(row));

    private async Task AddCoverVersionAsync(CoverRow? row)
    {
        if (row is null || Doc is not { IsReadOnly: false } doc || !row.CanMake) return;
        var path = row.FullPath;
        var target = row.Target.Target;
        var label = row.LabelToUse;
        row.IsMaking = true;
        row.VersionMessage = target == ColorTarget.Copy ? "Copying…" : $"Making the {ColorVersions.Title(target)}…";
        string message;
        try
        {
            var (temp, note) = await CoverRow.MakeAsync(colors, app.RootPath, app.Settings.Colors, path, target);
            var kind = ColorVersions.KindFor(target, label, app.Settings.FileKinds);
            FileEntry? added = null;
            await FileOpAsync(() => added = pieces.ImportFile(doc, temp, label, kind, move: true, notes: note));
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
            message = added is null ? "The new version couldn't be added." : $"Added {added.FileName} (in Files below).";
            if (added is not null && CoverFor(path) is { } now) now.NewVersion = "";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            message = "Not made: " + ex.Message;
        }
        var current = CoverFor(path) ?? row;
        current.IsMaking = false;
        current.VersionMessage = message;
    }

    [RelayCommand]
    private void OpenCover(CoverRow? row) => Ui.Fire(() => OpenFilePathAsync(row?.FullPath));

    [RelayCommand]
    private void ShowCover(CoverRow? row)
    {
        if (row is not null && File.Exists(row.FullPath)) platform.RevealInFileManager(row.FullPath);
    }
}

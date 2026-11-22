using ArtGander.App.Services;
using ArtGander.Core.Integrations;
using ArtGander.Core.Model;
using CommunityToolkit.Mvvm.Input;

namespace ArtGander.App.ViewModels;

/// <summary>Protecting a piece's images against AI training with Mistcoat. The protected copy becomes a new file of the piece.</summary>
public sealed partial class PieceDetailViewModel
{
    /// <summary>The row currently shown for a file. Rows are rebuilt after file operations, so a running protection looks it up again.</summary>
    private FileRowViewModel? RowFor(string fileName) =>
        Files.FirstOrDefault(r => string.Equals(r.Entry.FileName, fileName, StringComparison.OrdinalIgnoreCase));

    private static void CarryOver(FileRowViewModel? from, FileRowViewModel to) => to.CarryOverFrom(from);

    [RelayCommand]
    private void QuickProtect(FileRowViewModel? row) => Ui.Fire(() =>
        row is null || Doc is null ? Task.CompletedTask
            : ProtectAsync(row, protection.QuickOptions(Doc.Piece), Path.GetExtension(row.FullPath), ProtectionService.ProtectedVersion(row.Entry.Version)));

    [RelayCommand]
    private void ShowProtectOptions(FileRowViewModel? row)
    {
        if (row is null || Doc is null) return;
        row.Advanced = row.Advanced is null
            ? new ProtectOptionsViewModel(Path.GetExtension(row.FullPath), protection.QuickOptions(Doc.Piece), ProtectionService.ProtectedVersion(row.Entry.Version))
            : null;
    }

    [RelayCommand]
    private void ProtectWithOptions(FileRowViewModel? row) => Ui.Fire(() =>
    {
        if (row?.Advanced is not { } options) return Task.CompletedTask;
        var version = string.IsNullOrWhiteSpace(options.Version) ? ProtectionService.ProtectedVersion(row.Entry.Version) : options.Version;
        return ProtectAsync(row, options.ToOptions(), options.OutputExtension, version);
    });

    [RelayCommand]
    private void CancelProtect(FileRowViewModel? row) => row?.ProtectCancel?.Cancel();

    /// <summary>Opens Mistcoat's own window with this image, to check the preview there and save it from Mistcoat.</summary>
    [RelayCommand]
    private void OpenInMistcoat(FileRowViewModel? row) => Ui.Fire(() => row is null ? Task.CompletedTask : OpenInMistcoatAsync(protection, row));

    /// <summary>Opens Mistcoat's window with the row's image (shared by the piece and product pages).</summary>
    public static async Task OpenInMistcoatAsync(ProtectionService protection, ProtectableRow row)
    {
        if (await protection.FindAsync() is not { } runner) return;
        if (!runner.HasWindow)
        {
            await Dialogs.AlertAsync("Mistcoat window not available", "The Mistcoat program set in Settings is the command-line version, which has no window. Choose Mistcoat.exe (or Mistcoat.app) in Settings to use its window.");
            return;
        }
        try { runner.OpenWindow([row.FullPath]); }
        catch (Exception ex) { await Dialogs.ErrorAsync(ex); return; }
        row.ProtectMessage = "Opened in Mistcoat. It saves into a \"Protected\" folder next to the file; add the result here with Add… (or drop it on this page).";
    }

    private Task ProtectAsync(FileRowViewModel row, MistcoatOptions options, string extension, string version)
    {
        if (Doc is not { IsReadOnly: false } doc) return Task.CompletedTask;
        var fileName = row.Entry.FileName;
        return protection.RunAsync(row, () => RowFor(fileName), options, extension, async (output, result) =>
        {
            var notes = ProtectionService.NotesFor(options, fileName);
            FileEntry? added = null;
            if (ReferenceEquals(Doc, doc))
                await FileOpAsync(() => added = pieces.ImportFile(doc, output, version, FileKind.Protected, move: true, notes: notes));
            else
                using (app.SuspendWatching()) added = pieces.ImportFile(doc, output, version, FileKind.Protected, move: true, notes: notes);
            if (added is null) return "Mistcoat finished, but the protected copy couldn't be added to this piece.";
            if (RowFor(fileName) is { } r) r.Advanced = null;
            return ProtectionService.SavedMessage(added.FileName, result);
        });
    }
}

using ArtGander.App.Services;
using ArtGander.Core.Integrations;
using ArtGander.Core.Services;
using CommunityToolkit.Mvvm.Input;

namespace ArtGander.App.ViewModels;

/// <summary>
/// Protecting a piece's images against AI training with Mistcoat. The protected copy goes in the Protected folder
/// of the piece (<c>Protected/{name}-protected.{ext}</c>); the original stays as it is. This works for the tracked files
/// and for the original photographs in the Photograph folder.
/// </summary>
public sealed partial class PieceDetailViewModel
{
    /// <summary>The row currently shown for a file. Rows are rebuilt after file operations, so a running protection looks it up again.</summary>
    private FileRowViewModel? RowFor(string fileName) =>
        Files.FirstOrDefault(r => string.Equals(r.Entry.FileName, fileName, StringComparison.OrdinalIgnoreCase));

    /// <summary>The row currently shown for the same file as <paramref name="row"/> (a tracked file or a photograph).</summary>
    private ProtectableRow? RowNow(ProtectableRow row) => row is FileRowViewModel f
        ? RowFor(f.Entry.FileName)
        : Photographs.FirstOrDefault(p => string.Equals(p.FullPath, row.FullPath, StringComparison.OrdinalIgnoreCase));

    private static void CarryOver(FileRowViewModel? from, FileRowViewModel to)
    {
        to.CarryOverFrom(from);
        to.RefreshProtectedCopy();
    }

    [RelayCommand]
    private void QuickProtect(ProtectableRow? row) => Ui.Fire(() =>
        row is null || Doc is null ? Task.CompletedTask
            : ProtectRowAsync(protection, row, () => RowNow(row), protection.QuickOptions(Doc.Piece), Path.GetExtension(row.FullPath)));

    [RelayCommand]
    private void ShowProtectOptions(ProtectableRow? row)
    {
        if (row is null || Doc is null) return;
        row.Advanced = row.Advanced is null ? new ProtectOptionsViewModel(Path.GetExtension(row.FullPath), protection.QuickOptions(Doc.Piece), "") : null;
    }

    [RelayCommand]
    private void ProtectWithOptions(ProtectableRow? row) => Ui.Fire(() =>
        row?.Advanced is not { } options ? Task.CompletedTask
            : ProtectRowAsync(protection, row, () => RowNow(row), options.ToOptions(), options.OutputExtension));

    [RelayCommand]
    private void CancelProtect(ProtectableRow? row) => row?.ProtectCancel?.Cancel();

    [RelayCommand]
    private void OpenInMistcoat(ProtectableRow? row) => Ui.Fire(() => row is null ? Task.CompletedTask : OpenInMistcoatAsync(protection, row));

    [RelayCommand]
    private void OpenProtectedCopy(ProtectableRow? row) => Ui.Fire(() => OpenProtectedCopyAsync(row));

    [RelayCommand]
    private void ShowProtectedCopy(ProtectableRow? row) => ShowProtectedCopy(platform, row);

    [RelayCommand]
    private void OpenPhotograph(SpecialFileRow? row) => Ui.Fire(() => OpenFilePathAsync(row?.FullPath));

    [RelayCommand]
    private void ShowPhotograph(SpecialFileRow? row)
    {
        if (row is not null && File.Exists(row.FullPath)) platform.RevealInFileManager(row.FullPath);
    }

    // ---- Shared with the product page ----

    public static async Task OpenFilePathAsync(string? path)
    {
        if (path is null || !File.Exists(path)) return;
        try { await Launcher.Default.OpenAsync(new OpenFileRequest(Path.GetFileName(path), new ReadOnlyFile(path))); }
        catch (Exception ex) { await Dialogs.ErrorAsync(ex); }
    }

    /// <summary>Protects the row's image into its Protected folder, replacing an earlier protected copy of it.</summary>
    public static Task ProtectRowAsync(ProtectionService protection, ProtectableRow row, Func<ProtectableRow?> rowNow, MistcoatOptions options, string extension)
    {
        var source = row.FullPath;
        return protection.RunAsync(row, rowNow, options, extension, (output, result) =>
        {
            var target = ProtectedCopies.PathFor(source, extension);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            // Earlier protected copies of this file (another format, or Mistcoat's "_protected" name) are replaced by the new one.
            foreach (var earlier in ProtectedCopies.FindAll(source))
                if (!string.Equals(earlier, target, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(Path.GetFileName(earlier), Path.GetFileName(source), StringComparison.OrdinalIgnoreCase))
                    File.Delete(earlier);
            File.Move(output, target, overwrite: true);
            var current = rowNow() ?? row;
            current.Advanced = null;
            current.RefreshProtectedCopy();
            return Task.FromResult(ProtectionService.SavedMessage($"{ProtectedCopies.FolderName}/{Path.GetFileName(target)}", result));
        });
    }

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
        row.ProtectMessage = "Opened in Mistcoat. What you save there goes in the Protected folder next to the file; ArtGander shows it here (come back to this page to see it).";
    }

    public static async Task OpenProtectedCopyAsync(ProtectableRow? row)
    {
        if (row?.ProtectedCopyPath is not { } path || !File.Exists(path)) return;
        try { await Launcher.Default.OpenAsync(new OpenFileRequest(Path.GetFileName(path), new ReadOnlyFile(path))); }
        catch (Exception ex) { await Dialogs.ErrorAsync(ex); }
    }

    public static void ShowProtectedCopy(IPlatformServices platform, ProtectableRow? row)
    {
        if (row?.ProtectedCopyPath is { } path && File.Exists(path)) platform.RevealInFileManager(path);
    }
}

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

    /// <summary>Keeps protection progress and open options when the file rows are rebuilt.</summary>
    private static void CarryOver(FileRowViewModel? from, FileRowViewModel to)
    {
        if (from is null) return;
        to.IsProtecting = from.IsProtecting;
        to.ProtectStage = from.ProtectStage;
        to.ProtectFraction = from.ProtectFraction;
        to.ProtectMessage = from.ProtectMessage;
        to.ProtectCancel = from.ProtectCancel;
        to.Advanced = from.Advanced;
    }

    [RelayCommand]
    private void QuickProtect(FileRowViewModel? row) => Ui.Fire(() => QuickProtectAsync(row));

    private async Task QuickProtectAsync(FileRowViewModel? row)
    {
        if (row is null || Doc is null) return;
        await ProtectAsync(row, protection.QuickOptions(Doc.Piece), Path.GetExtension(row.FullPath), ProtectionService.ProtectedVersion(row.Entry.Version));
    }

    [RelayCommand]
    private void ShowProtectOptions(FileRowViewModel? row)
    {
        if (row is null || Doc is null) return;
        row.Advanced = row.Advanced is null
            ? new ProtectOptionsViewModel(Path.GetExtension(row.FullPath), protection.QuickOptions(Doc.Piece), ProtectionService.ProtectedVersion(row.Entry.Version))
            : null;
    }

    [RelayCommand]
    private void ProtectWithOptions(FileRowViewModel? row) => Ui.Fire(() => ProtectWithOptionsAsync(row));

    private async Task ProtectWithOptionsAsync(FileRowViewModel? row)
    {
        if (row?.Advanced is not { } options) return;
        var version = string.IsNullOrWhiteSpace(options.Version) ? ProtectionService.ProtectedVersion(row.Entry.Version) : options.Version;
        await ProtectAsync(row, options.ToOptions(), options.OutputExtension, version);
    }

    [RelayCommand]
    private void CancelProtect(FileRowViewModel? row) => row?.ProtectCancel?.Cancel();

    /// <summary>Opens Mistcoat's own window with this image, to check the preview there and save it from Mistcoat.</summary>
    [RelayCommand]
    private void OpenInMistcoat(FileRowViewModel? row) => Ui.Fire(async () =>
    {
        if (row is null) return;
        if (await FindMistcoatAsync() is not { } runner) return;
        if (!runner.HasWindow)
        {
            await Dialogs.AlertAsync("Mistcoat window not available", "The Mistcoat program set in Settings is the command-line version, which has no window. Choose Mistcoat.exe (or Mistcoat.app) in Settings to use its window.");
            return;
        }
        try { runner.OpenWindow([row.FullPath]); }
        catch (Exception ex) { await Dialogs.ErrorAsync(ex); return; }
        row.ProtectMessage = "Opened in Mistcoat. It saves into a \"Protected\" folder next to the file; add the result here with Add files… (or drop it on this page).";
    });

    private async Task<MistcoatRunner?> FindMistcoatAsync()
    {
        if (protection.Runner is { } runner) return runner;
        await Dialogs.AlertAsync("Mistcoat not found",
            "ArtGander uses Mistcoat to protect images against AI training, but couldn't find it on this computer. "
            + "Install Mistcoat, then choose the program in Settings → Image protection.");
        return null;
    }

    private async Task ProtectAsync(FileRowViewModel row, MistcoatOptions options, string extension, string version)
    {
        if (Doc is not { IsReadOnly: false } doc || !row.CanStartProtect) return;
        if (await FindMistcoatAsync() is not { } runner) return;

        var fileName = row.Entry.FileName;
        var source = row.FullPath;
        var output = ProtectionService.TempOutput(extension);
        using var cancel = new CancellationTokenSource();
        row.ProtectCancel = cancel;
        row.ProtectMessage = "";
        row.ProtectStage = "Starting Mistcoat…";
        row.ProtectFraction = 0;
        row.IsProtecting = true;

        var progress = new Progress<MistcoatProgress>(p =>
        {
            if (RowFor(fileName) is not { } r) return;
            r.ProtectStage = p.Stage;
            r.ProtectFraction = p.Fraction;
        });

        MistcoatResult result;
        try
        {
            result = await runner.ProtectAsync(source, output, options, progress, cancel.Token);
        }
        catch (OperationCanceledException)
        {
            result = new MistcoatResult(false, null, "Cancelled.", null, null, null, []);
        }
        catch (Exception ex)
        {
            result = new MistcoatResult(false, null, ex.Message, null, null, null, []);
        }

        string message;
        if (result.Ok && File.Exists(output))
        {
            var notes = $"Protected against AI training with Mistcoat ({options.Preset}{(options.Strength is { } s ? $", strength {s:0}" : "")}) from {fileName} on {DateTime.Now:d}.";
            FileEntry? added = null;
            if (ReferenceEquals(Doc, doc))
                await FileOpAsync(() => added = pieces.ImportFile(doc, output, version, FileKind.Protected, move: true, notes: notes));
            else
            {
                try { using (app.SuspendWatching()) added = pieces.ImportFile(doc, output, version, FileKind.Protected, move: true, notes: notes); }
                catch (Exception ex) { await Dialogs.ErrorAsync(ex); }
            }
            message = added is null
                ? "Mistcoat finished, but the protected copy couldn't be added to this piece."
                : $"Saved as {added.FileName}" + (result.Summary.Length > 0 ? $" · {result.Summary}" : "");
            if (added is not null && RowFor(fileName) is { } r) r.Advanced = null;
        }
        else
        {
            message = "Not protected: " + (result.Error ?? "Mistcoat didn't produce a file.");
        }
        try { if (File.Exists(output)) File.Delete(output); } catch (IOException) { } catch (UnauthorizedAccessException) { }

        var row2 = RowFor(fileName) ?? row;
        row2.ProtectCancel = null;
        row2.IsProtecting = false;
        row2.ProtectStage = "";
        row2.ProtectMessage = message;
    }
}

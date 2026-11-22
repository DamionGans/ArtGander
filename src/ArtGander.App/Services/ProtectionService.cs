using ArtGander.Core.Integrations;
using ArtGander.Core.Model;
using ArtGander.Core.Naming;

namespace ArtGander.App.Services;

/// <summary>
/// Protecting images against AI training with Mistcoat, the separate app. Where Mistcoat is installed is remembered per
/// computer; the defaults (preset, credit) live in the library settings so every computer uses the same.
/// </summary>
public sealed class ProtectionService(AppState app)
{
    private const string PathKey = "mistcoatPath";

    /// <summary>The Mistcoat program chosen on this computer (may be empty: then it is looked for in the usual places).</summary>
    public string ConfiguredPath
    {
        get => Preferences.Default.Get(PathKey, "");
        set => Preferences.Default.Set(PathKey, value?.Trim().Trim('"') ?? "");
    }

    /// <summary>Mistcoat, or null when it can't be found on this computer.</summary>
    public MistcoatRunner? Runner =>
        MistcoatRunner.Find(ConfiguredPath is { Length: > 0 } p ? p : Environment.GetEnvironmentVariable("MISTCOAT_EXE")) is { } exe ? new MistcoatRunner(exe) : null;

    public ProtectionSettings Defaults => app.Settings.Protection;

    /// <summary>The name embedded as the artist: the library's credit, else the piece's copyright owner, else the default owner.</summary>
    public string CreditFor(ArtPiece? piece) =>
        FirstNonEmpty(Defaults.Credit, piece?.Copyright.Owner, app.Settings.DefaultCopyrightOwner);

    /// <summary>"© 2026 Name", when there is an owner.</summary>
    public string CopyrightFor(ArtPiece? piece)
    {
        var owner = FirstNonEmpty(piece?.Copyright.Owner, app.Settings.DefaultCopyrightOwner, Defaults.Credit);
        return owner.Length == 0 ? "" : $"© {piece?.Copyright.Year ?? DateTime.Now.Year} {owner}";
    }

    /// <summary>The one-click settings: the library's preset with Mistcoat's defaults for everything else.</summary>
    public MistcoatOptions QuickOptions(ArtPiece? piece) => new()
    {
        Preset = MistcoatPasses.Presets.Contains(Defaults.Preset) ? Defaults.Preset : MistcoatPasses.DefaultPreset,
        Metadata = Defaults.Metadata,
        Credit = Defaults.Metadata ? CreditFor(piece) : "",
        Copyright = Defaults.Metadata ? CopyrightFor(piece) : "",
    };

    /// <summary>The version label for the protected copy of a file: "protected", or "{version}-protected".</summary>
    public static string ProtectedVersion(string sourceVersion) =>
        FileNamer.Label(sourceVersion) is { Length: > 0 } v ? $"{v}-protected" : "protected";

    /// <summary>A temporary output file for Mistcoat; ArtGander moves it into the piece folder afterwards.</summary>
    public static string TempOutput(string extension)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ArtGander", "mistcoat");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"{Guid.NewGuid():N}{extension.ToLowerInvariant()}");
    }

    /// <summary>Mistcoat, or a message saying how to set it up (shown to the user) when it's not found.</summary>
    public async Task<MistcoatRunner?> FindAsync()
    {
        if (Runner is { } runner) return runner;
        await Dialogs.AlertAsync("Mistcoat not found",
            "ArtGander uses Mistcoat to protect images against AI training, but couldn't find it on this computer. "
            + "Install Mistcoat, then choose the program in Settings → Image protection.");
        return null;
    }

    /// <summary>
    /// Protects the row's image into a temporary file, shows progress on the row, then lets <paramref name="addResult"/>
    /// put the protected copy where it belongs (it returns the message shown on the row).
    /// </summary>
    /// <param name="rowNow">The row currently shown for this file (rows are rebuilt after file operations).</param>
    public async Task RunAsync(ViewModels.ProtectableRow row, Func<ViewModels.ProtectableRow?> rowNow, MistcoatOptions options, string extension,
        Func<string, MistcoatResult, Task<string>> addResult)
    {
        if (!row.CanStartProtect) return;
        if (await FindAsync() is not { } runner) return;

        var output = TempOutput(extension);
        using var cancel = new CancellationTokenSource();
        row.ProtectCancel = cancel;
        row.ProtectMessage = "";
        row.ProtectStage = "Starting Mistcoat…";
        row.ProtectFraction = 0;
        row.IsProtecting = true;

        var progress = new Progress<MistcoatProgress>(p =>
        {
            if (rowNow() is not { } r) return;
            r.ProtectStage = p.Stage;
            r.ProtectFraction = p.Fraction;
        });

        MistcoatResult result;
        try { result = await runner.ProtectAsync(row.FullPath, output, options, progress, cancel.Token); }
        catch (OperationCanceledException) { result = new MistcoatResult(false, null, "Cancelled.", null, null, null, []); }
        catch (Exception ex) { result = new MistcoatResult(false, null, ex.Message, null, null, null, []); }

        string message;
        if (result.Ok && File.Exists(output))
        {
            try { message = await addResult(output, result); }
            catch (Exception ex) { message = "Mistcoat finished, but the protected copy couldn't be added: " + ex.Message; }
        }
        else
        {
            message = "Not protected: " + (result.Error ?? "Mistcoat didn't produce a file.");
        }
        try { if (File.Exists(output)) File.Delete(output); } catch (IOException) { } catch (UnauthorizedAccessException) { }

        var current = rowNow() ?? row;
        current.ProtectCancel = null;
        current.IsProtecting = false;
        current.ProtectStage = "";
        current.ProtectMessage = message;
    }

    public static string NotesFor(MistcoatOptions options, string sourceName) =>
        $"Protected against AI training with Mistcoat ({options.Preset}{(options.Strength is { } s ? $", strength {s:0}" : "")}) from {sourceName} on {DateTime.Now:d}.";

    public static string SavedMessage(string fileName, MistcoatResult result) =>
        $"Saved as {fileName}" + (result.Summary.Length > 0 ? $" · {result.Summary}" : "");

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? "";
}

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

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? "";
}

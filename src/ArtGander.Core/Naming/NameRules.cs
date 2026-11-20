using System.Text;

namespace ArtGander.Core.Naming;

/// <summary>
/// File and folder name rules that are safe on Windows (NTFS/SMB), macOS (APFS/SMB) and Linux at the same time.
/// The library may live on a network share used from several operating systems, so we apply the strictest
/// common rules everywhere instead of the current platform's rules.
/// </summary>
public static class NameRules
{
    public const int MaxFolderNameLength = 80;
    public const int MaxVersionLength = 40;

    private static readonly HashSet<char> Invalid = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Compares names the way the most restrictive file system does: case-insensitively and Unicode-normalized.
    /// macOS SMB clients can store decomposed (NFD) names, so we normalize before comparing.
    /// </summary>
    public static bool NamesEqual(string? a, string? b) =>
        string.Equals(a?.Normalize(NormalizationForm.FormC), b?.Normalize(NormalizationForm.FormC), StringComparison.OrdinalIgnoreCase);

    public static StringComparer Comparer { get; } = new NormalizedComparer();

    /// <summary>Turns an art piece title into a valid folder name, e.g. <c>"Goose: On the Loose?"</c> becomes <c>"Goose On the Loose"</c>.</summary>
    public static string ToFolderName(string title)
    {
        var sb = new StringBuilder();
        var lastWasSpace = true; // trims leading whitespace
        foreach (var ch in title.Normalize(NormalizationForm.FormC))
        {
            if (char.IsControl(ch) || Invalid.Contains(ch) || char.IsWhiteSpace(ch) || ch == '_')
            {
                if (!lastWasSpace) sb.Append(' ');
                lastWasSpace = true;
                continue;
            }
            sb.Append(ch);
            lastWasSpace = false;
        }

        var name = sb.ToString().TrimEnd(' ', '.');
        if (name.Length > MaxFolderNameLength) name = name[..MaxFolderNameLength].TrimEnd(' ', '.');
        name = name.TrimStart('.'); // no hidden folders
        if (name.Length == 0) name = "Untitled";
        if (ReservedWindowsNames.Contains(name)) name += " Art";
        return name;
    }

    /// <summary>The prefix shared by every file in a piece folder: <c>"Goose On the Loose"</c> becomes <c>"Goose_On_the_Loose"</c>.</summary>
    public static string ToFileBaseName(string folderName) =>
        ToFolderName(folderName).Replace(' ', '_');

    /// <summary>Normalizes a version label: lowercase, with words joined by hyphens, e.g. <c>"Print A4 (v2)"</c> becomes <c>"print-a4-v2"</c>.</summary>
    public static string ToVersionLabel(string version)
    {
        var sb = new StringBuilder();
        var lastWasDash = true;
        foreach (var ch in version.Normalize(NormalizationForm.FormC).ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
                lastWasDash = false;
            }
            else if (!lastWasDash)
            {
                sb.Append('-');
                lastWasDash = true;
            }
        }
        var label = sb.ToString().Trim('-');
        if (label.Length > MaxVersionLength) label = label[..MaxVersionLength].Trim('-');
        return label.Length == 0 ? "file" : label;
    }

    /// <summary>Normalizes an extension to lowercase with a leading dot (<c>"JPG"</c> becomes <c>".jpg"</c>). Returns "" when there is no extension.</summary>
    public static string NormalizeExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension)) return "";
        var ext = extension.Trim().ToLowerInvariant();
        return ext.StartsWith('.') ? ext : "." + ext;
    }

    private sealed class NormalizedComparer : StringComparer
    {
        public override int Compare(string? x, string? y) =>
            string.Compare(x?.Normalize(NormalizationForm.FormC), y?.Normalize(NormalizationForm.FormC), StringComparison.OrdinalIgnoreCase);

        public override bool Equals(string? x, string? y) => NamesEqual(x, y);

        public override int GetHashCode(string obj) =>
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Normalize(NormalizationForm.FormC));
    }
}

namespace ArtGander.Core.Naming;

/// <summary>
/// Builds and parses the naming convention <c>{Art_Piece_Name}-{version}.{ext}</c>.
/// Example: <c>Goose On The Loose/Goose_On_The_Loose-transparent.png</c>.
/// A file without a version, <c>{Art_Piece_Name}.{ext}</c>, is the <b>cover</b>: the official, fully cropped art.
/// Its version label is empty.
/// </summary>
public static class FileNamer
{
    public static string Build(string folderName, string version, string extension) =>
        Label(version).Length == 0
            ? $"{NameRules.ToFileBaseName(folderName)}{NameRules.NormalizeExtension(extension)}"
            : $"{NameRules.ToFileBaseName(folderName)}-{Label(version)}{NameRules.NormalizeExtension(extension)}";

    /// <summary>A version label for a file name; empty (the cover) stays empty.</summary>
    public static string Label(string? version) => string.IsNullOrWhiteSpace(version) ? "" : NameRules.ToVersionLabel(version);

    /// <summary>The cover is the file named after the piece without a version: <c>{Art_Piece_Name}.{ext}</c>.</summary>
    public static bool IsCoverName(string folderName, string fileName) =>
        NameRules.NamesEqual(Path.GetFileNameWithoutExtension(fileName), NameRules.ToFileBaseName(folderName));

    /// <summary>Checks whether <paramref name="fileName"/> follows the convention for <paramref name="folderName"/>. If it does, returns its version label.</summary>
    public static bool TryParseVersion(string folderName, string fileName, out string version)
    {
        version = "";
        if (IsCoverName(folderName, fileName)) return true; // the cover: no version
        var prefix = NameRules.ToFileBaseName(folderName) + "-";
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (stem.Length <= prefix.Length || !NameRules.NamesEqual(stem[..prefix.Length], prefix))
            return false;

        var candidate = stem[prefix.Length..];
        if (candidate != NameRules.ToVersionLabel(candidate))
            return false;

        version = candidate;
        return true;
    }

    /// <summary>Checks whether the file name matches the convention exactly, including the lowercase extension.</summary>
    public static bool FollowsConvention(string folderName, string fileName) =>
        TryParseVersion(folderName, fileName, out var version)
        && fileName == Build(folderName, version, Path.GetExtension(fileName));

    /// <summary>Suggests a version label for a file that doesn't follow the convention yet.</summary>
    public static string SuggestVersion(string folderName, string fileName)
    {
        if (TryParseVersion(folderName, fileName, out var version)) return version;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        // Drop the piece title from the start of the name if it is there in some other form, e.g. "Goose On The Loose - transparent".
        var title = NameRules.ToVersionLabel(folderName);
        var label = NameRules.ToVersionLabel(stem);
        if (label.StartsWith(title + "-", StringComparison.Ordinal)) label = label[(title.Length + 1)..];
        return label == title ? "" : label; // just the title, e.g. "Goose On The Loose.png": that's the cover
    }

    /// <summary>Returns a version label that isn't taken yet. It adds -2, -3, and so on to <paramref name="version"/> when needed.</summary>
    public static string MakeUnique(string folderName, string version, string extension, Func<string, bool> exists)
    {
        var label = Label(version);
        if (label.Length == 0)
        {
            if (!exists(Build(folderName, "", extension))) return "";
            label = "cover"; // a second cover in the same format: cover, cover-2…
        }
        var candidate = label;
        for (var i = 2; exists(Build(folderName, candidate, extension)); i++)
            candidate = $"{label}-{i}";
        return candidate;
    }
}

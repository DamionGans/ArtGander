using ArtGander.Core.Naming;

namespace ArtGander.Core.Services;

/// <summary>
/// Special folders inside an art piece's (or product's) folder:
/// <list type="bullet">
/// <item><c>Protected/</c>: copies protected against AI training (made with Mistcoat), named <c>{name}-protected.{ext}</c>
/// after the original. Mistcoat's own window names them <c>{name}_protected.{ext}</c>; those count too, as does a copy
/// named exactly like the original. The original is never changed.</item>
/// <item><c>Photograph/</c>: the original photographs of the artwork, <c>{Art_Piece_Name}-photograph.{ext}</c>.</item>
/// </list>
/// Neither is ever an art piece of its own.
/// </summary>
public static class ProtectedCopies
{
    public const string FolderName = "Protected";
    public const string PhotographFolderName = "Photograph";
    public const string Suffix = "-protected";
    private static readonly string[] Suffixes = [Suffix, "_protected", ""];

    /// <summary>The piece's (or product's) own folder for a file, also when the file sits in its Photograph folder.</summary>
    public static string OwnerFolderOf(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath) ?? "";
        return NameRules.NamesEqual(Path.GetFileName(dir), PhotographFolderName) ? Path.GetDirectoryName(dir) ?? dir : dir;
    }

    /// <summary>The Protected folder in the piece's (or product's) folder.</summary>
    public static string FolderFor(string originalPath) => Path.Combine(OwnerFolderOf(originalPath), FolderName);

    /// <summary>Where a new protected copy of <paramref name="originalPath"/> goes; <paramref name="extension"/> picks the format.</summary>
    public static string PathFor(string originalPath, string? extension = null) =>
        Path.Combine(FolderFor(originalPath), Path.GetFileNameWithoutExtension(originalPath) + Suffix + (extension ?? Path.GetExtension(originalPath)).ToLowerInvariant());

    /// <summary>The protected copy of a file (the newest when there are several), or null.</summary>
    public static string? Find(string originalPath) => FindAll(originalPath).FirstOrDefault();

    /// <summary>Every protected copy of a file, newest first.</summary>
    public static IReadOnlyList<string> FindAll(string originalPath)
    {
        var folder = FolderFor(originalPath);
        if (!Directory.Exists(folder)) return [];
        var stem = Path.GetFileNameWithoutExtension(originalPath);
        try
        {
            return Directory.EnumerateFiles(folder)
                .Where(f => ProductImageService.IsImage(f) && Suffixes.Any(s => NameRules.NamesEqual(Path.GetFileNameWithoutExtension(f), stem + s)))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>After a file was renamed, its protected copies follow: "Old-master-protected.png" → "New-master-protected.png".</summary>
    public static void FollowRename(string oldOriginalPath, string newOriginalPath)
    {
        var oldStem = Path.GetFileNameWithoutExtension(oldOriginalPath);
        var newStem = Path.GetFileNameWithoutExtension(newOriginalPath);
        if (oldStem == newStem) return;
        foreach (var copy in FindAll(oldOriginalPath))
        {
            var rest = Path.GetFileName(copy)[oldStem.Length..];
            var target = Path.Combine(FolderFor(newOriginalPath), newStem + rest);
            if (File.Exists(target) && !NameRules.NamesEqual(target, copy)) continue;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                PieceService.MoveSafe(copy, target);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Whether a folder is a Protected or Photograph folder rather than an art piece (an art piece has its details file).</summary>
    public static bool IsSpecialFolder(string folderPath) =>
        (NameRules.NamesEqual(Path.GetFileName(folderPath), FolderName) || NameRules.NamesEqual(Path.GetFileName(folderPath), PhotographFolderName))
        && !Storage.ArtPieceStore.HasPieceFile(folderPath);

    /// <summary>The pictures in the Photograph folder of a piece's (or product's) folder.</summary>
    public static IReadOnlyList<string> Photographs(string ownerFolder)
    {
        var folder = Path.Combine(ownerFolder, PhotographFolderName);
        if (!Directory.Exists(folder)) return [];
        try
        {
            return Directory.EnumerateFiles(folder)
                .Where(f => !Path.GetFileName(f).StartsWith('.') && ProductImageService.IsImage(f))
                .Order(NameRules.NaturalComparer)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>Where a tracked file that's missing went, when it was moved into the Photograph or Protected folder; else null.</summary>
    public static string? MovedInto(string ownerFolder, string fileName)
    {
        foreach (var special in new[] { PhotographFolderName, FolderName })
            if (File.Exists(Path.Combine(ownerFolder, special, fileName))) return special;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (stem.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase) || stem.EndsWith("_protected", StringComparison.OrdinalIgnoreCase))
            return Directory.Exists(Path.Combine(ownerFolder, FolderName)) ? FolderName : null;
        return null;
    }
}

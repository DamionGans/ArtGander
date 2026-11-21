using ArtGander.Core.Model;
using ArtGander.Core.Naming;
using ArtGander.Core.Schema;
using ArtGander.Core.Storage;

namespace ArtGander.Core.Library;

/// <summary>
/// One folder in the art library, as found on disk. The filesystem is the source of truth:
/// what <c>artgander.json</c> says gets reconciled against the files that actually exist.
/// </summary>
public sealed class PieceFolder
{
    public required string FolderPath { get; init; }
    public string FolderName => Path.GetFileName(FolderPath);

    /// <summary>Null when the folder has no <c>artgander.json</c> yet (not adopted) or the file couldn't be read.</summary>
    public ArtPieceDocument? Document { get; init; }
    public string? Error { get; init; }

    /// <summary>Files on disk that aren't listed in <c>artgander.json</c>.</summary>
    public IReadOnlyList<string> UntrackedFiles { get; init; } = [];

    /// <summary>Files listed in <c>artgander.json</c> that no longer exist on disk.</summary>
    public IReadOnlyList<string> MissingFiles { get; init; } = [];

    /// <summary>Tracked files whose names don't follow the <c>{Art_Piece_Name}-{version}.{ext}</c> convention.</summary>
    public IReadOnlyList<string> MisnamedFiles { get; init; } = [];

    public bool IsAdopted => Document is not null;
    public bool NeedsAttention => !IsAdopted || Error is not null || UntrackedFiles.Count > 0 || MissingFiles.Count > 0 || MisnamedFiles.Count > 0;

    public string Title => Document?.Piece.Title is { Length: > 0 } t ? t : FolderName;

    /// <summary>Files ArtGander never treats as art files.</summary>
    public static bool IsIgnoredFile(string fileName) =>
        fileName.StartsWith('.')
        || NameRules.NamesEqual(fileName, CurrentSchema.LegacyPieceFileName)
        || fileName.StartsWith(CurrentSchema.LegacyPieceFileName + ".", StringComparison.OrdinalIgnoreCase) // old backups
        || NameRules.NamesEqual(fileName, "Thumbs.db")
        || NameRules.NamesEqual(fileName, "desktop.ini")
        || fileName.StartsWith("~$", StringComparison.Ordinal)
        || AtomicFile.IsTempFile(fileName);

    public static IReadOnlyList<string> ListArtFiles(string folderPath)
    {
        try
        {
            return Directory.EnumerateFiles(folderPath)
                .Select(Path.GetFileName)
                .OfType<string>()
                .Where(n => !IsIgnoredFile(n))
                .OrderBy(n => n, NameRules.Comparer)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Reconciles a loaded document with the folder contents.</summary>
    public static PieceFolder From(string folderPath, ArtPieceDocument? doc, string? error = null)
    {
        var onDisk = ListArtFiles(folderPath);
        if (doc is null)
            return new PieceFolder { FolderPath = folderPath, Error = error, UntrackedFiles = onDisk };

        var diskSet = new HashSet<string>(onDisk, NameRules.Comparer);
        var tracked = new HashSet<string>(doc.Piece.Files.Select(f => f.FileName), NameRules.Comparer);
        return new PieceFolder
        {
            FolderPath = folderPath,
            Document = doc,
            Error = error,
            UntrackedFiles = onDisk.Where(f => !tracked.Contains(f)).ToList(),
            MissingFiles = doc.Piece.Files.Select(f => f.FileName).Where(f => !diskSet.Contains(f)).ToList(),
            MisnamedFiles = doc.Piece.Files.Select(f => f.FileName)
                .Where(f => diskSet.Contains(f) && !FileNamer.FollowsConvention(doc.FolderName, f)).ToList(),
        };
    }

    private static readonly string[] PreviewExtensions = [".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp", ".tif", ".tiff", ".heic"];
    private static readonly string[] PreviewPreference = [FileKind.Master, FileKind.Edit, FileKind.Print, FileKind.Transparent, FileKind.Mockup, FileKind.Photo, FileKind.Scan];

    /// <summary>Known good preview kinds first; custom and "other" kinds after them.</summary>
    private static int PreviewRank(string kind)
    {
        var i = Array.FindIndex(PreviewPreference, k => NameRules.NamesEqual(k, kind));
        return i < 0 ? PreviewPreference.Length : i;
    }

    /// <summary>Picks the best file to show as the piece's thumbnail.</summary>
    public string? PreviewFilePath
    {
        get
        {
            var candidates = Document?.Piece.Files
                .Where(f => PreviewExtensions.Contains(Path.GetExtension(f.FileName).ToLowerInvariant()) && !MissingFiles.Contains(f.FileName))
                .OrderBy(f => PreviewRank(f.Kind))
                .Select(f => f.FileName)
                ?? UntrackedFiles.Where(f => PreviewExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()));
            var first = candidates.FirstOrDefault();
            return first is null ? null : Path.Combine(FolderPath, first);
        }
    }
}

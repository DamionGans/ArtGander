using ArtGander.Core.Model;
using ArtGander.Core.Naming;
using ArtGander.Core.Schema;
using ArtGander.Core.Storage;

namespace ArtGander.Core.Library;

/// <summary>
/// One folder in the art library, as found on disk: an art piece or a project. The filesystem is the source of truth:
/// what <c>.artgander.json</c> says gets reconciled against the files that actually exist.
/// Projects contain their art pieces as subfolders (<see cref="Children"/>).
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

    /// <summary>For untracked folders: what the folder looks like (see <see cref="FolderClassifier"/>).</summary>
    public ItemType SuggestedType { get; init; } = ItemType.Piece;

    /// <summary>The number of art subfolders found (untracked project candidates, or a piece that grew subfolders).</summary>
    public int ArtSubfolderCount { get; init; }

    /// <summary>The project this piece belongs to, or null for a stand-alone piece or a project.</summary>
    public PieceFolder? Parent { get; internal set; }

    /// <summary>A project's art pieces, in the project's order.</summary>
    public IReadOnlyList<PieceFolder> Children { get; internal set; } = [];

    public ItemType Type => Document?.Piece.Type ?? SuggestedType;
    public bool IsProject => Type == ItemType.Project;

    /// <summary>A tracked art piece that contains several art subfolders and probably should be a project.</summary>
    public bool LooksLikeProject => IsAdopted && !IsProject && Parent is null && ArtSubfolderCount >= FolderClassifier.MinimumProjectPieces;

    public bool IsAdopted => Document is not null;
    public bool NeedsAttention => !IsAdopted || Error is not null || UntrackedFiles.Count > 0 || MissingFiles.Count > 0 || MisnamedFiles.Count > 0
                                  || LooksLikeProject || Children.Any(c => c.NeedsAttention);

    /// <summary>"Project > Piece" for pieces inside a project, otherwise the title.</summary>
    public string DisplayPath => Parent is null ? Title : Parent.Title + " › " + Title;

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
    public static PieceFolder From(string folderPath, ArtPieceDocument? doc, string? error = null, bool classify = true)
    {
        var onDisk = ListArtFiles(folderPath);
        // Projects can't be nested, so only top-level folders are checked for art subfolders.
        var artSubfolders = classify ? FolderClassifier.ArtSubfolders(folderPath).Count : 0;
        if (doc is null)
        {
            return new PieceFolder
            {
                FolderPath = folderPath,
                Error = error,
                UntrackedFiles = onDisk,
                ArtSubfolderCount = artSubfolders,
                SuggestedType = artSubfolders >= FolderClassifier.MinimumProjectPieces ? ItemType.Project : ItemType.Piece,
            };
        }

        var diskSet = new HashSet<string>(onDisk, NameRules.Comparer);
        var tracked = new HashSet<string>(doc.Piece.Files.Select(f => f.FileName), NameRules.Comparer);
        return new PieceFolder
        {
            FolderPath = folderPath,
            Document = doc,
            Error = error,
            ArtSubfolderCount = artSubfolders,
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

    /// <summary>
    /// Picks the best file to show as the thumbnail: the cover (the file named after the piece without a version,
    /// or one of kind "cover"), then the other images, then (for a project) the first piece's preview.
    /// An untracked cover file counts too.
    /// </summary>
    public string? PreviewFilePath
    {
        get
        {
            var images = Document?.Piece.Files
                .Where(f => PreviewExtensions.Contains(Path.GetExtension(f.FileName).ToLowerInvariant()) && !MissingFiles.Contains(f.FileName))
                .OrderBy(f => f.Version.Length == 0 && FileNamer.IsCoverName(FolderName, f.FileName) ? -2 : IsCover(f) ? -1 : PreviewRank(f.Kind))
                .Select(f => f.FileName)
                .ToList()
                ?? UntrackedFiles.Where(f => PreviewExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    .OrderBy(f => FileNamer.IsCoverName(FolderName, f) ? 0 : 1).ToList();
            var first = images.FirstOrDefault();
            if (first is not null) return Path.Combine(FolderPath, first);
            // No art file: the original photograph, if there is one.
            if (Document is not null && Services.ProtectedCopies.Photographs(FolderPath).FirstOrDefault() is { } photo) return photo;
            return Children.Select(c => c.PreviewFilePath).FirstOrDefault(p => p is not null);
        }
    }

    private static bool IsCover(FileEntry f) =>
        NameRules.NamesEqual(f.Kind, "cover") || f.Version.Contains("cover", StringComparison.OrdinalIgnoreCase);
}

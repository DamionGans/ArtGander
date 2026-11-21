using ArtGander.Core.Model;
using ArtGander.Core.Storage;

namespace ArtGander.Core.Library;

/// <summary>
/// Decides whether an untracked folder looks like a single art piece or a project.
/// A folder with at least two subfolders that contain art (or are tracked pieces already) looks like a project,
/// for example a book folder with one subfolder per illustration. Anything else looks like an art piece.
/// </summary>
public static class FolderClassifier
{
    public const int MinimumProjectPieces = 2;

    private static readonly HashSet<string> ArtExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp", ".tif", ".tiff", ".heic", ".svg",
        ".psd", ".psb", ".kra", ".procreate", ".clip", ".afphoto", ".afdesign", ".xcf", ".ai", ".eps",
        ".raw", ".dng", ".cr2", ".cr3", ".nef", ".arw", ".orf", ".rw2",
    };

    public static bool IsArtFile(string fileName) => ArtExtensions.Contains(Path.GetExtension(fileName));

    /// <summary>The subfolders that look like art pieces: tracked pieces, or folders with art files in them.</summary>
    public static IReadOnlyList<string> ArtSubfolders(string folderPath) =>
        LibraryScanner.EnumeratePieceFolders(folderPath)
            .Where(sub => ArtPieceStore.HasPieceFile(sub) || PieceFolder.ListArtFiles(sub).Any(IsArtFile))
            .ToList();

    public static ItemType Suggest(string folderPath) =>
        ArtSubfolders(folderPath).Count >= MinimumProjectPieces ? ItemType.Project : ItemType.Piece;
}

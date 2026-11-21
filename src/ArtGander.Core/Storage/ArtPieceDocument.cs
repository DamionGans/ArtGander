using ArtGander.Core.Model;
using ArtGander.Core.Schema;

namespace ArtGander.Core.Storage;

/// <summary>An art piece folder together with its loaded <c>artgander.json</c>.</summary>
public sealed class ArtPieceDocument
{
    internal ArtPieceDocument(string folderPath, ArtPiece piece, string? loadedHash, string savedJson)
    {
        FolderPath = folderPath;
        Piece = piece;
        LoadedHash = loadedHash;
        SavedJson = savedJson;
    }

    public string FolderPath { get; internal set; }
    public string FolderName => Path.GetFileName(FolderPath);

    /// <summary>For an art piece inside a project: the project's title (set by the library scanner).</summary>
    public string? ProjectTitle { get; set; }

    /// <summary>"Project › Piece" for pieces inside a project, otherwise the title.</summary>
    public string DisplayTitle => ProjectTitle is null ? Piece.Title : ProjectTitle + " › " + Piece.Title;
    public string FilePath => Path.Combine(FolderPath, CurrentSchema.PieceFileName);
    public ArtPiece Piece { get; }

    /// <summary>The file was written by a newer ArtGander. It is shown, but saving is blocked so no data is lost.</summary>
    public bool IsReadOnly { get; internal init; }
    public string? ReadOnlyReason { get; internal init; }

    /// <summary>Set when the file was upgraded from an older schema version during load.</summary>
    public int? UpgradedFromVersion { get; internal init; }

    /// <summary>The hash of the file as it was on disk when it was last loaded or saved. Used to detect edits made by another computer.</summary>
    internal string? LoadedHash { get; set; }

    /// <summary>The JSON as it was last loaded or saved. Used to detect unsaved changes.</summary>
    internal string SavedJson { get; set; }

    public bool HasUnsavedChanges => ArtGanderJson.Serialize(Piece) != SavedJson;

    public string GetFilePath(string fileName) => Path.Combine(FolderPath, fileName);
}

using ArtGander.Core.Library;
using ArtGander.Core.Naming;
using ArtGander.Core.Storage;

namespace ArtGander.Core.Services;

/// <summary>One name that doesn't fit the naming rules, and what it becomes.</summary>
public sealed record NameFix(string PieceTitle, bool IsFolder, string OldName, string NewName);

public sealed record NameFixResult(int Renamed, IReadOnlyList<string> Problems);

public sealed partial class PieceService
{
    /// <summary>
    /// Lists every name in the library that doesn't fit the rules: tracked files that don't follow
    /// <c>{Art_Piece_Name}-{version}.{ext}</c>, and folders whose names break the Windows/macOS rules
    /// (those are renamed after the piece's title). Read-only.
    /// </summary>
    public static IReadOnlyList<NameFix> PlanNameFixes(IEnumerable<PieceFolder> folders)
    {
        var fixes = new List<NameFix>();
        foreach (var folder in folders)
        {
            if (folder.Document is not { IsReadOnly: false } doc) continue;
            var folderName = doc.FolderName;
            if (!FolderNameIsCompatible(folderName))
            {
                folderName = NameRules.ToFolderName(doc.Piece.Title);
                fixes.Add(new NameFix(doc.DisplayTitle, true, doc.FolderName, folderName));
            }
            foreach (var name in folder.MisnamedFiles)
            {
                var entry = doc.Piece.FindFile(name);
                if (entry is null) continue;
                var version = string.IsNullOrWhiteSpace(entry.Version) ? FileNamer.SuggestVersion(doc.FolderName, name) : entry.Version;
                fixes.Add(new NameFix(doc.DisplayTitle, false, name, FileNamer.Build(folderName, version, Path.GetExtension(name))));
            }
        }
        return fixes;
    }

    /// <summary>A folder name is fine when the naming rules would leave it as it is.</summary>
    public static bool FolderNameIsCompatible(string folderName) => NameRules.ToFolderName(folderName) == folderName;

    /// <summary>
    /// Fixes every name from <see cref="PlanNameFixes"/>. Pieces inside a project are done before the project itself,
    /// so renaming a project folder never leaves its pieces behind. Each piece is read fresh from disk first, and a piece
    /// that can't be fixed (e.g. the new folder name is taken, or a file is open in another app) is reported and skipped.
    /// </summary>
    public NameFixResult FixAllNames(IReadOnlyList<PieceFolder> topLevel)
    {
        var renamed = 0;
        var problems = new List<string>();
        foreach (var top in topLevel)
        {
            // Children first: their paths are still valid until the project folder itself is renamed.
            foreach (var child in top.Children) renamed += FixPiece(child.FolderPath, problems);
            renamed += FixPiece(top.FolderPath, problems);
        }
        return new NameFixResult(renamed, problems);
    }

    private int FixPiece(string folderPath, List<string> problems)
    {
        if (!ArtPieceStore.HasPieceFile(folderPath)) return 0;
        ArtPieceDocument? doc = null;
        var count = 0;
        try
        {
            doc = store.Load(folderPath);
            if (doc.IsReadOnly) return 0;

            if (!FolderNameIsCompatible(doc.FolderName))
            {
                RenamePiece(doc, doc.Piece.Title); // renames the folder and every file that follows the convention
                count++;
            }
            foreach (var entry in doc.Piece.Files.ToList())
            {
                if (!File.Exists(doc.GetFilePath(entry.FileName)) || FileNamer.FollowsConvention(doc.FolderName, entry.FileName)) continue;
                ApplyConvention(doc, entry);
                count++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArtPieceFileException or InvalidOperationException)
        {
            problems.Add($"{doc?.DisplayTitle ?? Path.GetFileName(folderPath)}: {ex.Message}");
        }
        return count;
    }
}

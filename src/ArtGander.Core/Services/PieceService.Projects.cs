using ArtGander.Core.Library;
using ArtGander.Core.Model;
using ArtGander.Core.Naming;
using ArtGander.Core.Storage;

namespace ArtGander.Core.Services;

/// <summary>
/// Project operations. A project is a folder holding project-only files plus one subfolder per art piece.
/// Moving a piece into or out of a project moves its whole folder; the project's piece order is kept up to date.
/// </summary>
public sealed partial class PieceService
{
    public ArtPieceDocument CreateProject(string rootPath, string title, LibrarySettings? defaults = null) =>
        CreatePiece(rootPath, title, defaults, ItemType.Project);

    /// <summary>Creates a new art piece inside a project. Planned pieces are placeholders for work that isn't made yet.</summary>
    public ArtPieceDocument CreatePieceInProject(ArtPieceDocument project, string title, LibrarySettings? defaults = null, bool planned = false)
    {
        EnsureProject(project);
        EnsureNotChangedElsewhere(project);
        var doc = CreatePiece(project.FolderPath, title, defaults, ItemType.Piece, planned ? WorkStatus.Planned : WorkStatus.InProgress);
        project.Piece.PieceOrder.Add(doc.Piece.Id);
        store.Save(project);
        return doc;
    }

    /// <summary>
    /// Moves an art piece's folder into a project, into another project, or back to the art folder
    /// (<paramref name="targetFolder"/> is a project folder or the art folder itself).
    /// </summary>
    public void MovePiece(ArtPieceDocument piece, string targetFolder)
    {
        if (piece.Piece.IsProject) throw new InvalidOperationException("Projects can't be placed inside other projects.");
        EnsureNotChangedElsewhere(piece);

        var sourceParent = Path.GetDirectoryName(piece.FolderPath)!;
        if (NameRules.NamesEqual(Path.GetFullPath(sourceParent), Path.GetFullPath(targetFolder))) return;

        ArtPieceDocument? targetProject = null;
        if (ArtPieceStore.HasPieceFile(targetFolder))
        {
            targetProject = store.Load(targetFolder);
            if (!targetProject.Piece.IsProject) throw new InvalidOperationException($"\"{targetProject.Piece.Title}\" is an art piece, not a project.");
            if (targetProject.IsReadOnly) throw new InvalidOperationException(targetProject.ReadOnlyReason);
        }

        var destination = Path.Combine(targetFolder, piece.FolderName);
        if (Directory.Exists(destination))
            throw new IOException($"\"{Path.GetFileName(targetFolder)}\" already contains a folder named \"{piece.FolderName}\".");

        MoveDirectorySafe(piece.FolderPath, destination);
        piece.FolderPath = destination;

        // Keep both projects' piece order in sync. The move itself already succeeded, so these are best-effort.
        UpdateOrder(sourceParent, order => order.Remove(piece.Piece.Id));
        if (targetProject is not null && !targetProject.Piece.PieceOrder.Contains(piece.Piece.Id))
        {
            targetProject.Piece.PieceOrder.Add(piece.Piece.Id);
            store.Save(targetProject);
        }
    }

    /// <summary>
    /// Creates a new project and moves the given art pieces into it. If a move fails, everything is put back
    /// and the new project is removed again.
    /// </summary>
    public ArtPieceDocument GroupIntoProject(string rootPath, string title, IReadOnlyList<string> pieceFolders, LibrarySettings? defaults = null)
    {
        var pieces = pieceFolders.Select(store.Load).ToList();
        var bad = pieces.FirstOrDefault(p => p.Piece.IsProject || p.IsReadOnly);
        if (bad is not null) throw new InvalidOperationException($"\"{bad.Piece.Title}\" can't be moved into a project.");

        var project = CreateProject(rootPath, title, defaults);
        var moved = new List<(ArtPieceDocument Doc, string From)>();
        try
        {
            foreach (var piece in pieces)
            {
                var from = Path.GetDirectoryName(piece.FolderPath)!;
                MovePiece(piece, project.FolderPath);
                moved.Add((piece, from));
            }
            return store.Load(project.FolderPath);
        }
        catch
        {
            foreach (var (doc, from) in Enumerable.Reverse(moved))
            {
                try { MovePiece(doc, from); } catch (IOException) { }
            }
            try { Directory.Delete(project.FolderPath, recursive: true); } catch (IOException) { }
            throw;
        }
    }

    /// <summary>
    /// Turns a stand-alone art piece into a project. Its tracked files become project files, and its art subfolders
    /// become its pieces.
    /// </summary>
    public void ConvertToProject(ArtPieceDocument doc, LibrarySettings? defaults = null)
    {
        if (doc.Piece.IsProject) return;
        if (ArtPieceStore.HasPieceFile(Path.GetDirectoryName(doc.FolderPath)!))
            throw new InvalidOperationException("A piece inside a project can't become a project itself.");
        EnsureNotChangedElsewhere(doc);
        doc.Piece.Type = ItemType.Project;
        store.Save(doc);
        TrackProjectChildren(doc, defaults);
    }

    /// <summary>Sets the order of a project's pieces (e.g. the page order of a book).</summary>
    public void ReorderPieces(ArtPieceDocument project, IEnumerable<Guid> orderedPieceIds)
    {
        EnsureProject(project);
        EnsureNotChangedElsewhere(project);
        project.Piece.PieceOrder.Clear();
        foreach (var id in orderedPieceIds.Distinct()) project.Piece.PieceOrder.Add(id);
        store.Save(project);
    }

    /// <summary>Tracks a project's untracked art subfolders as pieces and adds every piece to the piece order.</summary>
    private void TrackProjectChildren(ArtPieceDocument project, LibrarySettings? defaults)
    {
        foreach (var sub in LibraryScanner.EnumeratePieceFolders(project.FolderPath).Order(NameRules.NaturalComparer))
        {
            ArtPieceDocument? child = null;
            if (ArtPieceStore.HasPieceFile(sub))
            {
                try { child = store.Load(sub); } catch (ArtPieceFileException) { }
            }
            else if (PieceFolder.ListArtFiles(sub).Any(FolderClassifier.IsArtFile))
            {
                child = AdoptFolder(sub, defaults, ItemType.Piece);
            }
            if (child is not null && !child.Piece.IsProject && !project.Piece.PieceOrder.Contains(child.Piece.Id))
                project.Piece.PieceOrder.Add(child.Piece.Id);
        }
        store.Save(project, force: true);
    }

    private void UpdateOrder(string folder, Func<IList<Guid>, bool> change)
    {
        if (!ArtPieceStore.HasPieceFile(folder)) return;
        try
        {
            var doc = store.Load(folder);
            if (doc.IsReadOnly || !doc.Piece.IsProject) return;
            if (change(doc.Piece.PieceOrder)) store.Save(doc);
        }
        catch (Exception ex) when (ex is IOException or ArtPieceFileException) { }
    }

    private static void EnsureProject(ArtPieceDocument doc)
    {
        if (!doc.Piece.IsProject) throw new InvalidOperationException($"\"{doc.Piece.Title}\" is not a project.");
    }
}

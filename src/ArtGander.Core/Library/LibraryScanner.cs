using ArtGander.Core.Naming;
using ArtGander.Core.Schema;
using ArtGander.Core.Storage;

namespace ArtGander.Core.Library;

/// <summary>
/// Reads the whole art folder. Each subfolder is an art piece or a project; a project's subfolders are its pieces.
/// </summary>
public sealed class LibraryScanner(ArtPieceStore store)
{
    public ArtPieceStore Store => store;

    public IReadOnlyList<PieceFolder> Scan(string rootPath, CancellationToken ct = default)
    {
        if (!Directory.Exists(rootPath)) return [];

        var folders = EnumeratePieceFolders(rootPath, ProductsFolderOf(rootPath)).ToList();
        var results = new PieceFolder[folders.Count];
        // Network shares have high latency per file, so reading folders in parallel pays off a lot.
        Parallel.For(0, folders.Count, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = 8 },
            i => results[i] = ScanFolder(folders[i], ct));
        return results.OrderBy(p => p.Title, NameRules.NaturalComparer).ToList();
    }

    /// <summary>Scans a top-level folder: a piece, or a project together with its pieces.</summary>
    public PieceFolder ScanFolder(string folderPath, CancellationToken ct = default)
    {
        var folder = ScanSingle(folderPath, classify: true);
        if (folder is { IsProject: true, IsAdopted: true }) ScanChildren(folder, ct);
        return folder;
    }

    private PieceFolder ScanSingle(string folderPath, bool classify)
    {
        if (!ArtPieceStore.HasPieceFile(folderPath))
            return PieceFolder.From(folderPath, null, classify: classify);
        try
        {
            return PieceFolder.From(folderPath, store.Load(folderPath), classify: classify);
        }
        catch (ArtPieceFileException ex)
        {
            return PieceFolder.From(folderPath, null, ex.Message, classify: classify);
        }
    }

    private void ScanChildren(PieceFolder project, CancellationToken ct)
    {
        var dirs = EnumeratePieceFolders(project.FolderPath).ToList();
        var children = new PieceFolder[dirs.Count];
        Parallel.For(0, dirs.Count, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = 4 },
            i => children[i] = ScanSingle(dirs[i], classify: false));
        foreach (var c in children)
        {
            c.Parent = project;
            if (c.Document is not null) c.Document.ProjectTitle = project.Title;
        }
        project.Children = OrderChildren(project.Document!.Piece.PieceOrder, children);
    }

    /// <summary>Orders a project's pieces: first as listed in pieceOrder, then the rest by title.</summary>
    public static IReadOnlyList<PieceFolder> OrderChildren(IList<Guid> order, IEnumerable<PieceFolder> children) =>
        children
            .OrderBy(c => c.Document is { } d && order.IndexOf(d.Piece.Id) is var i and >= 0 ? i : int.MaxValue)
            .ThenBy(c => c.Title, NameRules.NaturalComparer)
            .ToList();

    /// <summary>The products folder of the library (never an art piece), from its settings.</summary>
    public static string ProductsFolderOf(string rootPath)
    {
        try { return LibrarySettingsStore.Load(rootPath).ProductsFolder; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return ProductStore.DefaultFolderName; }
    }

    /// <param name="productsFolder">At the top of the art folder: the products folder, which is skipped.</param>
    public static IEnumerable<string> EnumeratePieceFolders(string rootPath, string? productsFolder = null)
    {
        IEnumerable<string> dirs;
        try { dirs = Directory.EnumerateDirectories(rootPath).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }

        foreach (var dir in dirs)
        {
            var name = Path.GetFileName(dir);
            if (name.StartsWith('.') || name.StartsWith('$') || NameRules.NamesEqual(name, CurrentSchema.LibraryFolderName))
                continue;
            if (NameRules.NamesEqual(name, "#recycle") || NameRules.NamesEqual(name, "@eaDir")) // NAS system folders
                continue;
            if (productsFolder is not null && NameRules.NamesEqual(name, productsFolder))
                continue;
            if (Services.ProtectedCopies.IsSpecialFolder(dir)) // protected copies or the photographs, not an art piece
                continue;
            yield return dir;
        }
    }

    /// <summary>
    /// A cheap fingerprint of the library state (folder names plus metadata timestamps and sizes).
    /// Comparing fingerprints detects changes from other computers on shares where file-change notifications don't work.
    /// </summary>
    public static string Fingerprint(string rootPath, IEnumerable<string>? projectFolders = null, string? productsFolder = null)
    {
        var hash = new HashCode();
        var dirs = EnumeratePieceFolders(rootPath, productsFolder)
            .Concat((projectFolders ?? []).Where(Directory.Exists).SelectMany(p => EnumeratePieceFolders(p)));
        foreach (var dir in dirs.Order(StringComparer.Ordinal))
        {
            hash.Add(dir, StringComparer.Ordinal);
            try
            {
                var di = new DirectoryInfo(dir);
                hash.Add(di.LastWriteTimeUtc);
                var meta = new FileInfo(Path.Combine(dir, CurrentSchema.PieceFileName));
                if (meta.Exists) { hash.Add(meta.LastWriteTimeUtc); hash.Add(meta.Length); }
            }
            catch (IOException) { }
        }
        return hash.ToHashCode().ToString("X8");
    }
}

using ArtGander.Core.Naming;
using ArtGander.Core.Schema;
using ArtGander.Core.Storage;

namespace ArtGander.Core.Library;

/// <summary>Reads the whole art folder: each subfolder is one art piece.</summary>
public sealed class LibraryScanner(ArtPieceStore store)
{
    public ArtPieceStore Store => store;

    public IReadOnlyList<PieceFolder> Scan(string rootPath, CancellationToken ct = default)
    {
        if (!Directory.Exists(rootPath)) return [];

        var folders = EnumeratePieceFolders(rootPath).ToList();
        var results = new PieceFolder[folders.Count];
        // Network shares have high latency per file, so reading folders in parallel pays off a lot.
        Parallel.For(0, folders.Count, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = 8 },
            i => results[i] = ScanFolder(folders[i]));
        return results.OrderBy(p => p.Title, NameRules.Comparer).ToList();
    }

    public PieceFolder ScanFolder(string folderPath)
    {
        if (!ArtPieceStore.HasPieceFile(folderPath))
            return PieceFolder.From(folderPath, null);
        try
        {
            return PieceFolder.From(folderPath, store.Load(folderPath));
        }
        catch (ArtPieceFileException ex)
        {
            return PieceFolder.From(folderPath, null, ex.Message);
        }
    }

    public static IEnumerable<string> EnumeratePieceFolders(string rootPath)
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
            yield return dir;
        }
    }

    /// <summary>
    /// A cheap fingerprint of the library state (folder names plus metadata timestamps and sizes).
    /// Comparing fingerprints detects changes from other computers on shares where file-change notifications don't work.
    /// </summary>
    public static string Fingerprint(string rootPath)
    {
        var hash = new HashCode();
        foreach (var dir in EnumeratePieceFolders(rootPath).Order(StringComparer.Ordinal))
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

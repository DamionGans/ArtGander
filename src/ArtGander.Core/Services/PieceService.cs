using ArtGander.Core.Model;
using ArtGander.Core.Naming;
using ArtGander.Core.Storage;

namespace ArtGander.Core.Services;

/// <summary>
/// Every operation that changes files or folders in the art library. Each one keeps the folder,
/// the file names and <c>artgander.json</c> consistent with each other.
/// </summary>
public sealed partial class PieceService(ArtPieceStore store)
{
    public ArtPieceStore Store => store;

    /// <summary>Creates <c>{parent}/{Title}/.artgander.json</c>. The parent is the art folder, or a project folder.</summary>
    public ArtPieceDocument CreatePiece(string parentPath, string title, LibrarySettings? defaults = null,
        ItemType type = ItemType.Piece, WorkStatus status = WorkStatus.InProgress)
    {
        var folderName = NameRules.ToFolderName(title);
        var folder = Path.Combine(parentPath, folderName);
        if (Directory.Exists(folder))
            throw new IOException($"A folder named \"{folderName}\" already exists there.");

        var piece = new ArtPiece
        {
            Type = type,
            Status = status,
            Title = title.Trim(),
            CreatedOn = DateOnly.FromDateTime(DateTime.Today),
            Copyright = new Copyright
            {
                Owner = defaults?.DefaultCopyrightOwner ?? "",
                Year = DateTime.Today.Year,
            },
        };
        return store.Create(folder, piece);
    }

    /// <summary>
    /// Starts tracking an existing folder as an art piece or a project (<paramref name="type"/>; when null the
    /// folder's look decides, see <see cref="Library.FolderClassifier"/>). Existing files are tracked as they are,
    /// without renaming. For a project, its untracked art subfolders are tracked as its pieces.
    /// </summary>
    public ArtPieceDocument AdoptFolder(string folderPath, LibrarySettings? defaults = null, ItemType? type = null)
    {
        var folderName = Path.GetFileName(folderPath);
        var itemType = type ?? Library.FolderClassifier.Suggest(folderPath);
        var piece = new ArtPiece
        {
            Type = itemType,
            Status = WorkStatus.Finished,
            Title = folderName,
            CreatedOn = DateOnly.FromDateTime(Directory.GetCreationTime(folderPath)),
            Copyright = new Copyright { Owner = defaults?.DefaultCopyrightOwner ?? "", Year = Directory.GetCreationTime(folderPath).Year },
        };
        foreach (var file in Library.PieceFolder.ListArtFiles(folderPath))
        {
            var version = FileNamer.SuggestVersion(folderName, file);
            piece.Files.Add(NewEntry(file, version, GuessKind(version, Path.GetExtension(file), defaults?.FileKinds)));
        }
        var doc = store.Create(folderPath, piece);
        if (itemType == ItemType.Project) TrackProjectChildren(doc, defaults);
        return doc;
    }

    /// <summary>
    /// Adds a piece to a collection, editing the latest version of its details file on disk.
    /// </summary>
    /// <returns>False when the piece was already in the collection.</returns>
    public bool AddToCollection(string folderPath, string collection)
    {
        collection = collection.Trim();
        if (collection.Length == 0) throw new ArgumentException("The collection name can't be empty.", nameof(collection));
        var doc = store.Load(folderPath);
        if (doc.IsReadOnly) throw new InvalidOperationException(doc.ReadOnlyReason ?? "This art piece is read-only.");
        if (doc.Piece.Collections.Any(c => NameRules.NamesEqual(c, collection))) return false;
        doc.Piece.Collections.Add(collection);
        store.Save(doc);
        return true;
    }

    /// <summary>
    /// Copies (or moves) an outside file into the piece folder under the naming convention and tracks it.
    /// </summary>
    /// <returns>The new file entry.</returns>
    public FileEntry ImportFile(ArtPieceDocument doc, string sourcePath, string version, string kind, bool move = false)
    {
        EnsureNotChangedElsewhere(doc);
        var ext = Path.GetExtension(sourcePath);
        var label = FileNamer.MakeUnique(doc.FolderName, version, ext, n => File.Exists(doc.GetFilePath(n)) || doc.Piece.FindFile(n) is not null);
        var fileName = FileNamer.Build(doc.FolderName, label, ext);
        var target = doc.GetFilePath(fileName);

        if (move) File.Move(sourcePath, target);
        else File.Copy(sourcePath, target);

        var entry = NewEntry(fileName, label, kind);
        doc.Piece.Files.Add(entry);
        try
        {
            store.Save(doc);
        }
        catch
        {
            doc.Piece.Files.Remove(entry);
            if (move) File.Move(target, sourcePath); else AtomicFile.TryDelete(target);
            throw;
        }
        return entry;
    }

    /// <summary>Starts tracking a file that already sits in the piece folder. It can optionally be renamed to the convention.</summary>
    public FileEntry TrackFile(ArtPieceDocument doc, string fileName, string version, string kind, bool renameToConvention)
    {
        var entry = NewEntry(fileName, NameRules.ToVersionLabel(version), kind);
        doc.Piece.Files.Add(entry);
        if (renameToConvention)
            RenameFileOnDisk(doc, entry, entry.Version);
        store.Save(doc);
        return entry;
    }

    /// <summary>Stops tracking a file. The file itself stays on disk, so nothing is ever deleted.</summary>
    public void UntrackFile(ArtPieceDocument doc, FileEntry entry)
    {
        doc.Piece.Files.Remove(entry);
        foreach (var p in doc.Piece.Products)
            RemoveName(p.Files, entry.FileName);
        store.Save(doc);
    }

    /// <summary>Changes a file's version label. The file on disk is renamed and product links are updated.</summary>
    public void ChangeFileVersion(ArtPieceDocument doc, FileEntry entry, string newVersion)
    {
        RenameFileOnDisk(doc, entry, newVersion);
        store.Save(doc);
    }

    /// <summary>Renames a tracked file to the convention, keeping its current version label.</summary>
    public void ApplyConvention(ArtPieceDocument doc, FileEntry entry)
    {
        var version = string.IsNullOrWhiteSpace(entry.Version) ? FileNamer.SuggestVersion(doc.FolderName, entry.FileName) : entry.Version;
        RenameFileOnDisk(doc, entry, version);
        store.Save(doc);
    }

    /// <summary>
    /// Renames the piece: its title, its folder, and every file that follows the convention.
    /// Files are renamed first, then the folder. If anything fails, everything is put back.
    /// </summary>
    public void RenamePiece(ArtPieceDocument doc, string newTitle)
    {
        newTitle = newTitle.Trim();
        if (newTitle.Length == 0) throw new ArgumentException("The title can't be empty.", nameof(newTitle));
        EnsureNotChangedElsewhere(doc);

        var oldFolder = doc.FolderPath;
        var oldFolderName = doc.FolderName;
        var newFolderName = NameRules.ToFolderName(newTitle);
        var newFolder = Path.Combine(Path.GetDirectoryName(oldFolder)!, newFolderName);
        var oldTitle = doc.Piece.Title;

        var folderChanges = newFolderName != oldFolderName;
        if (folderChanges && !NameRules.NamesEqual(newFolderName, oldFolderName) && Directory.Exists(newFolder))
            throw new IOException($"A folder named \"{newFolderName}\" already exists in the art folder.");

        var done = new List<(string From, string To, FileEntry Entry, string OldName)>();
        try
        {
            if (folderChanges)
            {
                foreach (var entry in doc.Piece.Files.ToList())
                {
                    if (!FileNamer.TryParseVersion(oldFolderName, entry.FileName, out var version)) continue;
                    var newName = FileNamer.Build(newFolderName, version, Path.GetExtension(entry.FileName));
                    var from = Path.Combine(oldFolder, entry.FileName);
                    var to = Path.Combine(oldFolder, newName);
                    if (!File.Exists(from) || from == to) continue;
                    MoveSafe(from, to);
                    done.Add((from, to, entry, entry.FileName));
                    ReplaceFileReferences(doc.Piece, entry.FileName, newName);
                    entry.FileName = newName;
                }
                MoveDirectorySafe(oldFolder, newFolder);
                doc.FolderPath = newFolder;
            }

            doc.Piece.Title = newTitle;
            store.Save(doc);
        }
        catch
        {
            // Roll back in reverse order.
            if (doc.FolderPath != oldFolder && Directory.Exists(doc.FolderPath))
            {
                try { MoveDirectorySafe(doc.FolderPath, oldFolder); } catch (IOException) { }
            }
            doc.FolderPath = oldFolder;
            for (var i = done.Count - 1; i >= 0; i--)
            {
                var (from, to, entry, oldName) = done[i];
                try { MoveSafe(to, from); } catch (IOException) { }
                ReplaceFileReferences(doc.Piece, entry.FileName, oldName);
                entry.FileName = oldName;
            }
            doc.Piece.Title = oldTitle;
            throw;
        }
    }

    /// <summary>Refuses to touch files when the details file was changed elsewhere and the document must be reloaded first.</summary>
    private void EnsureNotChangedElsewhere(ArtPieceDocument doc)
    {
        if (doc.IsReadOnly) throw new InvalidOperationException(doc.ReadOnlyReason ?? "This art piece is read-only.");
        if (store.ChangedOnDisk(doc)) throw new ConcurrentEditException(doc.FilePath);
    }

    private void RenameFileOnDisk(ArtPieceDocument doc, FileEntry entry, string newVersion)
    {
        EnsureNotChangedElsewhere(doc);
        var ext = Path.GetExtension(entry.FileName);
        var desired = FileNamer.Build(doc.FolderName, newVersion, ext);
        if (desired == entry.FileName) { entry.Version = NameRules.ToVersionLabel(newVersion); return; }

        var label = NameRules.NamesEqual(desired, entry.FileName)
            ? NameRules.ToVersionLabel(newVersion) // only the case changes: the same file, so no uniqueness check needed
            : FileNamer.MakeUnique(doc.FolderName, newVersion, ext,
                n => (File.Exists(doc.GetFilePath(n)) && !NameRules.NamesEqual(n, entry.FileName)) || (doc.Piece.FindFile(n) is { } f && f != entry));
        var newName = FileNamer.Build(doc.FolderName, label, ext);

        var from = doc.GetFilePath(entry.FileName);
        if (File.Exists(from)) MoveSafe(from, doc.GetFilePath(newName));
        ReplaceFileReferences(doc.Piece, entry.FileName, newName);
        entry.FileName = newName;
        entry.Version = label;
    }

    private static void ReplaceFileReferences(ArtPiece piece, string oldName, string newName)
    {
        foreach (var p in piece.Products)
        {
            for (var i = 0; i < p.Files.Count; i++)
                if (NameRules.NamesEqual(p.Files[i], oldName)) p.Files[i] = newName;
        }
    }

    private static void RemoveName(IList<string> list, string name)
    {
        for (var i = list.Count - 1; i >= 0; i--)
            if (NameRules.NamesEqual(list[i], name)) list.RemoveAt(i);
    }

    /// <summary>Renames a file, including case-only renames, which need a temporary step on case-insensitive file systems.</summary>
    internal static void MoveSafe(string from, string to)
    {
        if (NameRules.NamesEqual(from, to) && from != to)
        {
            var tmp = from + ".rename-" + Guid.NewGuid().ToString("N")[..8];
            File.Move(from, tmp);
            File.Move(tmp, to);
        }
        else
        {
            File.Move(from, to);
        }
    }

    internal static void MoveDirectorySafe(string from, string to)
    {
        if (from == to) return;
        if (NameRules.NamesEqual(from, to))
        {
            var tmp = from + ".rename-" + Guid.NewGuid().ToString("N")[..8];
            Directory.Move(from, tmp);
            Directory.Move(tmp, to);
        }
        else
        {
            Directory.Move(from, to);
        }
    }

    private static FileEntry NewEntry(string fileName, string version, string? kind = null) => new()
    {
        FileName = fileName,
        Version = version,
        Kind = kind ?? GuessKind(version, Path.GetExtension(fileName)),
        AddedAt = DateTimeOffset.Now,
    };

    /// <summary>
    /// Guesses the file kind from the version label and extension. The library's own kinds win when the label
    /// contains one (e.g. kind "sticker sheet" for version "sticker-sheet-a5"); otherwise common words are recognized,
    /// e.g. "transparent" or "no-bg" map to <see cref="FileKind.Transparent"/>.
    /// </summary>
    public static string GuessKind(string version, string extension, IEnumerable<string>? libraryKinds = null)
    {
        var v = version.ToLowerInvariant();
        var custom = libraryKinds?
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .OrderByDescending(k => k.Length)
            .FirstOrDefault(k => v.Contains(NameRules.ToVersionLabel(k), StringComparison.Ordinal));
        if (custom is not null) return custom;
        if (v.Contains("transparent") || v.Contains("transp") || v.Contains("no-bg") || v.Contains("nobg")) return FileKind.Transparent;
        if (v.Contains("photo") || v.Contains("foto") || v.Contains("raw")) return FileKind.Photo;
        if (v.Contains("scan")) return FileKind.Scan;
        if (v.Contains("mockup") || v.Contains("mock")) return FileKind.Mockup;
        if (v.Contains("print")) return FileKind.Print;
        if (v.Contains("master") || v.Contains("original")) return FileKind.Master;
        if (v.Contains("edit")) return FileKind.Edit;
        return NameRules.NormalizeExtension(extension) is ".psd" or ".kra" or ".procreate" or ".clip" or ".afphoto" or ".xcf"
            ? FileKind.Master
            : FileKind.Other;
    }
}

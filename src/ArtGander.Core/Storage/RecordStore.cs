using System.Text;
using System.Text.Json;
using ArtGander.Core.Model;
using ArtGander.Core.Schema;

namespace ArtGander.Core.Storage;

/// <summary>
/// Stores records (products, orders) as one JSON file each in <c>{ArtRoot}/.artgander/{folder}/</c>.
/// Writes are atomic, and a save fails with <see cref="ConcurrentEditException"/> when the file was changed
/// (e.g. on the other computer) after it was loaded, so nobody's edits get overwritten silently.
/// </summary>
public sealed class RecordStore<T>(
    string folderName,
    Func<T, string> fileStem,
    Func<string, T?> deserialize,
    Func<T, string> serialize,
    Func<T, int> schemaVersion,
    int supportedVersion,
    Action<T>? beforeSave = null)
    where T : ModelBase, IStoredRecord
{
    public string FolderName => folderName;

    public string FolderFor(string rootPath) => Path.Combine(rootPath, CurrentSchema.LibraryFolderName, folderName);

    public string FileNameFor(T record) => fileStem(record) + ".json";

    public IReadOnlyList<T> LoadAll(string rootPath)
    {
        var folder = FolderFor(rootPath);
        if (!Directory.Exists(folder)) return [];
        var result = new List<T>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
        {
            if (AtomicFile.IsTempFile(Path.GetFileName(file))) continue;
            if (TryLoadFile(file) is { } record) result.Add(record);
        }
        // Two files with the same id (e.g. a rename interrupted halfway): keep the newest.
        return result.GroupBy(r => r.Id).Select(g => g.OrderByDescending(r => File.GetLastWriteTimeUtc(Path.Combine(folder, r.StoredFileName!))).First()).ToList();
    }

    /// <summary>Loads the current version of a record from disk, or null when its file is gone.</summary>
    public T? Reload(string rootPath, T record) =>
        record.StoredFileName is { } name && TryLoadFile(Path.Combine(FolderFor(rootPath), name)) is { } fresh && fresh.Id == record.Id ? fresh : null;

    /// <summary>Checks whether the record's file changed on disk since it was loaded or saved.</summary>
    public bool ChangedOnDisk(string rootPath, T record) =>
        record.StoredFileName is { } name && record.StoredHash is { } hash && HashOf(Path.Combine(FolderFor(rootPath), name)) is { } current && current != hash;

    public bool IsReadOnly(T record) => schemaVersion(record) > supportedVersion;

    /// <summary>
    /// Saves a record. The file is renamed when its name depends on a field that changed (e.g. the order number).
    /// </summary>
    /// <exception cref="ConcurrentEditException">The file was changed by someone else after it was loaded.</exception>
    /// <exception cref="IOException">Another record already uses the file name.</exception>
    public void Save(string rootPath, T record, bool force = false)
    {
        if (IsReadOnly(record))
            throw new InvalidOperationException($"This {folderName.TrimEnd('s')} was saved by a newer ArtGander. Please update ArtGander to change it.");

        var folder = FolderFor(rootPath);
        Directory.CreateDirectory(folder);
        AtomicFile.MarkHidden(Path.GetDirectoryName(folder)!);

        var previous = record.StoredFileName;
        if (!force && previous is not null && record.StoredHash is { } loaded
            && HashOf(Path.Combine(folder, previous)) is { } current && current != loaded)
            throw new ConcurrentEditException(Path.Combine(folder, previous), $"This {folderName.TrimEnd('s')} was changed on another computer after you opened it.");

        var fileName = FileNameFor(record);
        var path = Path.Combine(folder, fileName);
        var renamed = previous is not null && !Naming.NameRules.NamesEqual(previous, fileName);
        if ((previous is null || renamed) && File.Exists(path) && TryLoadFile(path) is { } other && other.Id != record.Id)
            throw new IOException($"Another {folderName.TrimEnd('s')} already uses the name \"{fileStem(record)}\".");

        beforeSave?.Invoke(record);
        var json = serialize(record);
        AtomicFile.WriteAllText(path, json);
        record.StoredFileName = fileName;
        record.StoredHash = AtomicFile.Hash(Encoding.UTF8.GetBytes(json));
        if (renamed) AtomicFile.TryDelete(Path.Combine(folder, previous!));
    }

    public void Delete(string rootPath, T record)
    {
        if (record.StoredFileName is { } name) AtomicFile.TryDelete(Path.Combine(FolderFor(rootPath), name));
        record.StoredFileName = null;
        record.StoredHash = null;
    }

    private T? TryLoadFile(string path)
    {
        try
        {
            var bytes = AtomicFile.ReadAllBytes(path);
            var record = deserialize(AtomicFile.DecodeUtf8(bytes));
            if (record is null) return null;
            record.StoredFileName = Path.GetFileName(path);
            record.StoredHash = AtomicFile.Hash(bytes);
            return record;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }

    private static string? HashOf(string path)
    {
        try { return File.Exists(path) ? AtomicFile.Hash(AtomicFile.ReadAllBytes(path)) : null; }
        catch (IOException) { return null; }
    }
}

/// <summary>The stores for the library-level records.</summary>
public static class Records
{
    /// <summary>Products as stored by format 3 (<c>.artgander/products/*.json</c>); only read to upgrade them to product folders.</summary>
    public static RecordStore<Product> LegacyProducts { get; } = new(
        "products",
        p => $"{Naming.NameRules.ToFileBaseName(Naming.NameRules.ToFolderName(string.IsNullOrWhiteSpace(p.Name) ? "Product" : p.Name))}-{p.Id.ToString("N")[..8]}",
        ArtGanderJson.DeserializeProduct,
        ArtGanderJson.Serialize,
        p => p.SchemaVersion,
        Product.CurrentSchemaVersion,
        p =>
        {
            p.SchemaVersion = Product.CurrentSchemaVersion;
            p.CreatedAt ??= DateTimeOffset.Now;
            p.UpdatedAt = DateTimeOffset.Now;
            p.UpdatedBy = Environment.MachineName;
        });

    public static RecordStore<Order> Orders { get; } = new(
        "orders",
        o => string.IsNullOrWhiteSpace(o.OrderRef) ? "order-" + o.Id.ToString("N")[..8] : Naming.NameRules.ToVersionLabel(o.OrderRef),
        ArtGanderJson.DeserializeOrder,
        ArtGanderJson.Serialize,
        o => o.SchemaVersion,
        Order.CurrentSchemaVersion,
        o =>
        {
            o.SchemaVersion = Order.CurrentSchemaVersion;
            o.UpdatedAt = DateTimeOffset.Now;
            o.UpdatedBy = Environment.MachineName;
        });
}

using System.Text;
using System.Text.Json;
using ArtGander.Core.Model;
using ArtGander.Core.Naming;
using ArtGander.Core.Schema;

namespace ArtGander.Core.Storage;

/// <summary>A product folder that isn't where its links say it belongs (e.g. moved by hand), and where it should go.</summary>
public sealed record ProductMove(Product Product, string From, string To)
{
    public string Description => $"{Product.Name}: {From} → {To}";
}

/// <summary>
/// Stores each product in its own visible folder with its images (format v4):
/// <c>{ArtRoot}/Products/{Art piece}/{Product}/.artgander-product.json</c> for a product of exactly one art piece or
/// project, and <c>{ArtRoot}/Products/{Product}/</c> for everything else. Writes are atomic, and a save fails with
/// <see cref="ConcurrentEditException"/> when the file was changed elsewhere after it was loaded.
/// </summary>
public sealed class ProductStore
{
    public const string FileName = ".artgander-product.json";
    /// <summary>Hidden marker in the products folder, so it's recognized even after the setting changed.</summary>
    public const string MarkerFileName = ".artgander-products";
    public const string DefaultFolderName = "Products";

    private readonly string _rootPath;
    private readonly Func<Guid, string?> _groupNameFor;

    /// <param name="groupNameFor">The folder name of an art piece or project, by id; null when it's unknown.</param>
    public ProductStore(string rootPath, string? productsFolderName, Func<Guid, string?>? groupNameFor = null)
    {
        _rootPath = rootPath;
        FolderName = string.IsNullOrWhiteSpace(productsFolderName) ? DefaultFolderName : productsFolderName.Trim();
        _groupNameFor = groupNameFor ?? (_ => null);
    }

    public string FolderName { get; }
    public string ProductsPath => Path.Combine(_rootPath, FolderName);

    // ---- Reading ------------------------------------------------------------------------------------

    /// <summary>Every product: in a product folder directly under Products, or one level deeper in a group folder.</summary>
    public IReadOnlyList<Product> LoadAll()
    {
        if (!Directory.Exists(ProductsPath)) return [];
        var found = new List<Product>();
        foreach (var dir in SubDirectories(ProductsPath))
        {
            var file = Path.Combine(dir, FileName);
            if (File.Exists(file))
            {
                if (TryLoadFile(file) is { } p) found.Add(p);
                continue;
            }
            foreach (var inner in SubDirectories(dir)) // a group folder
                if (TryLoadFile(Path.Combine(inner, FileName)) is { } p) found.Add(p);
        }
        // Two folders with the same id (e.g. copied by hand): keep the newest.
        return found.GroupBy(p => p.Id)
            .Select(g => g.OrderByDescending(p => File.GetLastWriteTimeUtc(p.StoredFileName!)).First())
            .ToList();
    }

    public static string? FolderOf(Product product) => product.StoredFileName is { } f ? Path.GetDirectoryName(f) : null;

    public Product? Reload(Product product) =>
        product.StoredFileName is { } file && TryLoadFile(file) is { } fresh && fresh.Id == product.Id ? fresh : null;

    public bool ChangedOnDisk(Product product) =>
        product.StoredFileName is { } file && product.StoredHash is { } hash && HashOf(file) is { } current && current != hash;

    public static bool IsReadOnly(Product product) => product.SchemaVersion > Product.CurrentSchemaVersion;

    // ---- Where a product belongs --------------------------------------------------------------------

    /// <summary>The name of the group folder for this product (its one art piece or project), or null for the top level.</summary>
    public string? GroupNameFor(Product product)
    {
        if (product.Pieces.Count != 1) return null;
        var name = _groupNameFor(product.Pieces[0]);
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    /// <summary>The folder a product's folder belongs in: a group folder under Products, or Products itself.</summary>
    public string ExpectedParent(Product product) =>
        GroupNameFor(product) is { } group ? GroupPath(group) : ProductsPath;

    /// <summary>
    /// The group folder for an art piece. When a product folder already uses that name at the top level, the group
    /// gets " - products" added so the two stay apart.
    /// </summary>
    private string GroupPath(string group)
    {
        var path = Path.Combine(ProductsPath, group);
        return File.Exists(Path.Combine(path, FileName)) ? Path.Combine(ProductsPath, group + " - products") : path;
    }

    public static string ProductFolderName(Product product) =>
        NameRules.ToFolderName(string.IsNullOrWhiteSpace(product.Name) ? "Product" : product.Name);

    /// <summary>A free folder for the product in <paramref name="parent"/>: its name, or "Name (2)", "Name (3)"…</summary>
    private static string FreeFolder(string parent, string name, string? own)
    {
        var candidate = Path.Combine(parent, name);
        for (var i = 2; Directory.Exists(candidate) && !SamePath(candidate, own); i++)
            candidate = Path.Combine(parent, $"{name} ({i})");
        return candidate;
    }

    /// <summary>Products whose folder isn't where their links say it belongs.</summary>
    public IReadOnlyList<ProductMove> PlanTidy(IEnumerable<Product> products)
    {
        var moves = new List<ProductMove>();
        foreach (var p in products)
        {
            if (FolderOf(p) is not { } folder || IsReadOnly(p)) continue;
            if (MoveTarget(p) is { } to) moves.Add(new ProductMove(p, Relative(folder), Relative(to)));
        }
        return moves;
    }

    /// <summary>Where the product's folder would move to, or null when it's already in the right place.</summary>
    public string? MoveTarget(Product product)
    {
        if (FolderOf(product) is not { } folder) return null;
        var parent = ExpectedParent(product);
        if (SamePath(Path.GetDirectoryName(folder), parent)) return null;
        return FreeFolder(parent, Path.GetFileName(folder), own: null);
    }

    public string Relative(string path) => Path.GetRelativePath(_rootPath, path);

    // ---- Writing ------------------------------------------------------------------------------------

    /// <summary>
    /// Saves a product. A new product gets a folder where it belongs; a renamed product's folder is renamed in place
    /// (moving it to another group is a separate, confirmed step: <see cref="Move"/>).
    /// </summary>
    /// <exception cref="ConcurrentEditException">The product was changed elsewhere after it was loaded.</exception>
    public void Save(Product product, bool force = false)
    {
        if (IsReadOnly(product))
            throw new InvalidOperationException("This product was saved by a newer ArtGander. Please update ArtGander to change it.");
        EnsureProductsFolder();

        var file = product.StoredFileName;
        if (!force && file is not null && product.StoredHash is { } loaded && HashOf(file) is { } current && current != loaded)
            throw new ConcurrentEditException(file, "This product was changed on another computer after you opened it.");

        var folder = file is null || !Directory.Exists(Path.GetDirectoryName(file))
            ? FreeFolder(ExpectedParent(product), ProductFolderName(product), own: null)
            : Path.GetDirectoryName(file)!;
        var oldFolder = file is null ? null : Path.GetDirectoryName(file);

        // A renamed product: rename its folder (and its images) where it is.
        var wanted = ProductFolderName(product);
        string? renamedFrom = null;
        List<(string From, string To)> renamedImages = [];
        if (oldFolder is not null && Directory.Exists(oldFolder) && !NameRules.NamesEqual(Path.GetFileName(oldFolder), wanted)
            && !IsNumberedVariant(Path.GetFileName(oldFolder), wanted))
        {
            var target = FreeFolder(Path.GetDirectoryName(oldFolder)!, wanted, own: oldFolder);
            renamedImages = RenameImages(product, ConventionName(Path.GetFileName(oldFolder)), ConventionName(Path.GetFileName(target)), oldFolder);
            try { MoveDirectory(oldFolder, target); }
            catch { UndoImages(product, renamedImages, oldFolder); throw; }
            folder = target;
            renamedFrom = oldFolder;
        }
        else if (oldFolder is not null && Directory.Exists(oldFolder) && Path.GetFileName(oldFolder) != wanted
                 && NameRules.NamesEqual(Path.GetFileName(oldFolder), wanted))
        {
            // Only the case changed.
            var target = Path.Combine(Path.GetDirectoryName(oldFolder)!, wanted);
            renamedImages = RenameImages(product, ConventionName(Path.GetFileName(oldFolder)), wanted, oldFolder);
            MoveDirectory(oldFolder, target);
            folder = target;
            renamedFrom = oldFolder;
        }

        try
        {
            Directory.CreateDirectory(folder);
            Write(product, Path.Combine(folder, FileName));
        }
        catch
        {
            if (renamedFrom is not null)
            {
                try { MoveDirectory(folder, renamedFrom); } catch (IOException) { }
                UndoImages(product, renamedImages, renamedFrom);
            }
            throw;
        }
        if (renamedFrom is not null) RemoveEmptyGroup(Path.GetDirectoryName(renamedFrom)!);
    }

    /// <summary>Moves a product's folder (with its images) to where its links say it belongs. Returns false when it already is there.</summary>
    public bool Move(Product product)
    {
        if (MoveTarget(product) is not { } to || FolderOf(product) is not { } from) return false;
        if (ChangedOnDisk(product)) throw new ConcurrentEditException(product.StoredFileName!);
        EnsureProductsFolder();
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        MoveDirectory(from, to);
        product.StoredFileName = Path.Combine(to, FileName);
        RemoveEmptyGroup(Path.GetDirectoryName(from)!);
        return true;
    }

    /// <summary>
    /// After an art piece was renamed: renames its group folder, so its products stay grouped under the new name.
    /// Updates the given products' paths. Nothing happens when there is no group folder.
    /// </summary>
    public void RenameGroup(string oldGroupName, string newGroupName, IEnumerable<Product> loaded)
    {
        if (string.IsNullOrWhiteSpace(oldGroupName) || oldGroupName == newGroupName) return;
        var from = Path.Combine(ProductsPath, oldGroupName);
        if (!Directory.Exists(from) || File.Exists(Path.Combine(from, FileName))) return;
        var to = GroupPath(newGroupName);
        if (Directory.Exists(to) && !SamePath(from, to))
        {
            // The new group already exists: move the product folders over one by one.
            foreach (var dir in SubDirectories(from))
                MoveDirectory(dir, FreeFolder(to, Path.GetFileName(dir), own: null));
            RemoveEmptyGroup(from);
        }
        else
        {
            MoveDirectory(from, to);
        }
        foreach (var p in loaded)
        {
            if (p.StoredFileName is { } file && file.StartsWith(from + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(Path.GetDirectoryName(file)!);
                var moved = Path.Combine(to, name, FileName);
                p.StoredFileName = File.Exists(moved) ? moved : FindById(p.Id)?.StoredFileName ?? moved;
            }
        }
    }

    private Product? FindById(Guid id) => LoadAll().FirstOrDefault(p => p.Id == id);

    /// <summary>Deletes the product's details file. Its images stay; the folder is removed only when nothing else is in it.</summary>
    public void Delete(Product product)
    {
        if (product.StoredFileName is { } file)
        {
            AtomicFile.TryDelete(file);
            var folder = Path.GetDirectoryName(file)!;
            try
            {
                if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                {
                    Directory.Delete(folder);
                    RemoveEmptyGroup(Path.GetDirectoryName(folder)!);
                }
            }
            catch (IOException) { }
        }
        product.StoredFileName = null;
        product.StoredHash = null;
    }

    /// <summary>Writes the product's details into <paramref name="folder"/> (used by the upgrade from format 3).</summary>
    internal void WriteInto(Product product, string folder)
    {
        EnsureProductsFolder();
        Directory.CreateDirectory(folder);
        Write(product, Path.Combine(folder, FileName));
    }

    /// <summary>A free folder where a new product would go.</summary>
    internal string NewFolderFor(Product product) => FreeFolder(ExpectedParent(product), ProductFolderName(product), own: null);

    private static void Write(Product product, string path)
    {
        product.SchemaVersion = Product.CurrentSchemaVersion;
        product.CreatedAt ??= DateTimeOffset.Now;
        product.UpdatedAt = DateTimeOffset.Now;
        product.UpdatedBy = Environment.MachineName;
        var json = ArtGanderJson.Serialize(product);
        AtomicFile.WriteAllText(path, json);
        AtomicFile.MarkHidden(path);
        product.StoredFileName = path;
        product.StoredHash = AtomicFile.Hash(Encoding.UTF8.GetBytes(json));
    }

    public void EnsureProductsFolder()
    {
        Directory.CreateDirectory(ProductsPath);
        var marker = Path.Combine(ProductsPath, MarkerFileName);
        if (!File.Exists(marker))
        {
            AtomicFile.WriteAllText(marker, "This folder holds ArtGander's products: one folder per product, grouped per art piece.\n");
            AtomicFile.MarkHidden(marker);
        }
    }

    // ---- Images follow the product's name -----------------------------------------------------------

    /// <summary>Renames the product's images that follow the convention {Product_Name}-{label}.{ext} for a new folder name.</summary>
    private static List<(string From, string To)> RenameImages(Product product, string oldName, string newName, string folder)
    {
        var done = new List<(string, string)>();
        try
        {
            foreach (var image in product.Images)
            {
                if (!FileNamer.TryParseVersion(oldName, image.FileName, out var label)) continue;
                var renamed = FileNamer.Build(newName, label, Path.GetExtension(image.FileName));
                var from = Path.Combine(folder, image.FileName);
                if (renamed == image.FileName || !File.Exists(from)) { image.FileName = renamed; continue; }
                Services.PieceService.MoveSafe(from, Path.Combine(folder, renamed));
                done.Add((image.FileName, renamed));
                image.FileName = renamed;
            }
        }
        catch
        {
            UndoImages(product, done, folder);
            throw;
        }
        return done;
    }

    private static void UndoImages(Product product, List<(string From, string To)> done, string folder)
    {
        for (var i = done.Count - 1; i >= 0; i--)
        {
            var (from, to) = done[i];
            try { Services.PieceService.MoveSafe(Path.Combine(folder, to), Path.Combine(folder, from)); } catch (IOException) { }
            if (product.Images.FirstOrDefault(im => im.FileName == to) is { } image) image.FileName = from;
        }
    }

    // ---- Helpers ------------------------------------------------------------------------------------

    /// <summary>The name a product's pictures follow: the folder name without a " (2)" added for uniqueness.</summary>
    public static string ConventionName(string folderName)
    {
        var open = folderName.LastIndexOf(" (", StringComparison.Ordinal);
        return open > 0 && folderName.EndsWith(')') && int.TryParse(folderName[(open + 2)..^1], out _) ? folderName[..open] : folderName;
    }

    /// <summary>"Goose Mug (2)" is fine for a product called "Goose Mug": it was numbered because the name was taken.</summary>
    private static bool IsNumberedVariant(string folderName, string wanted) =>
        folderName.StartsWith(wanted + " (", StringComparison.OrdinalIgnoreCase) && folderName.EndsWith(')')
        && int.TryParse(folderName[(wanted.Length + 2)..^1], out _);

    private void RemoveEmptyGroup(string folder)
    {
        if (SamePath(folder, ProductsPath) || !SamePath(Path.GetDirectoryName(folder), ProductsPath)) return;
        try
        {
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void MoveDirectory(string from, string to) => Services.PieceService.MoveDirectorySafe(from, to);

    private static IEnumerable<string> SubDirectories(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path)
                .Where(d => Path.GetFileName(d) is var n && !n.StartsWith('.') && !n.StartsWith('$') && !AtomicFile.IsTempFile(n))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static bool SamePath(string? a, string? b) =>
        a is not null && b is not null
        && string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    private static Product? TryLoadFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var bytes = AtomicFile.ReadAllBytes(path);
            var product = ArtGanderJson.DeserializeProduct(AtomicFile.DecodeUtf8(bytes));
            if (product is null) return null;
            product.StoredFileName = path;
            product.StoredHash = AtomicFile.Hash(bytes);
            return product;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }

    private static string? HashOf(string path)
    {
        try { return File.Exists(path) ? AtomicFile.Hash(AtomicFile.ReadAllBytes(path)) : null; }
        catch (IOException) { return null; }
    }
}

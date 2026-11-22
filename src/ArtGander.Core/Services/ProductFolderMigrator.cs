using ArtGander.Core.Model;
using ArtGander.Core.Naming;
using ArtGander.Core.Schema;
using ArtGander.Core.Storage;

namespace ArtGander.Core.Services;

/// <summary>
/// Upgrades products from format 3 (<c>.artgander/products/{Name}-{id}.json</c>) to format 4: one visible folder per
/// product, grouped per art piece, with the details file inside (see <see cref="ProductStore"/>).
/// Safe to run again at any time: a product that already has a folder is only updated when the old file is newer
/// (e.g. saved by an older ArtGander on another computer), and every old file is kept in a backup folder.
/// </summary>
public static class ProductFolderMigrator
{
    public sealed record Result(int Moved, int Updated, string ProductsFolder, bool FolderNameChanged, IReadOnlyList<string> Problems);

    public static string LegacyFolder(string rootPath) => Records.LegacyProducts.FolderFor(rootPath);

    public static bool NeedsMigration(string rootPath)
    {
        var dir = LegacyFolder(rootPath);
        try { return Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*.json").Any(f => !AtomicFile.IsTempFile(Path.GetFileName(f))); }
        catch (IOException) { return false; }
    }

    /// <summary>
    /// The products folder to use. "Products" unless that name is already taken by something that isn't ArtGander's
    /// products folder (e.g. an art piece called "Products"), then "Shop Products".
    /// </summary>
    public static string ChooseProductsFolder(string rootPath, string current)
    {
        foreach (var name in new[] { current, "Shop Products", "ArtGander Products" })
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            var path = Path.Combine(rootPath, name);
            if (!Directory.Exists(path)) return name;
            if (File.Exists(Path.Combine(path, ProductStore.MarkerFileName))) return name;
            if (!ArtPieceStore.HasPieceFile(path) && !Directory.EnumerateFileSystemEntries(path).Any()) return name;
        }
        return "ArtGander Products " + DateTime.Now.ToString("yyyy-MM-dd");
    }

    /// <summary>Moves every old product file into its folder. Takes the library lock while working.</summary>
    /// <param name="settings">The library settings; the products folder name is set (and saved) when it has to change.</param>
    public static Result Run(string rootPath, ProductCatalog catalog, LibrarySettings settings)
    {
        var problems = new List<string>();
        var folderName = ChooseProductsFolder(rootPath, settings.ProductsFolder);
        var nameChanged = !NameRules.NamesEqual(folderName, settings.ProductsFolder);
        if (!NeedsMigration(rootPath)) return new Result(0, 0, folderName, false, problems);

        using var libraryLock = LibraryLock.Acquire(rootPath, "upgrading the products");
        if (nameChanged)
        {
            settings.ProductsFolder = folderName;
            LibrarySettingsStore.Save(rootPath, settings);
        }
        if (catalog.RootPath != rootPath || !NameRules.NamesEqual(catalog.ProductsFolderName, folderName))
        {
            var resolver = catalog.GroupNameFor;
            catalog.Load(rootPath, folderName);
            catalog.GroupNameFor = resolver;
        }

        var legacyDir = LegacyFolder(rootPath);
        var backup = Path.Combine(rootPath, CurrentSchema.LibraryFolderName, "backup", $"v3-products-{DateTime.Now:yyyy-MM-dd}");
        int moved = 0, updated = 0;
        foreach (var file in Directory.EnumerateFiles(legacyDir, "*.json").Where(f => !AtomicFile.IsTempFile(Path.GetFileName(f))).ToList())
        {
            try
            {
                Product? legacy;
                try { legacy = ArtGanderJson.DeserializeProduct(AtomicFile.DecodeUtf8(AtomicFile.ReadAllBytes(file))); }
                catch (System.Text.Json.JsonException ex) { problems.Add($"{Path.GetFileName(file)}: {ex.Message}"); continue; }
                if (legacy is null) continue;
                legacy.StoredFileName = null;
                legacy.StoredHash = null;

                if (catalog.Find(legacy.Id) is { } existing)
                {
                    // Already upgraded (e.g. on another computer). An older ArtGander may have saved it again since.
                    if ((legacy.UpdatedAt ?? DateTimeOffset.MinValue) > (existing.UpdatedAt ?? DateTimeOffset.MinValue))
                    {
                        legacy.StoredFileName = existing.StoredFileName;
                        legacy.StoredHash = existing.StoredHash;
                        foreach (var image in existing.Images) legacy.Images.Add(image); // format 3 had no pictures
                        catalog.Save(legacy, force: true);
                        updated++;
                    }
                }
                else
                {
                    catalog.Save(legacy);
                    moved++;
                }

                Directory.CreateDirectory(backup);
                var target = Path.Combine(backup, Path.GetFileName(file));
                for (var i = 2; File.Exists(target); i++)
                    target = Path.Combine(backup, $"{Path.GetFileNameWithoutExtension(file)} ({i}).json");
                File.Move(file, target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ConcurrentEditException)
            {
                problems.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
            libraryLock.Heartbeat();
        }
        try
        {
            if (!Directory.EnumerateFileSystemEntries(legacyDir).Any()) Directory.Delete(legacyDir);
        }
        catch (IOException) { }
        return new Result(moved, updated, folderName, nameChanged, problems);
    }
}

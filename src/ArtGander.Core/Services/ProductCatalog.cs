using ArtGander.Core.Model;
using ArtGander.Core.Naming;
using ArtGander.Core.Storage;

namespace ArtGander.Core.Services;

/// <summary>
/// The library's products (<c>Products/{Art piece}/{Product}/.artgander-product.json</c>, see <see cref="ProductStore"/>),
/// with the many-to-many links to art pieces.
/// </summary>
public sealed class ProductCatalog
{
    private readonly Lock _gate = new();
    private List<Product> _all = [];

    public string? RootPath { get; private set; }

    /// <summary>The name of the folder holding the products (library setting, default "Products").</summary>
    public string ProductsFolderName { get; private set; } = ProductStore.DefaultFolderName;

    /// <summary>The folder name of an art piece or project by id, so products of one piece are grouped under it. Set by the app.</summary>
    public Func<Guid, string?>? GroupNameFor { get; set; }

    public ProductStore Store => new(RequireRoot(), ProductsFolderName, id => GroupNameFor?.Invoke(id));

    public IReadOnlyList<Product> All
    {
        get { lock (_gate) return _all; }
    }

    /// <summary>Loads every product of the library at <paramref name="rootPath"/>.</summary>
    public void Load(string rootPath, string? productsFolder = null) =>
        SetLoaded(rootPath, new ProductStore(rootPath, productsFolder).LoadAll(), productsFolder);

    public void SetLoaded(string rootPath, IReadOnlyList<Product> products, string? productsFolder = null)
    {
        lock (_gate)
        {
            RootPath = rootPath;
            ProductsFolderName = string.IsNullOrWhiteSpace(productsFolder) ? ProductStore.DefaultFolderName : productsFolder.Trim();
            _all = products.OrderBy(p => p.Name, NameRules.NaturalComparer).ToList();
        }
    }

    public Product? Find(Guid? id) => id is null ? null : All.FirstOrDefault(p => p.Id == id);

    public IReadOnlyDictionary<Guid, Product> ById => All.GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First());

    /// <summary>The products that show this art piece (linked directly, through one of its files, or as part of a bundle).</summary>
    public IReadOnlyList<Product> ForPiece(Guid pieceId) => All.Where(p => Shows(p, pieceId)).ToList();

    public static bool Shows(Product p, Guid pieceId) =>
        p.Pieces.Contains(pieceId) || p.Files.Any(f => f.PieceId == pieceId) || p.Includes.Any(i => i.PieceId == pieceId);

    /// <summary>Saves a product, adding it to the catalog when it's new.</summary>
    /// <exception cref="ConcurrentEditException">The product was changed on another computer after it was loaded.</exception>
    public void Save(Product product, bool force = false)
    {
        Store.Save(product, force);
        lock (_gate)
        {
            var i = _all.FindIndex(p => p.Id == product.Id);
            var copy = _all.ToList();
            if (i >= 0) copy[i] = product; else copy.Add(product);
            _all = copy;
        }
    }

    public void Delete(Product product)
    {
        Store.Delete(product);
        lock (_gate) _all = _all.Where(p => p.Id != product.Id).ToList();
    }

    /// <summary>Loads the current version of a product from disk (e.g. after another computer changed it).</summary>
    public Product? Reload(Product product)
    {
        RequireRoot();
        var fresh = Store.Reload(product);
        if (fresh is not null)
        {
            lock (_gate)
            {
                var copy = _all.ToList();
                var i = copy.FindIndex(p => p.Id == product.Id);
                if (i >= 0) copy[i] = fresh;
                _all = copy;
            }
        }
        return fresh;
    }

    /// <summary>Links a piece to a product (and back). Saves the product.</summary>
    public void Link(Product product, Guid pieceId)
    {
        if (product.Pieces.Contains(pieceId)) return;
        product.Pieces.Add(pieceId);
        Save(product);
    }

    /// <summary>Removes the link between a piece and a product, including its file links. Saves the product.</summary>
    public void Unlink(Product product, Guid pieceId)
    {
        product.Pieces.Remove(pieceId);
        foreach (var f in product.Files.Where(f => f.PieceId == pieceId).ToList()) product.Files.Remove(f);
        Save(product);
    }

    /// <summary>
    /// Updates the products that use a file after it was renamed (<paramref name="newName"/>) or untracked (null).
    /// A product changed on another computer in the meantime is reloaded first, so its edits are kept.
    /// </summary>
    public void ReplaceFileReference(Guid pieceId, string oldName, string? newName)
    {
        if (RootPath is null) return;
        foreach (var product in All.Where(p => p.Files.Any(f => Matches(f, pieceId, oldName))).ToList())
        {
            var target = ChangedOnDisk(product) ? Reload(product) ?? product : product;
            foreach (var f in target.Files.Where(f => Matches(f, pieceId, oldName)).ToList())
            {
                if (newName is null) target.Files.Remove(f);
                else f.FileName = newName;
            }
            Save(target, force: true);
        }
    }

    public bool ChangedOnDisk(Product product) => RootPath is not null && Store.ChangedOnDisk(product);

    /// <summary>The product's folder (with its pictures), or null when it was never saved.</summary>
    public static string? FolderOf(Product product) => ProductStore.FolderOf(product);

    /// <summary>Where the product's folder would move to because of its links (relative to the art folder), or null when it's in place.</summary>
    public string? PendingMove(Product product) =>
        RootPath is not null && Store.MoveTarget(product) is { } to ? Store.Relative(to) : null;

    /// <summary>Moves the product's folder to where its links say it belongs. Returns false when it already was there.</summary>
    public bool MoveToPlace(Product product) => Store.Move(product);

    /// <summary>Products whose folder isn't where their links say it belongs (e.g. moved by hand).</summary>
    public IReadOnlyList<ProductMove> PlanTidy() => RootPath is null ? [] : Store.PlanTidy(All);

    /// <summary>Moves every misplaced product folder. Returns the problems, if any.</summary>
    public IReadOnlyList<string> Tidy()
    {
        var problems = new List<string>();
        foreach (var move in PlanTidy())
        {
            try { MoveToPlace(move.Product); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ConcurrentEditException) { problems.Add($"{move.Product.Name}: {ex.Message}"); }
        }
        return problems;
    }

    /// <summary>After an art piece was renamed: its products' group folder follows the new name.</summary>
    public void RenameGroup(string oldFolderName, string newFolderName)
    {
        if (RootPath is null) return;
        Store.RenameGroup(oldFolderName, newFolderName, All);
    }

    private static bool Matches(ProductFile f, Guid pieceId, string name) => f.PieceId == pieceId && NameRules.NamesEqual(f.FileName, name);

    private string RequireRoot() => RootPath ?? throw new InvalidOperationException("No art folder is open.");
}

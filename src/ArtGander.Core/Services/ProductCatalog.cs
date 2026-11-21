using ArtGander.Core.Model;
using ArtGander.Core.Naming;
using ArtGander.Core.Storage;

namespace ArtGander.Core.Services;

/// <summary>
/// The library's products (<c>.artgander/products/*.json</c>), with the many-to-many links to art pieces.
/// </summary>
public sealed class ProductCatalog
{
    private readonly Lock _gate = new();
    private List<Product> _all = [];

    public string? RootPath { get; private set; }

    public IReadOnlyList<Product> All
    {
        get { lock (_gate) return _all; }
    }

    /// <summary>Loads every product of the library at <paramref name="rootPath"/>.</summary>
    public void Load(string rootPath) => SetLoaded(rootPath, Records.Products.LoadAll(rootPath));

    public void SetLoaded(string rootPath, IReadOnlyList<Product> products)
    {
        lock (_gate)
        {
            RootPath = rootPath;
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
        Records.Products.Save(RequireRoot(), product, force);
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
        Records.Products.Delete(RequireRoot(), product);
        lock (_gate) _all = _all.Where(p => p.Id != product.Id).ToList();
    }

    /// <summary>Loads the current version of a product from disk (e.g. after another computer changed it).</summary>
    public Product? Reload(Product product)
    {
        var fresh = Records.Products.Reload(RequireRoot(), product);
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
            var target = Records.Products.ChangedOnDisk(RootPath, product) ? Reload(product) ?? product : product;
            foreach (var f in target.Files.Where(f => Matches(f, pieceId, oldName)).ToList())
            {
                if (newName is null) target.Files.Remove(f);
                else f.FileName = newName;
            }
            Save(target, force: true);
        }
    }

    private static bool Matches(ProductFile f, Guid pieceId, string name) => f.PieceId == pieceId && NameRules.NamesEqual(f.FileName, name);

    private string RequireRoot() => RootPath ?? throw new InvalidOperationException("No art folder is open.");
}

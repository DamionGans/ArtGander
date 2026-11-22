using ArtGander.Core.Model;
using ArtGander.Core.Naming;
using ArtGander.Core.Storage;

namespace ArtGander.Core.Services;

/// <summary>What's on disk in a product's folder compared to its list of pictures.</summary>
public sealed record ProductFolderState(IReadOnlyList<string> Untracked, IReadOnlyList<string> Missing);

/// <summary>A product picture together with its product, e.g. for an art piece's page.</summary>
public sealed record ProductPicture(Product Product, ProductImage Image, string Path);

/// <summary>
/// The pictures in a product's own folder, named <c>{Product_Name}-{label}.{ext}</c> like the files of an art piece.
/// These methods change the files and the product's <see cref="Product.Images"/> list; the caller saves the product.
/// </summary>
public static class ProductImageService
{
    public static IReadOnlySet<string> ImageExtensions { get; } = new HashSet<string>(
        [".jpg", ".jpeg", ".png", ".webp", ".gif", ".tif", ".tiff", ".bmp", ".heic", ".avif", ".jfif"], StringComparer.OrdinalIgnoreCase);

    public static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path));

    public static string FolderOf(Product product) =>
        ProductStore.FolderOf(product) ?? throw new InvalidOperationException("Save the product first: its pictures go in its folder.");

    /// <summary>The name the pictures are named after: the product folder's name without a "(2)" added for uniqueness.</summary>
    public static string ConventionName(Product product) => ProductStore.ConventionName(Path.GetFileName(FolderOf(product)));

    public static string PathOf(Product product, ProductImage image) => Path.Combine(FolderOf(product), image.FileName);

    /// <summary>A label for a picture from its file name, e.g. "IMG 2041.jpg" → "img-2041".</summary>
    public static string SuggestLabel(Product product, string fileName)
    {
        var label = FileNamer.SuggestVersion(ConventionName(product), fileName);
        return label.Length == 0 ? "photo" : label; // a picture named just like the product
    }

    /// <summary>Guesses the kind from the label: "mockup-front" → mockup, "detail" → detail…</summary>
    public static string GuessKind(string label)
    {
        foreach (var kind in ProductImageKind.All)
            if (label.Contains(NameRules.ToVersionLabel(kind), StringComparison.OrdinalIgnoreCase)) return kind;
        if (label.Contains("mock", StringComparison.OrdinalIgnoreCase)) return ProductImageKind.Mockup;
        if (label.Contains("size", StringComparison.OrdinalIgnoreCase)) return ProductImageKind.SizeChart;
        return ProductImageKind.Photo;
    }

    /// <summary>Copies (or moves) an outside picture into the product's folder under the naming convention.</summary>
    public static ProductImage Import(Product product, string sourcePath, string? label = null, string? kind = null, Guid? pieceId = null, bool move = false)
    {
        var folder = FolderOf(product);
        Directory.CreateDirectory(folder);
        var name = ConventionName(product);
        var ext = Path.GetExtension(sourcePath);
        label = string.IsNullOrWhiteSpace(label) ? SuggestLabel(product, Path.GetFileName(sourcePath)) : label;
        var unique = FileNamer.MakeUnique(name, label, ext, n => File.Exists(Path.Combine(folder, n)) || Find(product, n) is not null);
        if (unique.Length == 0) unique = FileNamer.MakeUnique(name, "photo", ext, n => File.Exists(Path.Combine(folder, n)) || Find(product, n) is not null);
        var fileName = FileNamer.Build(name, unique, ext);
        var target = Path.Combine(folder, fileName);
        if (move) File.Move(sourcePath, target); else File.Copy(sourcePath, target);
        var image = new ProductImage
        {
            FileName = fileName,
            Label = unique,
            Kind = kind ?? GuessKind(unique),
            PieceId = pieceId ?? (product.Pieces.Count == 1 ? product.Pieces[0] : null),
            AddedAt = DateTimeOffset.Now,
        };
        product.Images.Add(image);
        return image;
    }

    /// <summary>Starts tracking a picture that is already in the product's folder, optionally renaming it to the convention.</summary>
    public static ProductImage Track(Product product, string fileName, string? label, bool renameToConvention)
    {
        label = string.IsNullOrWhiteSpace(label) ? SuggestLabel(product, fileName) : FileNamer.Label(label);
        var image = new ProductImage
        {
            FileName = fileName,
            Label = label,
            Kind = GuessKind(label),
            PieceId = product.Pieces.Count == 1 ? product.Pieces[0] : null,
            AddedAt = DateTimeOffset.Now,
        };
        product.Images.Add(image);
        if (renameToConvention) ChangeLabel(product, image, label);
        return image;
    }

    /// <summary>Stops tracking a picture. The file stays in the folder; nothing is ever deleted.</summary>
    public static void Untrack(Product product, ProductImage image) => product.Images.Remove(image);

    /// <summary>Changes a picture's label, which renames the file.</summary>
    public static void ChangeLabel(Product product, ProductImage image, string newLabel)
    {
        var folder = FolderOf(product);
        var name = ConventionName(product);
        var ext = Path.GetExtension(image.FileName);
        var wanted = string.IsNullOrWhiteSpace(newLabel) ? "photo" : newLabel;
        var desired = FileNamer.Build(name, wanted, ext);
        if (desired == image.FileName) { image.Label = FileNamer.Label(wanted); return; }
        var label = NameRules.NamesEqual(desired, image.FileName)
            ? FileNamer.Label(wanted)
            : FileNamer.MakeUnique(name, wanted, ext, n =>
                (File.Exists(Path.Combine(folder, n)) && !NameRules.NamesEqual(n, image.FileName)) || (Find(product, n) is { } other && other != image));
        var fileName = FileNamer.Build(name, label, ext);
        var from = Path.Combine(folder, image.FileName);
        if (File.Exists(from)) PieceService.MoveSafe(from, Path.Combine(folder, fileName));
        image.FileName = fileName;
        image.Label = label;
    }

    /// <summary>Moves a picture earlier (-1) or later (+1) in the shop order.</summary>
    public static void Move(Product product, ProductImage image, int delta)
    {
        var i = product.Images.IndexOf(image);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= product.Images.Count) return;
        product.Images.Move(i, j);
    }

    /// <summary>Makes a picture the main one (the first).</summary>
    public static void MakeMain(Product product, ProductImage image)
    {
        var i = product.Images.IndexOf(image);
        if (i > 0) product.Images.Move(i, 0);
    }

    /// <summary>Pictures on disk that aren't tracked yet, and tracked pictures whose file is gone.</summary>
    public static ProductFolderState Inspect(Product product)
    {
        if (ProductStore.FolderOf(product) is not { } folder || !Directory.Exists(folder)) return new([], []);
        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(folder)
                .Select(Path.GetFileName)
                .OfType<string>()
                .Where(n => !n.StartsWith('.') && IsImage(n) && !AtomicFile.IsTempFile(n))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new([], []); }
        var tracked = new HashSet<string>(product.Images.Select(i => i.FileName), NameRules.Comparer);
        var onDisk = new HashSet<string>(files, NameRules.Comparer);
        return new(
            files.Where(f => !tracked.Contains(f)).Order(NameRules.NaturalComparer).ToList(),
            product.Images.Select(i => i.FileName).Where(f => !onDisk.Contains(f)).ToList());
    }

    private static ProductImage? Find(Product product, string fileName) =>
        product.Images.FirstOrDefault(i => NameRules.NamesEqual(i.FileName, fileName));
}

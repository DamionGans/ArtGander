using System.Collections.ObjectModel;
using ArtGander.App.Services;
using ArtGander.Core.Model;
using ArtGander.Core.Naming;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ArtGander.App.ViewModels;

/// <summary>A tracked file in the Files tab.</summary>
public sealed partial class FileRowViewModel(FileEntry entry, string fullPath, bool missing, bool misnamed) : ObservableObject
{
    public FileEntry Entry { get; } = entry;
    public string FullPath { get; } = fullPath;
    public bool IsMissing { get; } = missing;
    public bool IsMisnamed { get; } = misnamed;
    public bool IsOk => !IsMissing && !IsMisnamed;

    /// <summary>The version label being edited. Applying it renames the file.</summary>
    [ObservableProperty] public partial string EditVersion { get; set; } = entry.Version;
    [ObservableProperty] public partial ImageSource? Thumbnail { get; set; }

    public bool VersionChanged => NameRules.ToVersionLabel(EditVersion) != Entry.Version;
    partial void OnEditVersionChanged(string value) => OnPropertyChanged(nameof(VersionChanged));

    public string SizeText
    {
        get
        {
            try
            {
                var len = new FileInfo(FullPath).Length;
                return len switch
                {
                    < 1024 => $"{len} B",
                    < 1024 * 1024 => $"{len / 1024.0:0} KB",
                    < 1024L * 1024 * 1024 => $"{len / 1024.0 / 1024.0:0.0} MB",
                    _ => $"{len / 1024.0 / 1024.0 / 1024.0:0.00} GB",
                };
            }
            catch (IOException) { return ""; }
        }
    }

    public async Task LoadThumbnailAsync(ThumbnailService thumbs)
    {
        if (IsMissing) return;
        var path = await thumbs.GetAsync(FullPath);
        if (path is not null) MainThread.BeginInvokeOnMainThread(() => Thumbnail = ImageSource.FromFile(path));
    }
}

/// <summary>A file on disk that isn't tracked yet.</summary>
public sealed partial class UntrackedFileViewModel(string fileName, string suggestedVersion, string kind) : ObservableObject
{
    public string FileName { get; } = fileName;
    [ObservableProperty] public partial string Version { get; set; } = suggestedVersion;
    [ObservableProperty] public partial string Kind { get; set; } = kind;
}

/// <summary>A checkbox that links a product to a file.</summary>
public sealed partial class ProductFileLink : ObservableObject
{
    private readonly Product _product;

    public ProductFileLink(Product product, FileEntry file)
    {
        _product = product;
        File = file;
        IsLinked = product.Files.Any(f => NameRules.NamesEqual(f, file.FileName));
    }

    public FileEntry File { get; }
    public string Label => $"{File.Version} ({Path.GetExtension(File.FileName).TrimStart('.')})";

    [ObservableProperty] public partial bool IsLinked { get; set; }

    partial void OnIsLinkedChanged(bool value)
    {
        var existing = _product.Files.FirstOrDefault(f => NameRules.NamesEqual(f, File.FileName));
        if (value && existing is null) _product.Files.Add(File.FileName);
        else if (!value && existing is not null) _product.Files.Remove(existing);
    }
}

public sealed partial class ProductViewModel : ObservableObject
{
    public ProductViewModel(Product product, IEnumerable<FileEntry> files)
    {
        Product = product;
        RebuildLinks(files);
        Product.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(Product.Name) or nameof(Product.Type)) OnPropertyChanged(nameof(Header));
        };
    }

    public Product Product { get; }
    public ObservableCollection<ProductFileLink> FileLinks { get; } = [];

    /// <summary>Linked file names that no longer exist in the piece.</summary>
    [ObservableProperty] public partial string MissingLinksText { get; set; } = "";

    public string Header => string.IsNullOrWhiteSpace(Product.Name) ? "New product" : Product.Name;

    public void RebuildLinks(IEnumerable<FileEntry> files)
    {
        var list = files.ToList();
        FileLinks.Clear();
        foreach (var f in list) FileLinks.Add(new ProductFileLink(Product, f));
        var missing = Product.Files.Where(n => !list.Any(f => NameRules.NamesEqual(f.FileName, n))).ToList();
        MissingLinksText = missing.Count == 0 ? "" : "Linked but missing: " + string.Join(", ", missing);
    }
}

public sealed partial class SaleViewModel : ObservableObject
{
    public SaleViewModel(Sale sale, ObservableCollection<Product> products)
    {
        Sale = sale;
        Products = products;
        Sale.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(Sale.Quantity) or nameof(Sale.UnitPrice)) OnPropertyChanged(nameof(Revenue));
        };
    }

    public Sale Sale { get; }
    public ObservableCollection<Product> Products { get; }

    public Product? SelectedProduct
    {
        get => Products.FirstOrDefault(p => p.Id == Sale.ProductId);
        set
        {
            // Pickers push null while they are being torn down; never let that wipe the product link.
            if (value is null || value.Id == Sale.ProductId) return;
            Sale.ProductId = value?.Id;
            if (value is not null && Sale.UnitPrice == 0 && value.Price is { } price) Sale.UnitPrice = price;
            OnPropertyChanged();
        }
    }

    public decimal Revenue => Sale.UnitPrice * Sale.Quantity;
}

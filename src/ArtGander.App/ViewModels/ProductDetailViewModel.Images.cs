using System.Collections.ObjectModel;
using ArtGander.App.Services;
using ArtGander.Core.Integrations;
using ArtGander.Core.Model;
using ArtGander.Core.Naming;
using ArtGander.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ArtGander.App.ViewModels;

/// <summary>An art piece or project a product picture can show, or none.</summary>
public sealed record PieceChoice(Guid? Id, string Title);

/// <summary>One of a product's own pictures, on the product's Pictures tab.</summary>
public sealed partial class ProductImageRow : ProtectableRow
{
    public static IReadOnlyList<string> Kinds => ProductImageKind.All;

    private readonly IReadOnlyList<PieceChoice> _choices;

    public ProductImageRow(ProductImage image, string fullPath, int index, IReadOnlyList<PieceChoice> choices)
    {
        Image = image;
        FullPath = fullPath;
        IsMissing = !File.Exists(fullPath);
        Index = index;
        _choices = choices;
        EditLabel = image.Label;
        SelectedPiece = choices.FirstOrDefault(c => c.Id == image.PieceId) ?? choices[0];
    }

    public ProductImage Image { get; }
    public override string FullPath { get; }
    public override bool IsMissing { get; }
    public int Index { get; }
    public bool IsMain => Index == 0;
    public IReadOnlyList<PieceChoice> PieceChoices => _choices;
    public string SourceText => Image.Source switch
    {
        PictureSource.Printify => "Printify mockup",
        PictureSource.Shopify => "From the Shopify store",
        _ => "",
    };
    public bool HasSourceText => SourceText.Length > 0;

    [ObservableProperty] public partial string EditLabel { get; set; } = "";
    [ObservableProperty] public partial PieceChoice? SelectedPiece { get; set; }
    [ObservableProperty] public partial ImageSource? Thumbnail { get; set; }

    public bool LabelChanged => FileNamer.Label(EditLabel) != Image.Label && !string.IsNullOrWhiteSpace(EditLabel);
    partial void OnEditLabelChanged(string value) => OnPropertyChanged(nameof(LabelChanged));
    partial void OnSelectedPieceChanged(PieceChoice? value)
    {
        if (value is not null && Image.PieceId != value.Id) Image.PieceId = value.Id;
    }

    public async Task LoadThumbnailAsync(ThumbnailService thumbs)
    {
        if (IsMissing) return;
        var path = await thumbs.GetAsync(FullPath);
        if (path is not null) MainThread.BeginInvokeOnMainThread(() => Thumbnail = ImageSource.FromFile(path));
    }
}

/// <summary>A picture in the product's folder that isn't tracked yet.</summary>
public sealed partial class UntrackedImageRow(string fileName, string label) : ObservableObject
{
    public string FileName { get; } = fileName;
    [ObservableProperty] public partial string Label { get; set; } = label;
}

/// <summary>The product's own pictures: in its folder, in shop order, each optionally showing an art piece or project.</summary>
public sealed partial class ProductDetailViewModel
{
    public ObservableCollection<ProductImageRow> Images { get; } = [];
    public ObservableCollection<UntrackedImageRow> UntrackedImages { get; } = [];
    [ObservableProperty] public partial string ImagesNotice { get; set; } = "";

    public bool IsImages => Tab == "images";
    public bool HasImages => Images.Count > 0;
    public bool HasUntrackedImages => UntrackedImages.Count > 0;
    public bool IsPrintifyProduct => Product.Type == ProductType.PrintOnDemand;

    public static IReadOnlyList<PictureModeChoice> PictureModes { get; } =
    [
        new(PictureMode.PrintifyMockups, "Printify's mockups"),
        new(PictureMode.Own, "Our own pictures"),
    ];

    public PictureModeChoice SelectedPictureMode
    {
        get => PictureModes.First(m => m.Mode == Product.Pictures);
        set
        {
            if (value is null || Product.Pictures == value.Mode) return;
            Product.Pictures = value.Mode;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PictureModeText));
        }
    }

    public string PictureModeText => Product.EffectivePictures == PictureMode.Own
        ? IsPrintifyProduct
            ? "The shop shows these pictures, in this order (the first is the main picture). Printify no longer replaces them when it publishes."
            : "The shop shows these pictures, in this order (the first is the main picture)."
        : "The shop shows the mockups Printify makes. Pictures here are kept for yourself until you switch to your own pictures.";

    /// <summary>"None", then every art piece and project.</summary>
    private List<PieceChoice> BuildPieceChoices()
    {
        var choices = new List<PieceChoice> { new(null, "None (not one specific art piece)") };
        choices.AddRange(app.Documents
            .OrderBy(d => d.DisplayTitle, NameRules.NaturalComparer)
            .Select(d => new PieceChoice(d.Piece.Id, d.Piece.Type == ItemType.Project ? $"{d.DisplayTitle} (project)" : d.DisplayTitle)));
        foreach (var image in Product.Images)
            if (image.PieceId is { } id && choices.All(c => c.Id != id))
                choices.Add(new PieceChoice(id, "(art piece not found)"));
        return choices;
    }

    private void RebuildImages()
    {
        var previous = Images.ToList();
        Images.Clear();
        UntrackedImages.Clear();
        if (ProductCatalog.FolderOf(Product) is { } folder)
        {
            var choices = BuildPieceChoices();
            for (var i = 0; i < Product.Images.Count; i++)
            {
                var image = Product.Images[i];
                var row = new ProductImageRow(image, Path.Combine(folder, image.FileName), i, choices);
                row.CarryOverFrom(previous.FirstOrDefault(p => NameRules.NamesEqual(p.Image.FileName, image.FileName)));
                Images.Add(row);
                _ = row.LoadThumbnailAsync(thumbs);
            }
            var state = ProductImageService.Inspect(Product);
            foreach (var name in state.Untracked)
                UntrackedImages.Add(new UntrackedImageRow(name, ProductImageService.SuggestLabel(Product, name)));
        }
        OnPropertyChanged(nameof(HasImages));
        OnPropertyChanged(nameof(HasUntrackedImages));
        OnPropertyChanged(nameof(SelectedPictureMode));
        OnPropertyChanged(nameof(PictureModeText));
        OnPropertyChanged(nameof(IsPrintifyProduct));
    }

    private ProductImageRow? ImageRowFor(string fileName) =>
        Images.FirstOrDefault(r => NameRules.NamesEqual(r.Image.FileName, fileName));

    /// <summary>
    /// Runs a picture operation. Pictures live in the product's folder, so a new product is saved first, and the
    /// product (with any pending edits) is saved right after, like the files of an art piece.
    /// </summary>
    private async Task ImageOpAsync(Action op)
    {
        if (IsReadOnly) return;
        if (IsNew || ProductCatalog.FolderOf(Product) is null)
        {
            if (!await Dialogs.ConfirmAsync("Save the product first?", "Its pictures go in its own folder, which is made when the product is saved.", "Save")) return;
            if (!await TrySaveAsync()) return;
        }
        try
        {
            using (app.SuspendWatching()) op();
        }
        catch (Exception ex)
        {
            await Dialogs.ErrorAsync(ex);
            RebuildImages();
            return;
        }
        await TrySaveAsync();
        RebuildImages();
    }

    [RelayCommand]
    private void AddImages() => Ui.Fire(async () =>
    {
        IReadOnlyList<string> picked;
        var start = ProductCatalog.FolderOf(Product) ?? app.RootPath ?? "";
        try { picked = await platform.PickFilesAsync(start); }
        catch (Exception ex) { await Dialogs.ErrorAsync(ex); return; }
        await AddImagePathsAsync(picked);
    });

    /// <summary>Adds pictures from anywhere: copied into the product's folder (or tracked in place when already there).</summary>
    public async Task AddImagePathsAsync(IReadOnlyList<string> paths)
    {
        var images = paths.Where(File.Exists).ToList();
        if (images.Count == 0) return;
        var notImages = images.Where(p => !ProductImageService.IsImage(p)).ToList();
        images = images.Except(notImages).ToList();
        Tab = "images";
        await ImageOpAsync(() =>
        {
            var folder = ProductImageService.FolderOf(Product);
            foreach (var path in images)
            {
                if (NameRules.NamesEqual(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFullPath(folder)))
                {
                    var name = Path.GetFileName(path);
                    if (Product.Images.Any(i => NameRules.NamesEqual(i.FileName, name))) continue;
                    ProductImageService.Track(Product, name, null, renameToConvention: true);
                }
                else
                {
                    ProductImageService.Import(Product, path);
                }
            }
        });
        ImagesNotice = notImages.Count > 0 ? $"Skipped {notImages.Count} file(s) that aren't pictures: {string.Join(", ", notImages.Select(Path.GetFileName))}" : "";
    }

    [RelayCommand]
    private void RenameImage(ProductImageRow? row) => Ui.Fire(() =>
        row is null || !row.LabelChanged ? Task.CompletedTask : ImageOpAsync(() => ProductImageService.ChangeLabel(Product, row.Image, row.EditLabel)));

    [RelayCommand]
    private void MoveImageEarlier(ProductImageRow? row)
    {
        if (row is null) return;
        ProductImageService.Move(Product, row.Image, -1);
        RebuildImages();
    }

    [RelayCommand]
    private void MoveImageLater(ProductImageRow? row)
    {
        if (row is null) return;
        ProductImageService.Move(Product, row.Image, +1);
        RebuildImages();
    }

    [RelayCommand]
    private void MakeMainImage(ProductImageRow? row)
    {
        if (row is null) return;
        ProductImageService.MakeMain(Product, row.Image);
        RebuildImages();
    }

    [RelayCommand]
    private void OpenImage(ProductImageRow? row) => Ui.Fire(async () =>
    {
        if (row is null || row.IsMissing) return;
        try { await Launcher.Default.OpenAsync(new OpenFileRequest(row.Image.FileName, new ReadOnlyFile(row.FullPath))); }
        catch (Exception ex) { await Dialogs.ErrorAsync(ex); }
    });

    [RelayCommand]
    private void ShowImage(ProductImageRow? row)
    {
        if (row is null) return;
        if (!row.IsMissing) platform.RevealInFileManager(row.FullPath);
        else if (ProductCatalog.FolderOf(Product) is { } folder) platform.RevealInFileManager(folder);
    }

    [RelayCommand]
    private void RemoveImage(ProductImageRow? row) => Ui.Fire(async () =>
    {
        if (row is null) return;
        var msg = row.IsMissing
            ? $"Remove the missing picture \"{row.Image.FileName}\" from this product's list?"
            : $"Stop using \"{row.Image.FileName}\" for this product? The file stays in the product's folder.";
        if (!await Dialogs.ConfirmAsync("Remove picture", msg, "Remove")) return;
        await ImageOpAsync(() => ProductImageService.Untrack(Product, row.Image));
    });

    [RelayCommand]
    private void TrackImage(UntrackedImageRow? row) => Ui.Fire(() =>
        row is null ? Task.CompletedTask : ImageOpAsync(() => ProductImageService.Track(Product, row.FileName, row.Label, renameToConvention: true)));

    [RelayCommand]
    private void TrackImageAsIs(UntrackedImageRow? row) => Ui.Fire(() =>
        row is null ? Task.CompletedTask : ImageOpAsync(() => ProductImageService.Track(Product, row.FileName, row.Label, renameToConvention: false)));

    // ---- Protection with Mistcoat ----

    [RelayCommand]
    private void QuickProtectImage(ProductImageRow? row) => Ui.Fire(() =>
        row is null ? Task.CompletedTask : ProtectImageAsync(row, protection.QuickOptions(PieceOf(row)), Path.GetExtension(row.FullPath), ProtectedLabel(row)));

    [RelayCommand]
    private void ShowImageProtectOptions(ProductImageRow? row)
    {
        if (row is null) return;
        row.Advanced = row.Advanced is null
            ? new ProtectOptionsViewModel(Path.GetExtension(row.FullPath), protection.QuickOptions(PieceOf(row)), ProtectedLabel(row))
            : null;
    }

    [RelayCommand]
    private void ProtectImageWithOptions(ProductImageRow? row) => Ui.Fire(() =>
    {
        if (row?.Advanced is not { } options) return Task.CompletedTask;
        var label = string.IsNullOrWhiteSpace(options.Version) ? ProtectedLabel(row) : options.Version;
        return ProtectImageAsync(row, options.ToOptions(), options.OutputExtension, label);
    });

    [RelayCommand]
    private void CancelImageProtect(ProductImageRow? row) => row?.ProtectCancel?.Cancel();

    [RelayCommand]
    private void OpenImageInMistcoat(ProductImageRow? row) => Ui.Fire(() =>
        row is null ? Task.CompletedTask : PieceDetailViewModel.OpenInMistcoatAsync(protection, row));

    private ArtPiece? PieceOf(ProductImageRow row) =>
        row.Image.PieceId is { } id ? app.Documents.FirstOrDefault(d => d.Piece.Id == id)?.Piece : null;

    private static string ProtectedLabel(ProductImageRow row) => ProtectionService.ProtectedVersion(row.Image.Label);

    private Task ProtectImageAsync(ProductImageRow row, MistcoatOptions options, string extension, string label)
    {
        var fileName = row.Image.FileName;
        var source = row.Image;
        return protection.RunAsync(row, () => ImageRowFor(fileName), options, extension, async (output, result) =>
        {
            ProductImage? added = null;
            await ImageOpAsync(() =>
            {
                added = ProductImageService.Import(Product, output, label, source.Kind, source.PieceId, move: true);
                added.Alt = source.Alt;
                // Right after the original in the shop order.
                var at = Product.Images.IndexOf(source);
                if (at >= 0) Product.Images.Move(Product.Images.IndexOf(added), at + 1);
            });
            if (added is null) return "Mistcoat finished, but the protected copy couldn't be added to this product.";
            if (ImageRowFor(fileName) is { } r) r.Advanced = null;
            return ProtectionService.SavedMessage(added.FileName, result);
        });
    }
}

public sealed record PictureModeChoice(PictureMode Mode, string Title);

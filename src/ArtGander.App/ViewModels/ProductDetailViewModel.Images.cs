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
            OnPropertyChanged(nameof(GetShopPicturesText));
        }
    }

    public string PictureModeText => Product.EffectivePictures == PictureMode.Own
        ? IsPrintifyProduct
            ? "The shop shows these pictures (not the Printify mockups), in this order; the first is the main one. Sync uploads them to Shopify (as the protected copy, when a picture has one), and Printify no longer replaces them when it publishes."
            : "The shop shows these pictures, in this order; the first is the main one. Sync uploads them to Shopify (as the protected copy, when a picture has one)."
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
                row.RefreshProtectedCopy();
                Images.Add(row);
                _ = row.LoadThumbnailAsync(thumbs);
            }
            var state = ProductImageService.Inspect(Product);
            foreach (var name in state.Untracked)
                UntrackedImages.Add(new UntrackedImageRow(name, ProductImageService.SuggestLabel(Product, name)));
        }
        RebuildPhotographs();
        OnPropertyChanged(nameof(HasImages));
        OnPropertyChanged(nameof(HasUntrackedImages));
        OnPropertyChanged(nameof(SelectedPictureMode));
        OnPropertyChanged(nameof(PictureModeText));
        OnPropertyChanged(nameof(IsPrintifyProduct));
        OnPropertyChanged(nameof(CanGetShopPictures));
        OnPropertyChanged(nameof(GetShopPicturesText));
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

    public bool CanGetShopPictures => !IsNew && (Product.IsOnPrintify || Product.IsOnShopify);
    public string GetShopPicturesText => Product.IsOnPrintify && Product.EffectivePictures == PictureMode.PrintifyMockups
        ? "Get Printify's mockups" : "Get pictures from the shop";

    /// <summary>Downloads Printify's current mockups (or the Shopify pictures not here yet) into the product's folder.</summary>
    [RelayCommand]
    private void GetShopPictures() => Ui.Fire(async () =>
    {
        if (HasUnsavedChanges && !await TrySaveAsync()) return;
        ImagesNotice = "Getting the pictures…";
        try
        {
            int added;
            using (app.SuspendWatching()) added = await import.DownloadPicturesAsync(app.Settings, Product, CancellationToken.None);
            ImagesNotice = added == 0 ? "Nothing new: the pictures are up to date." : $"{added} picture(s) added.";
            Load(app.Catalog.Find(Product.Id) ?? Product);
        }
        catch (Exception ex)
        {
            ImagesNotice = "";
            await Dialogs.ErrorAsync(ex);
        }
    });

    // ---- The original photographs (Photograph folder) ----

    public ObservableCollection<SpecialFileRow> Photographs { get; } = [];
    public bool HasPhotographs => Photographs.Count > 0;

    private void RebuildPhotographs()
    {
        var previous = Photographs.ToList();
        Photographs.Clear();
        if (ProductCatalog.FolderOf(Product) is { } folder)
            foreach (var photo in ProtectedCopies.Photographs(folder))
            {
                var row = new SpecialFileRow(photo);
                row.CarryOverFrom(previous.FirstOrDefault(p => string.Equals(p.FullPath, photo, StringComparison.OrdinalIgnoreCase)));
                row.RefreshProtectedCopy();
                Photographs.Add(row);
                _ = row.LoadThumbnailAsync(thumbs);
            }
        OnPropertyChanged(nameof(HasPhotographs));
    }

    [RelayCommand]
    private void OpenPhotograph(SpecialFileRow? row) => Ui.Fire(() => PieceDetailViewModel.OpenFilePathAsync(row?.FullPath));

    [RelayCommand]
    private void ShowPhotograph(SpecialFileRow? row)
    {
        if (row is not null && File.Exists(row.FullPath)) platform.RevealInFileManager(row.FullPath);
    }

    // ---- Protection with Mistcoat: the copy goes in the product's Protected folder ----

    /// <summary>The row currently shown for the same file (a picture or a photograph); rows are rebuilt after picture operations.</summary>
    private ProtectableRow? RowNow(ProtectableRow row) =>
        (ProtectableRow?)Images.FirstOrDefault(r => string.Equals(r.FullPath, row.FullPath, StringComparison.OrdinalIgnoreCase))
        ?? Photographs.FirstOrDefault(r => string.Equals(r.FullPath, row.FullPath, StringComparison.OrdinalIgnoreCase));

    private ArtPiece? PieceOf(ProtectableRow row) =>
        row is ProductImageRow { Image.PieceId: { } id } ? app.Documents.FirstOrDefault(d => d.Piece.Id == id)?.Piece
        : Product.Pieces.Count == 1 ? app.Documents.FirstOrDefault(d => d.Piece.Id == Product.Pieces[0])?.Piece : null;

    [RelayCommand]
    private void QuickProtectImage(ProtectableRow? row) => Ui.Fire(() =>
        row is null ? Task.CompletedTask
            : PieceDetailViewModel.ProtectRowAsync(protection, row, () => RowNow(row), protection.QuickOptions(PieceOf(row)), Path.GetExtension(row.FullPath)));

    [RelayCommand]
    private void ShowImageProtectOptions(ProtectableRow? row)
    {
        if (row is null) return;
        row.Advanced = row.Advanced is null ? new ProtectOptionsViewModel(Path.GetExtension(row.FullPath), protection.QuickOptions(PieceOf(row)), "") : null;
    }

    [RelayCommand]
    private void ProtectImageWithOptions(ProtectableRow? row) => Ui.Fire(() =>
        row?.Advanced is not { } options ? Task.CompletedTask
            : PieceDetailViewModel.ProtectRowAsync(protection, row, () => RowNow(row), options.ToOptions(), options.OutputExtension));

    [RelayCommand]
    private void CancelImageProtect(ProtectableRow? row) => row?.ProtectCancel?.Cancel();

    [RelayCommand]
    private void OpenImageInMistcoat(ProtectableRow? row) => Ui.Fire(() =>
        row is null ? Task.CompletedTask : PieceDetailViewModel.OpenInMistcoatAsync(protection, row));

    [RelayCommand]
    private void OpenProtectedCopy(ProtectableRow? row) => Ui.Fire(() => PieceDetailViewModel.OpenProtectedCopyAsync(row));

    [RelayCommand]
    private void ShowProtectedCopy(ProtectableRow? row) => PieceDetailViewModel.ShowProtectedCopy(platform, row);
}

public sealed record PictureModeChoice(PictureMode Mode, string Title);

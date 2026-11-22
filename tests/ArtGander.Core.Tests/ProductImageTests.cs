using ArtGander.Core.Model;
using ArtGander.Core.Services;
using ArtGander.Core.Storage;

namespace ArtGander.Core.Tests;

public class ProductImageTests
{
    private static (TempLibrary Lib, ProductCatalog Catalog, Product Mug, Guid Goose) Setup()
    {
        var lib = new TempLibrary();
        var goose = Guid.NewGuid();
        var catalog = new ProductCatalog();
        catalog.SetLoaded(lib.Root, []);
        catalog.GroupNameFor = id => id == goose ? "Goose" : null;
        var mug = new Product { Name = "Goose Mug", Pieces = [goose] };
        catalog.Save(mug);
        return (lib, catalog, mug, goose);
    }

    [Fact]
    public void Pictures_are_copied_in_under_the_naming_convention()
    {
        var (lib, catalog, mug, goose) = Setup();
        using var _ = lib;
        var outside = lib.WriteFile("Downloads/IMG 2041.JPG", "jpg");
        var mockup = lib.WriteFile("Downloads/mockup front.png", "png");

        var photo = ProductImageService.Import(mug, outside);
        var mock = ProductImageService.Import(mug, mockup);
        var again = ProductImageService.Import(mug, outside);
        catalog.Save(mug);

        var folder = Path.Combine(lib.Root, "Products", "Goose", "Goose Mug");
        Assert.Equal("Goose_Mug-img-2041.jpg", photo.FileName);
        Assert.Equal("img-2041", photo.Label);
        Assert.Equal(goose, photo.PieceId); // a product of one piece: its pictures show that piece
        Assert.Equal(ProductImageKind.Mockup, mock.Kind);
        Assert.Equal("Goose_Mug-img-2041-2.jpg", again.FileName);
        Assert.True(File.Exists(Path.Combine(folder, "Goose_Mug-img-2041.jpg")));
        Assert.True(File.Exists(outside)); // copied, not moved
        Assert.Equal(3, Assert.Single(new ProductStore(lib.Root, null).LoadAll()).Images.Count);
    }

    [Fact]
    public void Relabel_reorder_and_main_picture()
    {
        var (lib, catalog, mug, _) = Setup();
        using var __ = lib;
        var a = ProductImageService.Import(mug, lib.WriteFile("a.jpg"));
        var b = ProductImageService.Import(mug, lib.WriteFile("b.jpg"));
        var c = ProductImageService.Import(mug, lib.WriteFile("c.jpg"));

        ProductImageService.ChangeLabel(mug, b, "Living Room");
        Assert.Equal("Goose_Mug-living-room.jpg", b.FileName);
        Assert.True(File.Exists(ProductImageService.PathOf(mug, b)));
        Assert.False(File.Exists(Path.Combine(ProductImageService.FolderOf(mug), "Goose_Mug-b.jpg")));

        ProductImageService.MakeMain(mug, c);
        Assert.Equal([c, a, b], mug.Images);
        ProductImageService.Move(mug, b, -1);
        Assert.Equal([c, b, a], mug.Images);
        ProductImageService.Move(mug, c, -1); // already first: nothing happens
        Assert.Equal([c, b, a], mug.Images);
    }

    [Fact]
    public void New_and_missing_pictures_in_the_folder_are_noticed()
    {
        var (lib, catalog, mug, _) = Setup();
        using var __ = lib;
        var kept = ProductImageService.Import(mug, lib.WriteFile("kept.jpg"));
        var gone = ProductImageService.Import(mug, lib.WriteFile("gone.jpg"));
        catalog.Save(mug);
        var folder = ProductImageService.FolderOf(mug);
        File.Delete(Path.Combine(folder, gone.FileName));
        File.WriteAllText(Path.Combine(folder, "phone photo.heic"), "x");
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "not a picture");

        var state = ProductImageService.Inspect(mug);
        Assert.Equal(["phone photo.heic"], state.Untracked);
        Assert.Equal([gone.FileName], state.Missing);

        var tracked = ProductImageService.Track(mug, "phone photo.heic", null, renameToConvention: true);
        Assert.Equal("Goose_Mug-phone-photo.heic", tracked.FileName);
        Assert.Empty(ProductImageService.Inspect(mug).Untracked);
        Assert.NotNull(kept);
    }

    [Fact]
    public void Pictures_are_found_for_a_piece_and_for_a_project_with_its_pieces()
    {
        var (lib, catalog, mug, goose) = Setup();
        using var _ = lib;
        var book = Guid.NewGuid();
        var otherPiece = Guid.NewGuid();
        var onGoose = ProductImageService.Import(mug, lib.WriteFile("a.jpg"));
        var onNothing = ProductImageService.Import(mug, lib.WriteFile("b.jpg"));
        onNothing.PieceId = null;
        catalog.Save(mug);
        var hardcover = new Product { Name = "Hardcover" };
        catalog.Save(hardcover);
        var cover = ProductImageService.Import(hardcover, lib.WriteFile("cover.jpg"), pieceId: book);
        catalog.Save(hardcover);

        Assert.Equal([onGoose], catalog.ImagesFor([goose]).Select(p => p.Image));
        Assert.Equal([cover], catalog.ImagesFor([book]).Select(p => p.Image));
        // A project page asks for the project and its pieces.
        Assert.Equal(2, catalog.ImagesFor([book, goose]).Count);
        Assert.Empty(catalog.ImagesFor([otherPiece]));
        Assert.True(File.Exists(catalog.ImagesFor([book])[0].Path));
    }

    [Fact]
    public void A_numbered_product_folder_keeps_clean_picture_names()
    {
        var (lib, catalog, mug, goose) = Setup();
        using var _ = lib;
        var second = new Product { Name = "Goose Mug", Pieces = [goose] };
        catalog.Save(second);
        Assert.EndsWith("Goose Mug (2)", ProductImageService.FolderOf(second));
        var image = ProductImageService.Import(second, lib.WriteFile("x.jpg"));
        Assert.Equal("Goose_Mug-x.jpg", image.FileName);
    }
}

using ArtGander.Core.Integrations;
using ArtGander.Core.Library;
using ArtGander.Core.Model;
using ArtGander.Core.Services;
using ArtGander.Core.Storage;

namespace ArtGander.Core.Tests;

/// <summary>The Protected and Photograph folders inside an art piece's (or product's) folder.</summary>
public class SpecialFolderTests
{
    [Fact]
    public void Protected_copies_are_named_after_the_original_in_the_Protected_folder()
    {
        using var lib = new TempLibrary();
        var original = lib.WriteFile("Goose/Goose-master.png");
        Assert.Equal(Path.Combine(lib.Root, "Goose", "Protected", "Goose-master-protected.png"), ProtectedCopies.PathFor(original));
        Assert.Equal(Path.Combine(lib.Root, "Goose", "Protected", "Goose-master-protected.jpg"), ProtectedCopies.PathFor(original, ".JPG"));
        Assert.Null(ProtectedCopies.Find(original));

        // Saved from Mistcoat's own window: "_protected".
        var fromMistcoat = lib.WriteFile("Goose/Protected/Goose-master_protected.png");
        Assert.Equal(Path.GetFullPath(fromMistcoat), ProtectedCopies.Find(original));
        File.SetLastWriteTimeUtc(fromMistcoat, DateTime.UtcNow.AddMinutes(-5));
        var ours = lib.WriteFile("Goose/Protected/Goose-master-protected.webp");
        Assert.Equal(Path.GetFullPath(ours), ProtectedCopies.Find(original)); // the newest
        Assert.Null(ProtectedCopies.Find(lib.WriteFile("Goose/Goose-photo.jpg")));

        // A photograph's copy goes in the piece's Protected folder, not in Photograph/Protected.
        var photo = lib.WriteFile("Goose/Photograph/Goose-photograph.jpg");
        Assert.Equal(Path.Combine(lib.Root, "Goose", "Protected", "Goose-photograph-protected.jpg"), ProtectedCopies.PathFor(photo));
    }

    [Fact]
    public void Protected_and_Photograph_folders_are_never_art_pieces()
    {
        using var lib = new TempLibrary();
        var store = new ArtPieceStore();
        var service = new PieceService(store);
        var book = service.CreateProject(lib.Root, "Goose Book");
        service.CreatePieceInProject(book, "Page One");
        lib.WriteFile("Goose Book/Protected/Goose_Book-cover-protected.png");
        lib.WriteFile("Goose Book/Photograph/Goose_Book-photograph.jpg");

        var scanned = Assert.Single(new LibraryScanner(store).Scan(lib.Root, TestContext.Current.CancellationToken));
        Assert.Equal(["Page One"], scanned.Children.Select(c => c.Title));
        // A plain folder holding only those is not a project either.
        lib.WriteFile("Swan/Swan.png");
        lib.WriteFile("Swan/Photograph/Swan-photograph.jpg");
        Assert.Empty(FolderClassifier.ArtSubfolders(Path.Combine(lib.Root, "Swan")));
    }

    [Fact]
    public void Renames_take_the_protected_copies_and_photographs_along()
    {
        using var lib = new TempLibrary();
        var store = new ArtPieceStore();
        var service = new PieceService(store);
        var doc = service.CreatePiece(lib.Root, "Goose");
        var master = service.ImportFile(doc, lib.WriteFile("in/master.png"), "master", "master");
        lib.WriteFile("Goose/Protected/Goose-master-protected.png");
        lib.WriteFile("Goose/Photograph/Goose-photograph.jpg");
        lib.WriteFile("Goose/Protected/Goose-photograph-protected.jpg");

        service.ChangeFileVersion(doc, master, "final");
        Assert.True(File.Exists(Path.Combine(lib.Root, "Goose", "Protected", "Goose-final-protected.png")));

        service.RenamePiece(doc, "Goose On The Loose");
        var folder = Path.Combine(lib.Root, "Goose On The Loose");
        Assert.True(File.Exists(Path.Combine(folder, "Protected", "Goose_On_The_Loose-final-protected.png")));
        Assert.True(File.Exists(Path.Combine(folder, "Photograph", "Goose_On_The_Loose-photograph.jpg")));
        Assert.True(File.Exists(Path.Combine(folder, "Protected", "Goose_On_The_Loose-photograph-protected.jpg")));
        Assert.Equal([Path.Combine(folder, "Photograph", "Goose_On_The_Loose-photograph.jpg")], ProtectedCopies.Photographs(folder));
    }

    [Fact]
    public void A_tracked_file_moved_into_a_special_folder_is_recognized()
    {
        using var lib = new TempLibrary();
        lib.WriteFile("Goose/Photograph/Goose-photo.jpg");
        lib.WriteFile("Goose/Protected/x.png");
        var folder = Path.Combine(lib.Root, "Goose");
        Assert.Equal("Photograph", ProtectedCopies.MovedInto(folder, "Goose-photo.jpg"));
        Assert.Equal("Protected", ProtectedCopies.MovedInto(folder, "Goose-master-protected.png"));
        Assert.Null(ProtectedCopies.MovedInto(folder, "Goose-gone.png"));
    }

    [Fact]
    public void Product_pictures_carry_their_protected_copy_and_the_shop_gets_it()
    {
        using var lib = new TempLibrary();
        var catalog = new ProductCatalog();
        catalog.SetLoaded(lib.Root, []);
        var mug = new Product { Name = "Mug" };
        catalog.Save(mug);
        var image = ProductImageService.Import(mug, lib.WriteFile("in/front.jpg", "original"));
        var copy = ProtectedCopies.PathFor(ProductImageService.PathOf(mug, image));
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        File.WriteAllText(copy, "protected");

        Assert.Equal(copy, ShopPictures.UploadPath(mug, image));

        ProductImageService.ChangeLabel(mug, image, "front-view");
        Assert.Equal(ProtectedCopies.PathFor(ProductImageService.PathOf(mug, image)), ShopPictures.UploadPath(mug, image));

        mug.Name = "Goose Mug";
        catalog.Save(mug);
        Assert.EndsWith(Path.Combine("Goose Mug", "Protected", "Goose_Mug-front-view-protected.jpg"), ShopPictures.UploadPath(mug, image));
    }
}

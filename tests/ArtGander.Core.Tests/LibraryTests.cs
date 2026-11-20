using ArtGander.Core.Library;
using ArtGander.Core.Model;
using ArtGander.Core.Services;
using ArtGander.Core.Storage;

namespace ArtGander.Core.Tests;

public class LibraryTests
{
    private readonly ArtPieceStore _store = new();
    private PieceService Service => new(_store);

    [Fact]
    public void Creates_piece_folder_with_details_file()
    {
        using var lib = new TempLibrary();
        var doc = Service.CreatePiece(lib.Root, "Goose: On The Loose", new LibrarySettings { DefaultCopyrightOwner = "Goose Studio" });

        Assert.Equal(Path.Combine(lib.Root, "Goose On The Loose"), doc.FolderPath);
        Assert.True(File.Exists(Path.Combine(doc.FolderPath, "artgander.json")));
        Assert.Equal("Goose Studio", doc.Piece.Copyright.Owner);
        Assert.Throws<IOException>(() => Service.CreatePiece(lib.Root, "Goose On The Loose"));
    }

    [Fact]
    public void Imports_files_under_the_naming_convention()
    {
        using var lib = new TempLibrary();
        var doc = Service.CreatePiece(lib.Root, "Goose Pond");
        var src = lib.WriteFile("incoming/IMG_0001.PNG");

        var a = Service.ImportFile(doc, src, "Transparent", FileKind.Transparent);
        var b = Service.ImportFile(doc, src, "transparent", FileKind.Transparent);

        Assert.Equal("Goose_Pond-transparent.png", a.FileName);
        Assert.Equal("Goose_Pond-transparent-2.png", b.FileName);
        Assert.True(File.Exists(Path.Combine(doc.FolderPath, a.FileName)));
        Assert.True(File.Exists(src)); // copy, not move
        Assert.Equal(2, _store.Load(doc.FolderPath).Piece.Files.Count);
    }

    [Fact]
    public void Scanner_reconciles_disk_with_details_file()
    {
        using var lib = new TempLibrary();
        var doc = Service.CreatePiece(lib.Root, "Goose");
        doc.Piece.Files.Add(new FileEntry { FileName = "Goose-gone.png", Version = "gone" });
        doc.Piece.Files.Add(new FileEntry { FileName = "weird name.jpg", Version = "weird" });
        _store.Save(doc);
        lib.WriteFile("Goose/weird name.jpg");
        lib.WriteFile("Goose/Goose-new.png");
        lib.WriteFile("Goose/Thumbs.db");
        lib.Folder("Unadopted Folder");
        lib.Folder(".artgander");

        var pieces = new LibraryScanner(_store).Scan(lib.Root, TestContext.Current.CancellationToken);

        Assert.Equal(2, pieces.Count);
        var goose = pieces.Single(p => p.IsAdopted);
        Assert.Equal(["Goose-new.png"], goose.UntrackedFiles);
        Assert.Equal(["Goose-gone.png"], goose.MissingFiles);
        Assert.Equal(["weird name.jpg"], goose.MisnamedFiles);
        Assert.False(pieces.Single(p => !p.IsAdopted).IsAdopted);
    }

    [Fact]
    public void Adopts_existing_folder_without_renaming()
    {
        using var lib = new TempLibrary();
        lib.WriteFile("Old Piece/Old_Piece-transparent.png");
        lib.WriteFile("Old Piece/scan from phone.jpg");

        var doc = Service.AdoptFolder(Path.Combine(lib.Root, "Old Piece"));

        Assert.Equal("Old Piece", doc.Piece.Title);
        Assert.Contains(doc.Piece.Files, f => f is { FileName: "Old_Piece-transparent.png", Version: "transparent", Kind: FileKind.Transparent });
        Assert.Contains(doc.Piece.Files, f => f is { FileName: "scan from phone.jpg", Version: "scan-from-phone" });
        Assert.True(File.Exists(Path.Combine(lib.Root, "Old Piece", "scan from phone.jpg")));
    }

    [Fact]
    public void Apply_convention_renames_file_and_product_links()
    {
        using var lib = new TempLibrary();
        lib.WriteFile("Goose/scan from phone.jpg");
        var doc = Service.AdoptFolder(Path.Combine(lib.Root, "Goose"));
        var entry = doc.Piece.Files.Single();
        doc.Piece.Products.Add(new Product { Name = "Print", Files = { entry.FileName } });
        _store.Save(doc);

        Service.ApplyConvention(doc, entry);

        Assert.Equal("Goose-scan-from-phone.jpg", entry.FileName);
        Assert.Equal(["Goose-scan-from-phone.jpg"], doc.Piece.Products[0].Files);
        Assert.True(File.Exists(Path.Combine(doc.FolderPath, "Goose-scan-from-phone.jpg")));
    }

    [Fact]
    public void Renaming_a_piece_renames_folder_files_and_links()
    {
        using var lib = new TempLibrary();
        var doc = Service.CreatePiece(lib.Root, "Goose");
        var src = lib.WriteFile("in/a.png");
        var file = Service.ImportFile(doc, src, "transparent", FileKind.Transparent);
        doc.Piece.Products.Add(new Product { Name = "Sticker", Files = { file.FileName } });
        _store.Save(doc);

        Service.RenamePiece(doc, "Goose On The Loose");

        Assert.Equal(Path.Combine(lib.Root, "Goose On The Loose"), doc.FolderPath);
        Assert.False(Directory.Exists(Path.Combine(lib.Root, "Goose")));
        Assert.True(File.Exists(Path.Combine(doc.FolderPath, "Goose_On_The_Loose-transparent.png")));
        var reloaded = _store.Load(doc.FolderPath);
        Assert.Equal("Goose On The Loose", reloaded.Piece.Title);
        Assert.Equal(["Goose_On_The_Loose-transparent.png"], reloaded.Piece.Products[0].Files);
    }

    [Fact]
    public void Case_only_rename_works()
    {
        using var lib = new TempLibrary();
        var doc = Service.CreatePiece(lib.Root, "goose");
        Service.ImportFile(doc, lib.WriteFile("in/a.png"), "master", FileKind.Master);

        Service.RenamePiece(doc, "Goose");

        Assert.Equal("Goose", Path.GetFileName(Directory.GetDirectories(lib.Root).Single(d => !d.EndsWith("in"))));
        Assert.Equal("Goose-master.png", Path.GetFileName(Directory.GetFiles(doc.FolderPath, "*.png").Single()));
    }

    [Fact]
    public void Rename_rolls_back_when_target_exists()
    {
        using var lib = new TempLibrary();
        var doc = Service.CreatePiece(lib.Root, "Goose");
        Service.CreatePiece(lib.Root, "Swan");
        Assert.Throws<IOException>(() => Service.RenamePiece(doc, "Swan"));
        Assert.Equal("Goose", doc.Piece.Title);
        Assert.True(File.Exists(Path.Combine(lib.Root, "Goose", "artgander.json")));
    }

    [Fact]
    public void Untracking_keeps_the_file_and_removes_links()
    {
        using var lib = new TempLibrary();
        var doc = Service.CreatePiece(lib.Root, "Goose");
        var entry = Service.ImportFile(doc, lib.WriteFile("in/a.png"), "master", FileKind.Master);
        doc.Piece.Products.Add(new Product { Files = { entry.FileName } });

        Service.UntrackFile(doc, entry);

        Assert.Empty(doc.Piece.Files);
        Assert.Empty(doc.Piece.Products[0].Files);
        Assert.True(File.Exists(Path.Combine(doc.FolderPath, "Goose-master.png")));
    }

    [Fact]
    public void Sales_report_groups_orders_across_pieces()
    {
        using var lib = new TempLibrary();
        var a = Service.CreatePiece(lib.Root, "A");
        var b = Service.CreatePiece(lib.Root, "B");
        var pa = new Product { Name = "Print", ProductionCost = 3 };
        a.Piece.Products.Add(pa);
        a.Piece.Sales.Add(new Sale { OrderRef = "#1001", ProductId = pa.Id, Quantity = 2, UnitPrice = 20, Fees = 1 });
        b.Piece.Sales.Add(new Sale { OrderRef = "#1001", Quantity = 1, UnitPrice = 10, UnitCost = 2 });
        b.Piece.Sales.Add(new Sale { Quantity = 1, UnitPrice = 5 });

        var orders = SalesReport.Orders(SalesReport.Rows([a, b]));
        var totals = SalesReport.Totals(orders);

        Assert.Equal(2, orders.Count);
        var order = orders.Single(o => o.OrderRef == "#1001");
        Assert.Equal(50, order.Revenue);
        Assert.Equal(8, order.Cost);
        Assert.Equal(41, order.Profit);
        Assert.Equal(55, totals.Revenue);
    }
}

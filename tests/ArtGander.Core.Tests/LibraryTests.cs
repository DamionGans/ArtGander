using ArtGander.Core.Library;
using ArtGander.Core.Model;
using ArtGander.Core.Services;
using ArtGander.Core.Storage;

namespace ArtGander.Core.Tests;

public class LibraryTests
{
    private readonly ArtPieceStore _store = new();
    private PieceService Service => new(_store);

    private (PieceService Service, ProductCatalog Catalog) WithCatalog(TempLibrary lib)
    {
        var catalog = new ProductCatalog();
        catalog.SetLoaded(lib.Root, []);
        return (new PieceService(_store, catalog), catalog);
    }

    [Fact]
    public void The_cover_is_the_preview_and_follows_renames()
    {
        using var lib = new TempLibrary();
        lib.WriteFile("Goose/Goose-photo.jpg");
        lib.WriteFile("Goose/Goose.png"); // the official cropped art
        var doc = Service.AdoptFolder(Path.Combine(lib.Root, "Goose"));

        var cover = doc.Piece.FindFile("Goose.png")!;
        Assert.Equal("", cover.Version);
        Assert.Equal(FileKind.Cover, cover.Kind);
        var folder = Assert.Single(new LibraryScanner(_store).Scan(lib.Root, TestContext.Current.CancellationToken));
        Assert.Empty(folder.MisnamedFiles);
        Assert.Equal(Path.Combine(lib.Root, "Goose", "Goose.png"), folder.PreviewFilePath);

        Service.RenamePiece(doc, "Goose On The Loose");
        Assert.True(File.Exists(Path.Combine(lib.Root, "Goose On The Loose", "Goose_On_The_Loose.png")));
    }

    [Fact]
    public void All_incompatible_names_are_fixed_with_one_call()
    {
        using var lib = new TempLibrary();
        // A piece with two files that don't follow the convention.
        lib.WriteFile("Goose/scan from phone.jpg");
        lib.WriteFile("Goose/Goose_master.PNG");
        Service.AdoptFolder(Path.Combine(lib.Root, "Goose"));
        // A folder whose name breaks the rules (underscore), with a convention-named file and a product link to it.
        lib.WriteFile("Swan_Lake/whatever.png");
        var swan = Service.AdoptFolder(Path.Combine(lib.Root, "Swan_Lake"));
        // A project with a piece inside whose folder name breaks the rules.
        var book = Service.CreateProject(lib.Root, "Goose Book");
        lib.WriteFile("Goose Book/Page__1/page.png");
        Service.AdoptFolder(Path.Combine(book.FolderPath, "Page__1"));
        var (service, catalog) = WithCatalog(lib);
        var swanFile = swan.Piece.Files.Single().FileName;
        catalog.Save(new Product { Name = "Swan print", Files = { new ProductFile { PieceId = swan.Piece.Id, FileName = swanFile } } });

        var scanned = new LibraryScanner(_store).Scan(lib.Root, TestContext.Current.CancellationToken);
        var all = scanned.SelectMany(p => p.Children.Prepend(p)).ToList();
        var plan = PieceService.PlanNameFixes(all);
        Assert.Contains(plan, f => f.IsFolder && f.OldName == "Swan_Lake" && f.NewName == "Swan Lake");
        Assert.Contains(plan, f => f.IsFolder && f.OldName == "Page__1" && f.NewName == "Page 1");
        Assert.Contains(plan, f => !f.IsFolder && f.OldName == "scan from phone.jpg" && f.NewName == "Goose-scan-from-phone.jpg");

        var result = service.FixAllNames(scanned);

        Assert.Empty(result.Problems);
        var after = new LibraryScanner(_store).Scan(lib.Root, TestContext.Current.CancellationToken);
        Assert.Empty(PieceService.PlanNameFixes(after.SelectMany(p => p.Children.Prepend(p))));
        Assert.True(Directory.Exists(Path.Combine(lib.Root, "Swan Lake")));
        Assert.True(Directory.Exists(Path.Combine(lib.Root, "Goose Book", "Page 1")));
        Assert.True(File.Exists(Path.Combine(lib.Root, "Goose", "Goose-scan-from-phone.jpg")));
        Assert.StartsWith("Swan_Lake-", Assert.Single(new ProductStore(lib.Root, null).LoadAll()).Files[0].FileName); // product link followed
    }

    [Fact]
    public void Creates_piece_folder_with_details_file()
    {
        using var lib = new TempLibrary();
        var doc = Service.CreatePiece(lib.Root, "Goose: On The Loose", new LibrarySettings { DefaultCopyrightOwner = "Goose Studio" });

        Assert.Equal(Path.Combine(lib.Root, "Goose On The Loose"), doc.FolderPath);
        Assert.True(File.Exists(Path.Combine(doc.FolderPath, ".artgander.json")));
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
        var (service, catalog) = WithCatalog(lib);
        var product = new Product { Name = "Print", Pieces = [doc.Piece.Id], Files = { new ProductFile { PieceId = doc.Piece.Id, FileName = entry.FileName } } };
        catalog.Save(product);

        service.ApplyConvention(doc, entry);

        Assert.Equal("Goose-scan-from-phone.jpg", entry.FileName);
        Assert.Equal("Goose-scan-from-phone.jpg", Assert.Single(new ProductStore(lib.Root, null).LoadAll()).Files[0].FileName);
        Assert.True(File.Exists(Path.Combine(doc.FolderPath, "Goose-scan-from-phone.jpg")));
    }

    [Fact]
    public void Library_file_kinds_are_recognized_from_version_names()
    {
        string[] kinds = [.. FileKind.Defaults, "sticker sheet", "social media"];

        Assert.Equal("sticker sheet", PieceService.GuessKind("sticker-sheet-a5", ".png", kinds));
        Assert.Equal("social media", PieceService.GuessKind("social-media-square", ".jpg", kinds));
        Assert.Equal(FileKind.Transparent, PieceService.GuessKind("no-bg", ".png", kinds));
        Assert.Equal(FileKind.Other, PieceService.GuessKind("something", ".png", kinds));
    }

    [Fact]
    public void Old_library_settings_get_default_file_kinds()
    {
        using var lib = new TempLibrary();
        lib.WriteFile(".artgander/library.json", """{ "schemaVersion": 1, "defaultCurrency": "USD" }""");
        var settings = LibrarySettingsStore.Load(lib.Root);
        Assert.Equal(FileKind.Defaults, settings.FileKinds);

        settings.FileKinds.Add("sticker sheet");
        LibrarySettingsStore.Save(lib.Root, settings);
        Assert.Contains("sticker sheet", LibrarySettingsStore.Load(lib.Root).FileKinds);
    }

    [Fact]
    public void Adds_pieces_to_a_collection_once()
    {
        using var lib = new TempLibrary();
        var doc = Service.CreatePiece(lib.Root, "Goose");

        Assert.True(Service.AddToCollection(doc.FolderPath, " Birds "));
        Assert.False(Service.AddToCollection(doc.FolderPath, "birds"));

        Assert.Equal(["Birds"], _store.Load(doc.FolderPath).Piece.Collections);
    }

    [Fact]
    public void Copies_with_numbered_suffix_get_unique_convention_names()
    {
        using var lib = new TempLibrary();
        lib.WriteFile("Goose/Goose (2).png");
        lib.WriteFile("Goose/sketch (2).png");
        lib.WriteFile("Goose/sketch-2.png");
        var doc = Service.AdoptFolder(Path.Combine(lib.Root, "Goose"));

        foreach (var entry in doc.Piece.Files.ToList()) Service.ApplyConvention(doc, entry);

        var names = Directory.GetFiles(doc.FolderPath, "*.png").Select(Path.GetFileName).Order().ToList();
        Assert.Equal(["Goose-2.png", "Goose-sketch-2-2.png", "Goose-sketch-2.png"], names);
        Assert.All(doc.Piece.Files, f => Assert.True(File.Exists(Path.Combine(doc.FolderPath, f.FileName))));
    }

    [Fact]
    public void Renaming_a_piece_renames_folder_files_and_links()
    {
        using var lib = new TempLibrary();
        var doc = Service.CreatePiece(lib.Root, "Goose");
        var src = lib.WriteFile("in/a.png");
        var file = Service.ImportFile(doc, src, "transparent", FileKind.Transparent);
        var (service, catalog) = WithCatalog(lib);
        catalog.Save(new Product { Name = "Sticker", Files = { new ProductFile { PieceId = doc.Piece.Id, FileName = file.FileName } } });

        service.RenamePiece(doc, "Goose On The Loose");

        Assert.Equal(Path.Combine(lib.Root, "Goose On The Loose"), doc.FolderPath);
        Assert.False(Directory.Exists(Path.Combine(lib.Root, "Goose")));
        Assert.True(File.Exists(Path.Combine(doc.FolderPath, "Goose_On_The_Loose-transparent.png")));
        var reloaded = _store.Load(doc.FolderPath);
        Assert.Equal("Goose On The Loose", reloaded.Piece.Title);
        Assert.Equal("Goose_On_The_Loose-transparent.png", Assert.Single(new ProductStore(lib.Root, null).LoadAll()).Files[0].FileName);
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
        Assert.True(File.Exists(Path.Combine(lib.Root, "Goose", ".artgander.json")));
    }

    [Fact]
    public void Untracking_keeps_the_file_and_removes_links()
    {
        using var lib = new TempLibrary();
        var doc = Service.CreatePiece(lib.Root, "Goose");
        var entry = Service.ImportFile(doc, lib.WriteFile("in/a.png"), "master", FileKind.Master);
        var (service, catalog) = WithCatalog(lib);
        var product = new Product { Files = { new ProductFile { PieceId = doc.Piece.Id, FileName = entry.FileName } } };
        catalog.Save(product);

        service.UntrackFile(doc, entry);

        Assert.Empty(doc.Piece.Files);
        Assert.Empty(product.Files);
        Assert.True(File.Exists(Path.Combine(doc.FolderPath, "Goose-master.png")));
    }

    [Fact]
    public void Sales_report_groups_orders_across_pieces()
    {
        using var lib = new TempLibrary();
        var a = Service.CreatePiece(lib.Root, "A");
        var b = Service.CreatePiece(lib.Root, "B");
        var pa = new Product { Name = "Print", ProductionCost = 3, Pieces = [a.Piece.Id] };
        var o1 = new Order { OrderRef = "#1001" };
        o1.Lines.Add(new OrderLine { ProductId = pa.Id, Quantity = 2, UnitPrice = 20, Fees = 1 });
        o1.Lines.Add(new OrderLine { PieceId = b.Piece.Id, Quantity = 1, UnitPrice = 10, UnitCost = 2 });
        var o2 = new Order();
        o2.Lines.Add(new OrderLine { PieceId = b.Piece.Id, Quantity = 1, UnitPrice = 5 });

        var orders = SalesReport.Orders([o1, o2], [pa], [a, b]);
        var totals = SalesReport.Totals(orders);

        Assert.Equal(2, orders.Count);
        var order = orders.Single(o => o.OrderRef == "#1001");
        Assert.Equal(50, order.Revenue);
        Assert.Equal(8, order.Cost);
        Assert.Equal(41, order.Profit);
        Assert.Equal(55, totals.Revenue);
    }
}

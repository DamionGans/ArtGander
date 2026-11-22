using ArtGander.Core.Model;
using ArtGander.Core.Services;
using ArtGander.Core.Storage;

namespace ArtGander.Core.Tests;

public class ProductTests
{
    private const string V2Piece = """
        {
          "schemaVersion": 2,
          "type": "piece",
          "status": "finished",
          "id": "11111111-1111-1111-1111-111111111111",
          "title": "Goose",
          "files": [ { "fileName": "Goose-transparent.png", "version": "transparent", "kind": "transparent" } ],
          "products": [
            { "id": "22222222-2222-2222-2222-222222222222", "name": "Goose mug", "type": "printOnDemand", "files": ["Goose-transparent.png"],
              "price": 18.5, "productionCost": 7, "listings": [ { "channel": "shopify", "url": "https://shop/goose-mug" } ] }
          ],
          "sales": [
            { "id": "33333333-3333-3333-3333-333333333333", "date": "2026-05-01", "orderRef": "#1001", "channel": "shopify",
              "productId": "22222222-2222-2222-2222-222222222222", "quantity": 2, "unitPrice": 18.5, "customer": "Honk B." },
            { "id": "44444444-4444-4444-4444-444444444444", "date": "2026-05-02", "quantity": 1, "unitPrice": 50, "channel": "inPerson" }
          ]
        }
        """;

    [Fact]
    public void Version_2_products_and_sales_move_to_their_own_files()
    {
        using var lib = new TempLibrary();
        var folder = lib.Folder("Goose");
        File.WriteAllText(Path.Combine(folder, ".artgander.json"), V2Piece);
        // An order file from 0.2 with details for #1001.
        Records.Orders.Save(lib.Root, new Order { OrderRef = "#1001", Carrier = "USPS" });

        var store = new ArtPieceStore();
        var doc = store.Load(folder);
        Assert.Equal(2, doc.UpgradedFromVersion);
        Assert.NotNull(doc.Piece.LegacyProducts);
        Assert.True(LibraryMigrator.NeedsMigration([doc]));

        var catalog = new ProductCatalog();
        var result = LibraryMigrator.Run(lib.Root, [doc], store, catalog);

        Assert.Empty(result.Problems);
        Assert.Equal(1, result.Products);
        Assert.Equal(2, result.OrderLines);

        var product = Assert.Single(new ProductStore(lib.Root, null).LoadAll());
        Assert.Equal(Guid.Parse("22222222-2222-2222-2222-222222222222"), product.Id);
        Assert.Equal("Goose mug", product.Name);
        Assert.Equal([doc.Piece.Id], product.Pieces);
        Assert.Equal("Goose-transparent.png", Assert.Single(product.Files).FileName);
        Assert.Equal(Fulfillment.Printify, product.Fulfillment);

        var orders = Records.Orders.LoadAll(lib.Root);
        Assert.Equal(2, orders.Count);
        var o1001 = orders.Single(o => o.OrderRef == "#1001");
        Assert.Equal("USPS", o1001.Carrier); // existing details kept
        Assert.Equal("Honk B.", o1001.Customer);
        var line = Assert.Single(o1001.Lines);
        Assert.Equal(product.Id, line.ProductId);
        Assert.Equal(Fulfillment.Printify, line.Fulfillment);
        Assert.Equal(new DateOnly(2026, 5, 2), orders.Single(o => o.OrderRef == "").Date);

        var reloaded = store.Load(folder);
        Assert.Null(reloaded.Piece.LegacyProducts);
        Assert.Null(reloaded.Piece.LegacySales);
        Assert.DoesNotContain("legacy", File.ReadAllText(Path.Combine(folder, ".artgander.json")));
        Assert.True(File.Exists(Path.Combine(folder, ".artgander.json.v2.bak")));

        var summary = SalesReport.Orders(orders, [product], [reloaded]).Single(o => o.OrderRef == "#1001");
        Assert.Equal(14, summary.ProductionCost);
    }

    [Fact]
    public void Migration_can_run_again_without_duplicates()
    {
        using var lib = new TempLibrary();
        var folder = lib.Folder("Goose");
        File.WriteAllText(Path.Combine(folder, ".artgander.json"), V2Piece);
        var store = new ArtPieceStore();
        var catalog = new ProductCatalog();
        var doc = store.Load(folder);

        // Simulate an interruption: products and orders written, but the piece not cleaned up yet.
        var copy = store.Load(folder);
        LibraryMigrator.Run(lib.Root, [doc], store, catalog);
        File.Delete(Path.Combine(folder, ".artgander.json")); // hidden files can't be overwritten directly on Windows
        File.WriteAllText(Path.Combine(folder, ".artgander.json"), V2Piece);
        var again = store.Load(folder);
        LibraryMigrator.Run(lib.Root, [again], store, catalog);

        Assert.Single(new ProductStore(lib.Root, null).LoadAll());
        Assert.Equal(2, Records.Orders.LoadAll(lib.Root).Sum(o => o.Lines.Count));
        Assert.NotNull(copy);
    }

    [Fact]
    public void Saving_a_product_changed_elsewhere_is_refused()
    {
        using var lib = new TempLibrary();
        var store = new ProductStore(lib.Root, null);
        var mine = new Product { Name = "Poster" };
        store.Save(mine);

        var theirs = store.Reload(mine)!;
        theirs.Price = 30;
        store.Save(theirs);

        mine.Price = 25;
        Assert.True(store.ChangedOnDisk(mine));
        Assert.Throws<ConcurrentEditException>(() => store.Save(mine));
        Assert.Equal(30, store.Reload(mine)!.Price);
    }

    [Fact]
    public void Renaming_a_product_renames_its_folder_and_pictures()
    {
        using var lib = new TempLibrary();
        var store = new ProductStore(lib.Root, null);
        var product = new Product { Name = "Poster" };
        store.Save(product);
        var folder = ProductStore.FolderOf(product)!;
        Assert.Equal(Path.Combine(lib.Root, "Products", "Poster"), folder);
        File.WriteAllText(Path.Combine(folder, "Poster-living-room.jpg"), "jpg");
        File.WriteAllText(Path.Combine(folder, "my own name.jpg"), "jpg");
        product.Images.Add(new ProductImage { FileName = "Poster-living-room.jpg", Label = "living-room" });
        product.Images.Add(new ProductImage { FileName = "my own name.jpg" });
        store.Save(product);

        product.Name = "Goose poster A3";
        store.Save(product);

        var renamed = Path.Combine(lib.Root, "Products", "Goose poster A3");
        Assert.Equal(Path.Combine(renamed, ProductStore.FileName), product.StoredFileName);
        Assert.False(Directory.Exists(folder));
        Assert.True(File.Exists(Path.Combine(renamed, "Goose_poster_A3-living-room.jpg")));
        Assert.True(File.Exists(Path.Combine(renamed, "my own name.jpg"))); // not following the convention: left alone
        Assert.Equal("Goose_poster_A3-living-room.jpg", product.Images[0].FileName);
        Assert.Equal("Goose_poster_A3-living-room.jpg", Assert.Single(store.LoadAll()).Images[0].FileName);
    }

    [Fact]
    public void Products_of_one_art_piece_are_grouped_in_its_folder()
    {
        using var lib = new TempLibrary();
        var goose = Guid.NewGuid();
        var book = Guid.NewGuid();
        var names = new Dictionary<Guid, string> { [goose] = "Goose On The Loose", [book] = "Goose Book" };
        var catalog = new ProductCatalog();
        catalog.SetLoaded(lib.Root, []);
        catalog.GroupNameFor = id => names.GetValueOrDefault(id);

        var canvas = new Product { Name = "Goose Canvas", Pieces = [goose] };
        var mug = new Product { Name = "Goose Mug", Pieces = [goose] };
        var bookProduct = new Product { Name = "Hardcover", Pieces = [book] };
        var set = new Product { Name = "Set of 3", Pieces = [goose, book] };
        var loose = new Product { Name = "Gift card" };
        foreach (var p in new[] { canvas, mug, bookProduct, set, loose }) catalog.Save(p);

        string Rel(Product p) => Path.GetRelativePath(lib.Root, ProductCatalog.FolderOf(p)!);
        Assert.Equal(Path.Combine("Products", "Goose On The Loose", "Goose Canvas"), Rel(canvas));
        Assert.Equal(Path.Combine("Products", "Goose On The Loose", "Goose Mug"), Rel(mug));
        Assert.Equal(Path.Combine("Products", "Goose Book", "Hardcover"), Rel(bookProduct)); // a project groups too
        Assert.Equal(Path.Combine("Products", "Set of 3"), Rel(set));
        Assert.Equal(Path.Combine("Products", "Gift card"), Rel(loose));
        Assert.Empty(catalog.PlanTidy());

        // Unlinking from the piece: the folder isn't moved silently, but the move is offered.
        mug.Pieces.Clear();
        catalog.Save(mug);
        Assert.Equal(Path.Combine("Products", "Gift card".Replace("Gift card", "Goose Mug")), catalog.PendingMove(mug));
        Assert.True(catalog.MoveToPlace(mug));
        Assert.Equal(Path.Combine("Products", "Goose Mug"), Rel(mug));

        // Moving the last product out removes the empty group.
        canvas.Pieces.Clear();
        catalog.Save(canvas);
        catalog.MoveToPlace(canvas);
        Assert.False(Directory.Exists(Path.Combine(lib.Root, "Products", "Goose On The Loose")));

        // All five are found again, one or two levels deep.
        Assert.Equal(5, new ProductStore(lib.Root, null).LoadAll().Count);
    }

    [Fact]
    public void Renaming_an_art_piece_renames_its_product_group()
    {
        using var lib = new TempLibrary();
        var store = new ArtPieceStore();
        var catalog = new ProductCatalog();
        catalog.SetLoaded(lib.Root, []);
        var service = new PieceService(store, catalog);
        var doc = service.CreatePiece(lib.Root, "Goose");
        catalog.GroupNameFor = id => id == doc.Piece.Id ? doc.FolderName : null;
        var mug = new Product { Name = "Mug", Pieces = [doc.Piece.Id] };
        catalog.Save(mug);
        Assert.True(Directory.Exists(Path.Combine(lib.Root, "Products", "Goose", "Mug")));

        service.RenamePiece(doc, "Goose On The Loose");

        Assert.False(Directory.Exists(Path.Combine(lib.Root, "Products", "Goose")));
        Assert.Equal(Path.Combine(lib.Root, "Products", "Goose On The Loose", "Mug", ProductStore.FileName), mug.StoredFileName);
        Assert.Empty(catalog.PlanTidy());
    }

    [Fact]
    public void A_product_moved_by_hand_is_found_and_offered_in_tidy_up()
    {
        using var lib = new TempLibrary();
        var goose = Guid.NewGuid();
        var catalog = new ProductCatalog();
        catalog.SetLoaded(lib.Root, []);
        catalog.GroupNameFor = id => id == goose ? "Goose" : null;
        var mug = new Product { Name = "Mug", Pieces = [goose] };
        catalog.Save(mug);
        Directory.Move(Path.Combine(lib.Root, "Products", "Goose", "Mug"), Path.Combine(lib.Root, "Products", "Mug"));

        catalog.Load(lib.Root);
        catalog.GroupNameFor = id => id == goose ? "Goose" : null;
        var move = Assert.Single(catalog.PlanTidy());
        Assert.Equal(Path.Combine("Products", "Goose", "Mug"), move.To);
        Assert.Empty(catalog.Tidy());
        Assert.True(File.Exists(Path.Combine(lib.Root, "Products", "Goose", "Mug", ProductStore.FileName)));
    }

    [Fact]
    public void Version_3_products_move_into_folders_and_are_backed_up()
    {
        using var lib = new TempLibrary();
        var goose = Guid.NewGuid();
        var poster = new Product { Name = "Goose poster", Pieces = [goose], Price = 20 };
        Records.LegacyProducts.Save(lib.Root, poster);
        Records.LegacyProducts.Save(lib.Root, new Product { Name = "Gift card" });
        Assert.True(ProductFolderMigrator.NeedsMigration(lib.Root));

        var catalog = new ProductCatalog();
        catalog.GroupNameFor = id => id == goose ? "Goose" : null;
        var settings = new LibrarySettings();
        var result = ProductFolderMigrator.Run(lib.Root, catalog, settings);

        Assert.Empty(result.Problems);
        Assert.Equal(2, result.Moved);
        Assert.False(ProductFolderMigrator.NeedsMigration(lib.Root));
        Assert.True(File.Exists(Path.Combine(lib.Root, "Products", "Goose", "Goose poster", ProductStore.FileName)));
        Assert.True(File.Exists(Path.Combine(lib.Root, "Products", "Gift card", ProductStore.FileName)));
        Assert.Equal(2, Directory.GetFiles(Path.Combine(lib.Root, ".artgander", "backup"), "*.json", SearchOption.AllDirectories).Length);

        // An older ArtGander on another computer saves the poster the old way again: picked up, nothing duplicated.
        poster.StoredFileName = null;
        poster.Price = 25;
        poster.UpdatedAt = DateTimeOffset.Now.AddMinutes(5);
        Assert.False(Directory.Exists(Records.LegacyProducts.FolderFor(lib.Root))); // the empty old folder was removed
        Directory.CreateDirectory(Records.LegacyProducts.FolderFor(lib.Root));
        File.WriteAllText(Path.Combine(Records.LegacyProducts.FolderFor(lib.Root), "Goose_poster-again.json"), Schema.ArtGanderJson.Serialize(poster));
        var again = ProductFolderMigrator.Run(lib.Root, catalog, settings);
        Assert.Equal(1, again.Updated);
        var all = new ProductStore(lib.Root, null).LoadAll();
        Assert.Equal(2, all.Count);
        Assert.Equal(25, all.Single(p => p.Id == poster.Id).Price);
    }

    [Fact]
    public void An_art_piece_called_Products_keeps_its_folder()
    {
        using var lib = new TempLibrary();
        var store = new ArtPieceStore();
        new PieceService(store).CreatePiece(lib.Root, "Products");
        Assert.Equal("Shop Products", ProductFolderMigrator.ChooseProductsFolder(lib.Root, "Products"));

        new ProductStore(lib.Root, "Shop Products").Save(new Product { Name = "Mug" });
        Assert.Equal("Shop Products", ProductFolderMigrator.ChooseProductsFolder(lib.Root, "Shop Products"));
        var settings = new LibrarySettings { ProductsFolder = "Shop Products" };
        LibrarySettingsStore.Save(lib.Root, settings);
        var scanned = new Library.LibraryScanner(store).Scan(lib.Root, TestContext.Current.CancellationToken);
        Assert.Equal("Products", Assert.Single(scanned).Title); // the piece shows; the products folder doesn't
    }

    [Fact]
    public void Products_and_pieces_link_both_ways()
    {
        using var lib = new TempLibrary();
        var catalog = new ProductCatalog();
        catalog.SetLoaded(lib.Root, []);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var poster = new Product { Name = "Poster" };
        var mug = new Product { Name = "Mug" };
        catalog.Save(poster);
        catalog.Save(mug);

        catalog.Link(poster, a);
        catalog.Link(poster, b);
        catalog.Link(mug, a);

        Assert.Equal(2, catalog.ForPiece(a).Count);
        Assert.Equal([poster], catalog.ForPiece(b));
        catalog.Unlink(poster, b);
        Assert.Empty(catalog.ForPiece(b));

        catalog.Load(lib.Root);
        Assert.Equal(2, catalog.ForPiece(a).Count);
    }

    [Fact]
    public void Library_lock_blocks_a_second_computer_until_released_or_stale()
    {
        using var lib = new TempLibrary();
        var start = DateTimeOffset.Now;
        using (var held = LibraryLock.Acquire(lib.Root, "syncing", "MAC", start))
        {
            var ex = Assert.Throws<LibraryBusyException>(() => LibraryLock.Acquire(lib.Root, "syncing", "PC", start.AddMinutes(1)));
            Assert.Equal("MAC", ex.Machine);
            Assert.True(held.IsStillMine());
        }
        using (LibraryLock.Acquire(lib.Root, "syncing", "PC", start)) { }

        // A lock that was abandoned (no heartbeat for a long time) is taken over.
        var abandoned = LibraryLock.Acquire(lib.Root, "syncing", "MAC", start.AddHours(-1));
        using var taken = LibraryLock.Acquire(lib.Root, "syncing", "PC", start);
        Assert.False(abandoned.IsStillMine());
        abandoned.Dispose(); // must not delete the new owner's lock
        Assert.True(taken.IsStillMine());
    }
}

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

        var product = Assert.Single(Records.Products.LoadAll(lib.Root));
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

        Assert.Single(Records.Products.LoadAll(lib.Root));
        Assert.Equal(2, Records.Orders.LoadAll(lib.Root).Sum(o => o.Lines.Count));
        Assert.NotNull(copy);
    }

    [Fact]
    public void Saving_a_product_changed_elsewhere_is_refused()
    {
        using var lib = new TempLibrary();
        var mine = new Product { Name = "Poster" };
        Records.Products.Save(lib.Root, mine);

        var theirs = Records.Products.Reload(lib.Root, mine)!;
        theirs.Price = 30;
        Records.Products.Save(lib.Root, theirs);

        mine.Price = 25;
        Assert.True(Records.Products.ChangedOnDisk(lib.Root, mine));
        Assert.Throws<ConcurrentEditException>(() => Records.Products.Save(lib.Root, mine));
        Assert.Equal(30, Records.Products.Reload(lib.Root, mine)!.Price);
    }

    [Fact]
    public void Renaming_a_product_renames_its_file()
    {
        using var lib = new TempLibrary();
        var product = new Product { Name = "Poster" };
        Records.Products.Save(lib.Root, product);
        var first = product.StoredFileName;
        product.Name = "Goose poster A3";
        Records.Products.Save(lib.Root, product);

        Assert.NotEqual(first, product.StoredFileName);
        Assert.StartsWith("Goose_poster_A3-", product.StoredFileName);
        Assert.Single(Directory.GetFiles(Records.Products.FolderFor(lib.Root)));
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

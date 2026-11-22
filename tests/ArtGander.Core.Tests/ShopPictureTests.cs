using ArtGander.Core.Integrations;
using ArtGander.Core.Model;
using ArtGander.Core.Services;

namespace ArtGander.Core.Tests;

public class ShopPictureTests
{
    private static readonly PictureDownloader FakeDownload = (url, target, ct) => File.WriteAllTextAsync(target, "picture from " + url, ct);
    private static readonly Dictionary<string, SyncResolution> Defaults = [];

    /// <summary>A product you make yourselves, imported from Shopify with one picture there.</summary>
    private static async Task<(TempLibrary Lib, ProductCatalog Catalog, Product Product, FakeShopify Shopify)> SelfMadeAsync()
    {
        var lib = new TempLibrary();
        var shopify = new FakeShopify
        {
            Product = new RemoteProduct
            {
                Channel = "shopify", Id = "gid://shopify/Product/900", Title = "Hand-printed goose", Status = "active", UpdatedAt = "s1",
                Variants = [new RemoteVariant { Id = "gid://shopify/ProductVariant/9", Title = "Default Title", Price = 60m, Stock = 5, InventoryItemId = "gid://shopify/InventoryItem/9" }],
                Images = [new RemoteImage("gid://shopify/MediaImage/1", "https://cdn.shopify.com/s/files/goose.jpg", "Goose print on a wall")],
            },
        };
        var candidate = Assert.Single(ProductImporter.Plan([], [shopify.Product], [], []));
        var product = ProductImporter.Apply(candidate, [], "EUR");
        var catalog = new ProductCatalog();
        catalog.SetLoaded(lib.Root, []);
        catalog.Save(product);
        Assert.Equal(1, await ShopPictures.DownloadFromShopAsync(product, null, shopify.Product, FakeDownload, CancellationToken.None));
        catalog.Save(product);
        return (lib, catalog, product, shopify);
    }

    private static async Task SyncAsync(ProductCatalog catalog, FakeShopify shopify, string root, Dictionary<string, SyncResolution>? choices = null)
    {
        var plans = SyncPlanner.Plan(catalog.All, [], [shopify.Product]);
        await new SyncRunner(catalog, null, shopify, FakeDownload).ApplyAsync(root, plans, choices ?? Defaults);
    }

    [Fact]
    public async Task Imported_shop_pictures_match_the_shop_so_nothing_needs_syncing()
    {
        var (lib, catalog, product, shopify) = await SelfMadeAsync();
        using var _ = lib;
        var image = Assert.Single(product.Images);
        Assert.Equal(PictureSource.Shopify, image.Source);
        Assert.Equal("Goose print on a wall", image.Alt);
        Assert.Equal("Hand-printed_goose-goose-print-on-a-wall.jpg", image.FileName);
        Assert.Empty(SyncPlanner.Plan(catalog.All, [], [shopify.Product]).SelectMany(p => p.Changes).Select(c => $"{c.Field} {c.Kind}: base={c.Base} local={c.Local} remote={c.Remote}"));
    }

    [Fact]
    public async Task New_pictures_are_uploaded_in_order_with_their_alt_text()
    {
        var (lib, catalog, product, shopify) = await SelfMadeAsync();
        using var _ = lib;
        var detail = ProductImageService.Import(product, lib.WriteFile("detail.jpg", "detail"));
        detail.Alt = "Close-up of the print";
        ProductImageService.MakeMain(product, detail);
        catalog.Save(product);

        var plan = Assert.Single(SyncPlanner.Plan(catalog.All, [], [shopify.Product]));
        var change = Assert.Single(plan.Changes);
        Assert.Equal("pictures", change.Field);
        Assert.Equal(SyncChangeKind.Push, change.Kind);
        Assert.Contains("detail (new)", change.Local);

        await new SyncRunner(catalog, null, shopify, FakeDownload).ApplyAsync(lib.Root, [plan], Defaults);
        Assert.Equal(["upload Hand-printed_goose-detail.jpg", "reorder"], shopify.Calls);
        Assert.Equal(["Close-up of the print", "Goose print on a wall"], shopify.Product.Images.Select(i => i.Alt));
        Assert.True(detail.Shopify!.UploadedByArtGander);
        Assert.Empty(SyncPlanner.Plan(catalog.All, [], [shopify.Product])); // in step now
    }

    [Fact]
    public async Task Alt_text_changes_and_changed_files_are_sent()
    {
        var (lib, catalog, product, shopify) = await SelfMadeAsync();
        using var _ = lib;
        var image = product.Images[0];
        image.Alt = "The goose print, framed";
        catalog.Save(product);
        await SyncAsync(catalog, shopify, lib.Root);
        Assert.Equal(["alt The goose print, framed"], shopify.Calls);

        // A retouched file replaces the picture in the shop.
        shopify.Calls.Clear();
        File.WriteAllText(ProductImageService.PathOf(product, image), "retouched");
        await SyncAsync(catalog, shopify, lib.Root);
        Assert.Equal(["upload Hand-printed_goose-goose-print-on-a-wall.jpg", "remove gid://shopify/MediaImage/1"], shopify.Calls);
        Assert.NotEqual("gid://shopify/MediaImage/1", Assert.Single(shopify.Product.Images).Id);
        Assert.Empty(SyncPlanner.Plan(catalog.All, [], [shopify.Product]));
    }

    [Fact]
    public async Task A_picture_added_in_Shopify_is_taken_over_not_removed()
    {
        var (lib, catalog, product, shopify) = await SelfMadeAsync();
        using var _ = lib;
        shopify.AddByHand("gid://shopify/MediaImage/50", "Lifestyle shot");

        var plan = Assert.Single(SyncPlanner.Plan(catalog.All, [], [shopify.Product]));
        Assert.Equal(SyncChangeKind.OutsideEdit, Assert.Single(plan.Changes).Kind);
        Assert.Contains("added in Shopify", plan.Changes[0].Remote);

        await new SyncRunner(catalog, null, shopify, FakeDownload).ApplyAsync(lib.Root, [plan], Defaults); // default: take it over
        Assert.Equal(2, product.Images.Count);
        Assert.Equal("Lifestyle shot", product.Images[1].Alt);
        Assert.True(File.Exists(ProductImageService.PathOf(product, product.Images[1])));
        Assert.Empty(shopify.Calls); // nothing sent to the shop
        Assert.Empty(SyncPlanner.Plan(catalog.All, [], [shopify.Product]));
    }

    [Fact]
    public async Task Removing_a_picture_takes_it_off_the_shop_but_keeps_pictures_added_there()
    {
        var (lib, catalog, product, shopify) = await SelfMadeAsync();
        using var _ = lib;
        var mine = ProductImageService.Import(product, lib.WriteFile("detail.jpg", "detail"));
        catalog.Save(product);
        await SyncAsync(catalog, shopify, lib.Root);
        shopify.Calls.Clear();

        // In ArtGander the first picture is removed, while someone adds one in Shopify: both sides changed.
        ProductImageService.Untrack(product, product.Images[0]);
        catalog.Save(product);
        shopify.AddByHand("gid://shopify/MediaImage/77", "Added in the admin");
        var plan = Assert.Single(SyncPlanner.Plan(catalog.All, [], [shopify.Product]));
        var change = Assert.Single(plan.Changes);
        Assert.Equal(SyncChangeKind.Conflict, change.Kind);
        Assert.Contains("1 taken off the shop", change.Local);

        // You choose ArtGander's pictures: only the removed one goes; the admin's picture stays, at the end.
        await new SyncRunner(catalog, null, shopify, FakeDownload).ApplyAsync(lib.Root, [plan],
            new Dictionary<string, SyncResolution> { [SyncRunner.ChoiceKey(product, change)] = SyncResolution.Push });
        Assert.Contains("remove gid://shopify/MediaImage/1", shopify.Calls);
        Assert.Equal([mine.Shopify!.MediaId, "gid://shopify/MediaImage/77"], shopify.Product.Images.Select(i => i.Id));
    }

    [Fact]
    public async Task Printify_mockups_are_downloaded_and_never_uploaded()
    {
        using var lib = new TempLibrary();
        var catalog = new ProductCatalog();
        catalog.SetLoaded(lib.Root, []);
        var poster = new Product { Name = "Goose poster", Type = ProductType.PrintOnDemand };
        catalog.Save(poster);
        var printify = new RemoteProduct
        {
            Channel = "printify", Id = "p1",
            Images =
            [
                new RemoteImage("https://images-api.printify.com/mockup/1.jpg?camera_label=front", "https://images-api.printify.com/mockup/1.jpg?camera_label=front"),
                new RemoteImage("https://images-api.printify.com/mockup/1.jpg?camera_label=back", "https://images-api.printify.com/mockup/1.jpg?camera_label=back", Selected: false),
                new RemoteImage("https://images-api.printify.com/mockup/1.jpg?camera_label=context-1", "https://images-api.printify.com/mockup/1.jpg?camera_label=context-1"),
            ],
        };
        Assert.Equal(2, await ShopPictures.DownloadFromShopAsync(poster, printify, null, FakeDownload, CancellationToken.None));
        Assert.Equal(["Goose_poster-mockup-front.jpg", "Goose_poster-mockup-context-1.jpg"], poster.Images.Select(i => i.FileName));
        Assert.All(poster.Images, i => Assert.Equal(PictureSource.Printify, i.Source));

        // Printify drops the context mockup: its downloaded copy goes too; your own photo stays.
        var own = ProductImageService.Import(poster, lib.WriteFile("photo.jpg"));
        printify = printify with { Images = [printify.Images[0]] };
        Assert.Equal(0, await ShopPictures.DownloadFromShopAsync(poster, printify, null, FakeDownload, CancellationToken.None));
        Assert.Equal(["Goose_poster-mockup-front.jpg", own.FileName], poster.Images.Select(i => i.FileName));
        Assert.False(File.Exists(Path.Combine(ProductImageService.FolderOf(poster), "Goose_poster-mockup-context-1.jpg")));

        // In mockup mode the shop's pictures are Printify's business: nothing to sync.
        Assert.DoesNotContain(ShopPictures.ShopImages(poster), i => i.Source == PictureSource.Printify);
        Assert.Equal(PictureMode.PrintifyMockups, poster.EffectivePictures);
    }

    [Theory]
    [InlineData("https://cdn.shopify.com/s/files/1/goose.jpg", true)]
    [InlineData("https://images-api.printify.com/mockup/1.jpg", true)]
    [InlineData("http://cdn.shopify.com/goose.jpg", false)]
    [InlineData("https://evil.example.com/cdn.shopify.com/goose.jpg", false)]
    [InlineData("https://shopify.com.evil.example/goose.jpg", false)]
    public void Only_downloads_from_the_shops_picture_servers(string url, bool allowed) =>
        Assert.Equal(allowed, ShopPictures.IsAllowedSource(url));
}

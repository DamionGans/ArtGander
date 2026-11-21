using ArtGander.Core.Integrations;
using ArtGander.Core.Model;
using ArtGander.Core.Services;

namespace ArtGander.Core.Tests;

/// <summary>A pretend Printify shop.</summary>
internal sealed class FakePrintify : IPrintifyApi
{
    public RemoteProduct Product { get; set; } = null!;
    public List<string> Calls { get; } = [];
    public int Version { get; private set; } = 1;

    public Task<RemoteProduct> GetProductAsync(long shopId, string productId, CancellationToken ct = default) => Task.FromResult(Product);

    public Task UpdateProductAsync(long shopId, string productId, PrintifyUpdate update, CancellationToken ct = default)
    {
        Calls.Add("update " + string.Join(",", new[] { update.Title is null ? "" : "title", update.Description is null ? "" : "description",
            update.Tags is null ? "" : "tags", update.Variants is null ? "" : "variants" }.Where(x => x.Length > 0)));
        Product = Product with
        {
            Title = update.Title ?? Product.Title,
            Description = update.Description ?? Product.Description,
            Tags = update.Tags ?? Product.Tags,
            Variants = update.Variants is { } vs ? Product.Variants.Select(v => vs.FirstOrDefault(x => x.Id == v.Id) is { } n ? v with { Price = n.Price, Enabled = n.Enabled } : v).ToList() : Product.Variants,
            UpdatedAt = "v" + ++Version,
        };
        return Task.CompletedTask;
    }

    public Task PublishAsync(long shopId, string productId, PublishParts parts, CancellationToken ct = default)
    {
        Calls.Add($"publish title={parts.Title} variants={parts.Variants}");
        return Task.CompletedTask;
    }
}

/// <summary>A pretend Shopify store.</summary>
internal sealed class FakeShopify : IShopifyApi
{
    public RemoteProduct Product { get; set; } = null!;
    public List<string> Calls { get; } = [];
    private int _version = 1;

    public Task<RemoteProduct?> GetProductAsync(string id, CancellationToken ct = default) => Task.FromResult<RemoteProduct?>(Product);

    public Task UpdateProductAsync(string productId, string? title, string? descriptionHtml, IReadOnlyList<string>? tags, string? status,
        IReadOnlyList<RemoteVariant>? variantPrices, CancellationToken ct = default)
    {
        Calls.Add($"update title={title} status={status} prices={variantPrices?.Count ?? 0}");
        Product = Product with
        {
            Title = title ?? Product.Title,
            Status = status ?? Product.Status,
            Variants = Product.Variants.Select(v => variantPrices?.FirstOrDefault(x => x.Id == v.Id) is { } n ? v with { Price = n.Price } : v).ToList(),
            UpdatedAt = "s" + ++_version,
        };
        return Task.CompletedTask;
    }

    public Task SetStockAsync(string locationId, IReadOnlyList<(string InventoryItemId, int Quantity, int? ExpectedCurrent)> items, CancellationToken ct = default)
    {
        foreach (var (item, qty, expected) in items) Calls.Add($"stock {item}={qty} if {expected}");
        Product = Product with { Variants = Product.Variants.Select(v => items.FirstOrDefault(i => i.InventoryItemId == v.InventoryItemId) is { InventoryItemId: not null } i ? v with { Stock = i.Quantity } : v).ToList() };
        return Task.CompletedTask;
    }

    public Task<string?> GetPrimaryLocationIdAsync(CancellationToken ct = default) => Task.FromResult<string?>("gid://shopify/Location/1");
}

public class SyncTests
{
    private static (ProductCatalog Catalog, Product Product, FakePrintify Printify, FakeShopify Shopify) PrintifySetup(TempLibrary lib)
    {
        var printify = new FakePrintify
        {
            Product = new RemoteProduct
            {
                Channel = "printify", Id = "pf1", ShopId = 42, Title = "Goose poster", Description = "Honk", Tags = ["goose"], UpdatedAt = "v1", ExternalId = "777",
                Variants = [new RemoteVariant { Id = "101", Title = "A3", Sku = "GP-A3", Price = 25m, Enabled = true },
                            new RemoteVariant { Id = "102", Title = "A2", Sku = "GP-A2", Price = 35m, Enabled = true },
                            new RemoteVariant { Id = "103", Title = "A1", Sku = "GP-A1", Price = 45m, Enabled = false }],
            },
        };
        var shopify = new FakeShopify
        {
            Product = new RemoteProduct
            {
                Channel = "shopify", Id = "gid://shopify/Product/777", Title = "Goose poster", Description = "Honk", Tags = ["goose"], Status = "active", UpdatedAt = "s1",
                Variants = [new RemoteVariant { Id = "gid://shopify/ProductVariant/5001", Title = "A3", Sku = "GP-A3", Price = 25m },
                            new RemoteVariant { Id = "gid://shopify/ProductVariant/5002", Title = "A2", Sku = "GP-A2", Price = 35m }],
            },
        };
        var candidate = Assert.Single(ProductImporter.Plan([printify.Product], [shopify.Product], [], []));
        var product = ProductImporter.Apply(candidate, [], "EUR");
        var catalog = new ProductCatalog();
        catalog.SetLoaded(lib.Root, []);
        catalog.Save(product);
        return (catalog, product, printify, shopify);
    }

    [Fact]
    public void Nothing_to_do_right_after_an_import()
    {
        using var lib = new TempLibrary();
        var (catalog, _, printify, shopify) = PrintifySetup(lib);
        Assert.Empty(SyncPlanner.Plan(catalog.All, [printify.Product], [shopify.Product]));
    }

    [Fact]
    public async Task ArtGander_edits_go_to_Printify_which_publishes_to_Shopify()
    {
        using var lib = new TempLibrary();
        var (catalog, product, printify, shopify) = PrintifySetup(lib);
        product.Name = "Goose On The Loose poster";
        product.Variants.Single(v => v.Sku == "GP-A2").Price = 39m;
        catalog.Save(product);

        var plan = Assert.Single(SyncPlanner.Plan(catalog.All, [printify.Product], [shopify.Product]));
        Assert.All(plan.Changes, c => Assert.Equal(SyncChangeKind.Push, c.Kind));
        Assert.All(plan.Changes, c => Assert.Equal("printify", c.Channel)); // never written to Shopify directly
        Assert.Equal(2, plan.Changes.Count);

        var outcome = Assert.Single(await new SyncRunner(catalog, printify, shopify).ApplyAsync(lib.Root, [plan], new Dictionary<string, SyncResolution>()));

        Assert.True(outcome.Done, outcome.Message);
        Assert.Equal(["update title,variants", "publish title=True variants=True"], printify.Calls);
        Assert.Empty(shopify.Calls);
        Assert.Equal(39m, printify.Product.Variants.Single(v => v.Id == "102").Price);
        Assert.False(printify.Product.Variants.Single(v => v.Id == "103").Enabled); // untracked variant left alone
        Assert.True(product.Channels.Printify!.PublishPending);

        // While Printify is publishing, Shopify still shows the old title: that's not an outside edit.
        Assert.Empty(SyncPlanner.Plan(catalog.All, [printify.Product], [shopify.Product]));
    }

    [Fact]
    public async Task Outside_edits_are_taken_over_and_conflicts_wait_for_you()
    {
        using var lib = new TempLibrary();
        var (catalog, product, printify, shopify) = PrintifySetup(lib);
        // Someone edits the description in Printify; meanwhile both sides change the title differently.
        printify.Product = printify.Product with { Description = "Honk honk", Title = "Goose poster (Printify)", UpdatedAt = "v9" };
        product.Name = "Goose poster (ArtGander)";
        catalog.Save(product);

        var plan = Assert.Single(SyncPlanner.Plan(catalog.All, [printify.Product], [shopify.Product]));
        var description = Assert.Single(plan.Changes, c => c.Field == "description");
        var title = Assert.Single(plan.Changes, c => c.Field == "title");
        Assert.Equal(SyncChangeKind.OutsideEdit, description.Kind);
        Assert.Equal(SyncChangeKind.Conflict, title.Kind);

        await new SyncRunner(catalog, printify, shopify).ApplyAsync(lib.Root, [plan], new Dictionary<string, SyncResolution>());

        Assert.Equal("Honk honk", product.Description);                 // taken over
        Assert.Equal("Goose poster (ArtGander)", product.Name);         // conflict kept, nothing sent
        Assert.Equal("Goose poster (Printify)", printify.Product.Title);
        Assert.Empty(printify.Calls);

        // The conflict comes back next time instead of being decided silently.
        var again = Assert.Single(SyncPlanner.Plan(catalog.All, [printify.Product], [shopify.Product]));
        Assert.Equal(SyncChangeKind.Conflict, Assert.Single(again.Changes).Kind);

        // Choosing ArtGander's title sends it.
        var choice = new Dictionary<string, SyncResolution> { [SyncRunner.ChoiceKey(product, again.Changes[0])] = SyncResolution.Push };
        await new SyncRunner(catalog, printify, shopify).ApplyAsync(lib.Root, [again], choice);
        Assert.Equal("Goose poster (ArtGander)", printify.Product.Title);
    }

    [Fact]
    public async Task A_product_changed_after_the_check_is_skipped()
    {
        using var lib = new TempLibrary();
        var (catalog, product, printify, shopify) = PrintifySetup(lib);
        product.Name = "New name";
        catalog.Save(product);
        var plan = Assert.Single(SyncPlanner.Plan(catalog.All, [printify.Product], [shopify.Product]));

        printify.Product = printify.Product with { UpdatedAt = "v-someone-else" }; // edited in Printify between check and apply

        var outcome = Assert.Single(await new SyncRunner(catalog, printify, shopify).ApplyAsync(lib.Root, [plan], new Dictionary<string, SyncResolution>()));
        Assert.False(outcome.Done);
        Assert.Contains("Changed in Printify", outcome.Message);
        Assert.Empty(printify.Calls);
    }

    [Fact]
    public async Task Self_made_products_go_straight_to_Shopify_and_sales_never_lose_stock()
    {
        using var lib = new TempLibrary();
        var shopify = new FakeShopify
        {
            Product = new RemoteProduct
            {
                Channel = "shopify", Id = "gid://shopify/Product/900", Title = "Hand-printed goose", Status = "active", UpdatedAt = "s1",
                Variants = [new RemoteVariant { Id = "gid://shopify/ProductVariant/9", Title = "Default Title", Price = 60m, Stock = 5, InventoryItemId = "gid://shopify/InventoryItem/9" }],
            },
        };
        var candidate = Assert.Single(ProductImporter.Plan([], [shopify.Product], [], []));
        var product = ProductImporter.Apply(candidate, [], "EUR");
        var catalog = new ProductCatalog();
        catalog.SetLoaded(lib.Root, []);
        catalog.Save(product);
        var variant = Assert.Single(product.Variants);

        // You printed 3 more (5 → 8) while a customer bought one in Shopify (5 → 4), and you raised the price.
        variant.Stock = 8;
        variant.Price = 65m;
        catalog.Save(product);
        shopify.Product = shopify.Product with { UpdatedAt = "s2", Variants = [shopify.Product.Variants[0] with { Stock = 4 }] };

        var plan = Assert.Single(SyncPlanner.Plan(catalog.All, [], [shopify.Product]));
        Assert.Equal(SyncChangeKind.Push, Assert.Single(plan.Changes, c => c.Field == "price").Kind);
        var stock = Assert.Single(plan.Changes, c => c.Field == "stock");
        Assert.Equal(SyncChangeKind.AutoTake, stock.Kind); // both changed: the shop's count wins by default…

        await new SyncRunner(catalog, null, shopify).ApplyAsync(lib.Root, [plan], new Dictionary<string, SyncResolution>());
        Assert.Equal(4, variant.Stock);                    // …so the sale is never overwritten
        Assert.Equal(65m, shopify.Product.Variants[0].Price);
        Assert.DoesNotContain(shopify.Calls, c => c.StartsWith("stock"));

        // Setting stock on purpose sends it with the count Shopify had (compare-and-set).
        variant.Stock = 7;
        catalog.Save(product);
        var plan2 = Assert.Single(SyncPlanner.Plan(catalog.All, [], [shopify.Product]));
        await new SyncRunner(catalog, null, shopify).ApplyAsync(lib.Root, [plan2], new Dictionary<string, SyncResolution>());
        Assert.Contains("stock gid://shopify/InventoryItem/9=7 if 4", shopify.Calls);
    }
}

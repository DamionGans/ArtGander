using System.Net;
using System.Text;
using ArtGander.Core.Integrations;
using ArtGander.Core.Model;
using ArtGander.Core.Services;
using ArtGander.Core.Storage;

namespace ArtGander.Core.Tests;

/// <summary>Answers HTTP requests from a script, and records them.</summary>
internal sealed class FakeHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(HttpMethod Method, string Url, string Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        Requests.Add((request.Method, request.RequestUri!.ToString(), body));
        return respond(request, body);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

public class IntegrationTests
{
    private static ApiHttp Http(FakeHandler handler) => new(new HttpClient(handler)) { Delay = (_, _) => Task.CompletedTask };

    private const string PrintifyProduct = """
        { "id": "pf1", "title": "Goose On The Loose poster", "description": "<p>Honk</p>", "tags": ["goose", "poster"],
          "visible": true, "is_locked": false, "updated_at": "2026-11-01 10:00:00+00:00", "blueprint_id": 282, "print_provider_id": 99,
          "external": { "id": "777", "handle": "goose-poster" },
          "variants": [
            { "id": 101, "title": "A3", "sku": "GP-A3", "price": 2500, "cost": 1100, "is_enabled": true },
            { "id": 102, "title": "A2", "sku": "GP-A2", "price": 3500, "cost": 1500, "is_enabled": true },
            { "id": 103, "title": "A1", "sku": "GP-A1", "price": 4500, "cost": 2000, "is_enabled": false } ] }
        """;

    private static RemoteProduct ShopifyPoster(string updated = "2026-11-01T10:00:00Z", string title = "Goose On The Loose poster") => new()
    {
        Channel = "shopify", Id = "gid://shopify/Product/777", Title = title, Description = "<p>Honk</p>", Tags = ["goose", "poster"],
        Status = "active", UpdatedAt = updated, Handle = "goose-poster",
        Variants =
        [
            new RemoteVariant { Id = "gid://shopify/ProductVariant/5001", Title = "A3", Sku = "GP-A3", Price = 25m },
            new RemoteVariant { Id = "gid://shopify/ProductVariant/5002", Title = "A2", Sku = "GP-A2", Price = 35m },
        ],
    };

    [Fact]
    public async Task Printify_products_are_read_across_pages_in_cents()
    {
        var handler = new FakeHandler((req, _) => FakeHandler.Json(req.RequestUri!.Query.Contains("page=1")
            ? $$"""{ "current_page": 1, "last_page": 2, "data": [ {{PrintifyProduct}} ] }"""
            : """{ "current_page": 2, "last_page": 2, "data": [ { "id": "pf2", "title": "Mug", "variants": [] } ] }"""));
        var client = new PrintifyClient(Http(handler), "token");

        var products = await client.GetProductsAsync(42);

        Assert.Equal(2, products.Count);
        var poster = products[0];
        Assert.Equal("777", poster.ExternalId);
        Assert.Equal(25m, poster.Variants[0].Price);
        Assert.Equal(11m, poster.Variants[0].Cost);
        Assert.Equal(2, poster.EnabledVariants.Count());
        Assert.All(handler.Requests, r => Assert.StartsWith("https://api.printify.com/v1/shops/42/products.json", r.Url));
    }

    [Fact]
    public async Task Rate_limited_requests_are_retried()
    {
        var calls = 0;
        var handler = new FakeHandler((_, _) =>
        {
            if (++calls < 3)
            {
                var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
                return r;
            }
            return FakeHandler.Json("""[ { "id": 42, "title": "Goose shop", "sales_channel": "shopify" } ]""");
        });

        var shops = await new PrintifyClient(Http(handler), "token").GetShopsAsync();

        Assert.Equal(3, calls);
        Assert.Equal("shopify", Assert.Single(shops).SalesChannel);
    }

    [Fact]
    public async Task Shopify_token_is_fetched_once_and_reused()
    {
        var handler = new FakeHandler((req, body) =>
            req.RequestUri!.AbsolutePath.EndsWith("access_token")
                ? FakeHandler.Json("""{ "access_token": "shpat_test", "scope": "read_products", "expires_in": 86399 }""")
                : FakeHandler.Json("""{ "data": { "shop": { "name": "Goose Shop", "currencyCode": "EUR" } } }"""));
        var auth = new ShopifyAuth(new HttpClient(handler), "goose-shop", "client", "secret");
        var client = new ShopifyClient(Http(handler), auth);

        Assert.Equal("Goose Shop", await client.GetShopNameAsync());
        Assert.Equal("Goose Shop", await client.GetShopNameAsync());

        Assert.Single(handler.Requests, r => r.Url.EndsWith("/admin/oauth/access_token"));
        Assert.Contains("grant_type=client_credentials", handler.Requests[0].Body);
        Assert.Equal("goose-shop.myshopify.com", auth.ShopDomain);
        Assert.Contains("/admin/api/" + ShopifyClient.DefaultApiVersion + "/graphql.json", handler.Requests[1].Url);
    }

    [Theory]
    [InlineData("goose-shop", "goose-shop.myshopify.com")]
    [InlineData("Goose-Shop.myshopify.com", "goose-shop.myshopify.com")]
    [InlineData("https://goose-shop.myshopify.com/admin/products", "goose-shop.myshopify.com")]
    [InlineData("https://admin.shopify.com/store/goose-shop/products?x=1", "goose-shop.myshopify.com")]
    [InlineData("admin.shopify.com/store/goose-shop", "goose-shop.myshopify.com")]
    [InlineData("www.mygooseshop.com", "www.mygooseshop.com")]
    public void Store_addresses_are_understood(string input, string expected)
    {
        Assert.Equal(expected, ShopifyAuth.NormalizeDomain(input));
    }

    [Fact]
    public async Task A_web_page_instead_of_data_explains_the_store_address()
    {
        var handler = new FakeHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<!DOCTYPE html><html><body>Goose Shop</body></html>", Encoding.UTF8, "text/html"),
        });
        var auth = new ShopifyAuth(new HttpClient(handler), "www.mygooseshop.com", "client", "secret");
        var client = new ShopifyClient(Http(handler), auth);

        var ex = await Assert.ThrowsAsync<ShopifyException>(() => client.GetShopNameAsync());
        Assert.Contains("web page", ex.Message);
        Assert.False(ShopifyAuth.IsMyShopifyDomain(auth.ShopDomain));
    }

    [Fact]
    public async Task A_store_that_does_not_exist_is_explained()
    {
        var handler = new FakeHandler((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("<!DOCTYPE html><html><head><title>Store unavailable</title></head></html>", Encoding.UTF8, "text/html"),
        });
        var auth = new ShopifyAuth(new HttpClient(handler), "hopes-art", "client", "secret");
        var client = new ShopifyClient(Http(handler), auth);

        var ex = await Assert.ThrowsAsync<ShopifyException>(() => client.GetShopNameAsync());
        Assert.Contains("no Shopify store at \"hopes-art.myshopify.com\"", ex.Message);
        Assert.Contains("Settings → Domains", ex.Message);
    }

    [Fact]
    public async Task Login_errors_from_Shopify_are_explained()
    {
        var handler = new FakeHandler((_, _) => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("<!DOCTYPE html><html><head><title>400 - Oauth error application_cannot_be_found</title></head></html>", Encoding.UTF8, "text/html"),
        });
        var client = new ShopifyClient(Http(handler), new ShopifyAuth(new HttpClient(handler), "goose-shop.myshopify.com", "wrong", "secret"));

        var ex = await Assert.ThrowsAsync<ShopifyException>(() => client.GetShopNameAsync());
        Assert.Contains("doesn't know this Client ID", ex.Message);
    }

    [Fact]
    public void Printify_and_Shopify_products_become_one_product_linked_to_its_piece()
    {
        using var lib = new TempLibrary();
        var piece = new PieceService(new ArtPieceStore()).CreatePiece(lib.Root, "Goose On The Loose");
        var printify = PrintifyClient.ParseProduct(System.Text.Json.Nodes.JsonNode.Parse(PrintifyProduct), 42);
        var shopify = ShopifyPoster();
        var lonely = new RemoteProduct { Channel = "shopify", Id = "gid://shopify/Product/900", Title = "Original painting",
            Variants = [new RemoteVariant { Id = "gid://shopify/ProductVariant/9", Title = "Default Title", Price = 400, Stock = 1 }] };

        var plan = ProductImporter.Plan([printify], [shopify, lonely], [], [piece]);

        Assert.Equal(2, plan.Count);
        var poster = plan.Single(c => c.Printify is not null);
        Assert.Same(shopify, poster.Shopify);
        Assert.Equal([piece.Piece.Id], poster.SuggestedPieces);

        var product = ProductImporter.Apply(poster, poster.SuggestedPieces, "EUR");
        Assert.Equal(ProductType.PrintOnDemand, product.Type);
        Assert.Equal(Fulfillment.Printify, product.Fulfillment);
        Assert.Equal(2, product.Variants.Count); // only enabled variants
        Assert.Equal(25m, product.Price);
        Assert.Equal("gid://shopify/ProductVariant/5002", product.Variants.Single(v => v.Sku == "GP-A2").ShopifyVariantId);
        Assert.Equal(102, product.Variants.Single(v => v.Sku == "GP-A2").PrintifyVariantId);
        Assert.Equal("pf1", product.Channels.Printify!.ProductId);
        Assert.Equal("2026-11-01 10:00:00+00:00", product.Channels.Printify.RemoteUpdatedAt);
        Assert.Equal(2, product.Channels.Shopify!.Base!.Variants.Count);

        var original = ProductImporter.Apply(plan.Single(c => c.Printify is null), [], "EUR");
        Assert.Equal(Fulfillment.Self, original.Fulfillment);
        Assert.Equal(1, original.Stock);

        // Importing again finds the existing product instead of creating a second one.
        var again = ProductImporter.Plan([printify], [shopify], [product], [piece]);
        Assert.True(Assert.Single(again).IsUpToDate);
    }

    [Fact]
    public void Same_products_are_combined_even_without_a_Printify_link()
    {
        RemoteProduct P(string id, string title, string sku) => new()
        {
            Channel = "printify", Id = id, Title = title, Variants = [new RemoteVariant { Id = id + "1", Sku = sku, Price = 20, Enabled = true }],
        };
        RemoteProduct S(string id, string title, string sku) => new()
        {
            Channel = "shopify", Id = "gid://shopify/Product/" + id, Title = title, Variants = [new RemoteVariant { Id = "gid://shopify/ProductVariant/" + id, Sku = sku, Price = 20 }],
        };
        var printify = new[] { P("a", "Goose mug", "MUG-1"), P("b", "Swan Tote Bag", "TOTE-9"), P("c", "Duck sticker", "ST-1") };
        var shopify = new[] { S("1", "Big goose mug 11oz", "MUG-1"), S("2", "swan tote bag!", "OTHER"), S("3", "Hand-painted goose", "") };
        var handMade = new Product { Name = "Hand painted goose" }; // entered in ArtGander before

        var plan = ProductImporter.Plan(printify, shopify, [handMade], []);

        Assert.Equal(4, plan.Count);
        Assert.Equal("gid://shopify/Product/1", plan.Single(c => c.Printify?.Id == "a").Shopify?.Id); // same SKU
        Assert.Equal("gid://shopify/Product/2", plan.Single(c => c.Printify?.Id == "b").Shopify?.Id); // same title
        Assert.Null(plan.Single(c => c.Printify?.Id == "c").Shopify);
        var joined = plan.Single(c => c.Shopify?.Id == "gid://shopify/Product/3");
        Assert.Same(handMade, joined.Existing);
        Assert.Contains("same name", joined.MatchReason);
    }

    [Fact]
    public void The_first_sync_after_joining_an_existing_product_asks_instead_of_sending()
    {
        var mine = new Product { Name = "Goose poster", Price = 22m };
        var shop = new RemoteProduct
        {
            Channel = "shopify", Id = "gid://shopify/Product/5", Title = "Goose poster", Description = "From the shop", Status = "active", UpdatedAt = "s1",
            Variants = [new RemoteVariant { Id = "gid://shopify/ProductVariant/5", Title = "Default Title", Price = 25m }],
        };
        var candidate = Assert.Single(ProductImporter.Plan([], [shop], [mine], []));
        ProductImporter.Apply(candidate, [], "EUR");
        Assert.True(mine.Channels.Shopify!.NeedsReview);

        var plan = Assert.Single(SyncPlanner.Plan([mine], [], [shop]));
        Assert.NotEmpty(plan.Changes);
        Assert.All(plan.Changes, c => Assert.Equal(SyncChangeKind.Conflict, c.Kind)); // nothing is sent unless you choose
    }

    private static RemoteOrder ShopifyOrder(string updated = "2026-11-02T10:00:00Z") => new()
    {
        Channel = "shopify", Id = "gid://shopify/Order/555", Name = "#1001", CreatedAt = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero),
        UpdatedAt = updated, Currency = "EUR", Customer = "Honk B.", Paid = true, TotalPaid = 30m, TotalTax = 5m,
        Lines = [new RemoteOrderLine { Id = "gid://shopify/LineItem/1", Title = "Goose poster - A3", Sku = "GP-A3", Quantity = 1, UnitPrice = 25m,
            ProductId = "gid://shopify/Product/777", VariantId = "gid://shopify/ProductVariant/5001" }],
    };

    private static RemoteOrder PrintifyOrder(string tracking = "") => new()
    {
        Channel = "printify", Id = "pfo1", ShopOrderId = "555", ShopOrderLabel = "#1001", CreatedAt = new DateTimeOffset(2026, 11, 2, 9, 5, 0, TimeSpan.Zero),
        UpdatedAt = tracking, ProductionCost = 11m, ProductionTax = 2.2m, ShippingCost = 4m,
        Carrier = tracking.Length > 0 ? "USPS" : "", TrackingCode = tracking, Fulfilled = tracking.Length > 0,
        Lines = [new RemoteOrderLine { Id = "pl1", ProductId = "pf1", VariantId = "101", Quantity = 1, Sku = "GP-A3" }],
    };

    [Fact]
    public void Shopify_and_Printify_orders_are_combined_into_one()
    {
        var poster = new Product { Name = "Goose poster", Type = ProductType.PrintOnDemand };
        poster.Variants.Add(new ProductVariant { Key = "101", Sku = "GP-A3", PrintifyVariantId = 101, ShopifyVariantId = "gid://shopify/ProductVariant/5001" });
        poster.Channels.Shopify = new ShopifyLink { ProductId = "gid://shopify/Product/777" };
        poster.Channels.Printify = new PrintifyLink { ProductId = "pf1" };

        var plan = OrderImporter.Plan([ShopifyOrder()], [PrintifyOrder()], [], [poster], "EUR");

        var item = Assert.Single(plan);
        Assert.True(item.IsNew);
        OrderImporter.Apply(item);
        var order = item.Order;
        Assert.Equal("#1001", order.OrderRef);
        Assert.Equal(30m, order.AmountPaid);
        Assert.Equal(5m, order.AmountPaidTax);
        Assert.Equal(11m, order.ProductionCost);
        Assert.Equal(4m, order.ShippingCost);
        var line = Assert.Single(order.Lines);
        Assert.Equal(poster.Id, line.ProductId);
        Assert.Equal("101", line.VariantKey);
        Assert.Equal(Fulfillment.Printify, line.Fulfillment);
        Assert.Equal("pfo1", order.Sources.Printify!.Id);
    }

    [Fact]
    public void Reimporting_keeps_your_edits_and_reports_real_conflicts()
    {
        var first = Assert.Single(OrderImporter.Plan([ShopifyOrder()], [PrintifyOrder()], [], [], "EUR"));
        OrderImporter.Apply(first);
        var order = first.Order;

        // You correct the customer name and add a note; meanwhile Printify ships the order.
        order.Customer = "Honk Bird";
        order.Notes = "gift wrap";

        var second = Assert.Single(OrderImporter.Plan([ShopifyOrder("2026-11-05T10:00:00Z")], [PrintifyOrder("9400 1")], [order], [], "EUR"));
        Assert.False(second.IsNew);
        Assert.DoesNotContain(second.Changes, c => c.Field == "customer"); // Shopify didn't change it: yours stays, no question
        Assert.Contains(second.Changes, c => c.Field == "trackingCode" && !c.IsConflict);
        Assert.Contains(second.Changes, c => c.Field == "status" && !c.IsConflict);
        Assert.Empty(second.NewLines); // the line was imported before

        OrderImporter.Apply(second);
        Assert.Equal("Honk Bird", second.Order.Customer);
        Assert.Equal("gift wrap", second.Order.Notes);
        Assert.Equal("9400 1", second.Order.TrackingCode);
        Assert.Equal(OrderStatus.Shipped, second.Order.Status);

        // Now the shop changes the amount too, while you had corrected it: that's a conflict, yours is kept unless chosen.
        second.Order.AmountPaid = 29m;
        var shopifyChanged = ShopifyOrder("2026-11-06T10:00:00Z") with { TotalPaid = 31m };
        var third = Assert.Single(OrderImporter.Plan([shopifyChanged], [PrintifyOrder("9400 1")], [second.Order], [], "EUR"));
        var conflict = Assert.Single(third.Changes, c => c.Field == "amountPaid");
        Assert.True(conflict.IsConflict);
        OrderImporter.Apply(third);
        Assert.Equal(29m, third.Order.AmountPaid);
    }

    [Fact]
    public void Imported_order_joins_an_order_entered_by_hand()
    {
        var manual = new Order { OrderRef = "#1001", Carrier = "PostNL" };
        var item = Assert.Single(OrderImporter.Plan([ShopifyOrder()], [], [manual], [], "EUR"));
        Assert.False(item.IsNew);
        Assert.Equal(manual.Id, item.Order.Id);
        Assert.DoesNotContain(item.Changes, c => c.Field == "carrier");
    }
}

using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace ArtGander.Core.Integrations;

/// <summary>
/// Gets Shopify Admin API tokens with the client credentials grant (an app from the Shopify Dev Dashboard,
/// installed on your own store). Tokens last 24 hours; a new one is fetched automatically.
/// </summary>
public sealed class ShopifyAuth(HttpClient http, string shopDomain, string clientId, string clientSecret)
{
    private string? _token;
    private DateTimeOffset _expires;
    private readonly SemaphoreSlim _gate = new(1);

    public string ShopDomain { get; } = NormalizeDomain(shopDomain);

    /// <summary>"my-shop", "my-shop.myshopify.com" or "https://my-shop.myshopify.com/admin" → "my-shop.myshopify.com".</summary>
    public static string NormalizeDomain(string input)
    {
        var s = input.Trim().ToLowerInvariant();
        if (s.Contains("://", StringComparison.Ordinal)) s = new Uri(s).Host;
        s = s.Split('/')[0];
        return s.Length == 0 || s.Contains('.') ? s : s + ".myshopify.com";
    }

    public async Task<string> GetTokenAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_token is not null && DateTimeOffset.UtcNow < _expires) return _token;
            using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{ShopDomain}/admin/oauth/access_token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = clientId,
                    ["client_secret"] = clientSecret,
                }),
            };
            request.Headers.UserAgent.ParseAdd("ArtGander");
            using var response = await http.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new ApiException(response.StatusCode, ShopDomain, text.Length > 300 ? text[..300] : text);
            var node = JsonNode.Parse(text);
            _token = node.Str("access_token");
            var seconds = node.Long("expires_in") ?? 86399;
            _expires = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, seconds - 300)); // renew 5 minutes early
            return _token;
        }
        finally { _gate.Release(); }
    }
}

/// <summary>Shopify Admin GraphQL API.</summary>
public sealed class ShopifyClient(ApiHttp http, ShopifyAuth auth, string apiVersion = ShopifyClient.DefaultApiVersion) : IShopifyApi
{
    /// <summary>A stable Admin API version. Shopify supports each version for at least 12 months; it can be changed in Settings.</summary>
    public const string DefaultApiVersion = "2026-10";

    public string ShopDomain => auth.ShopDomain;

    public async Task<JsonNode> GraphQLAsync(string query, JsonObject? variables = null, CancellationToken ct = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            var token = await auth.GetTokenAsync(ct);
            var node = await http.SendJsonAsync(HttpMethod.Post, $"https://{auth.ShopDomain}/admin/api/{apiVersion}/graphql.json",
                new JsonObject { ["query"] = query, ["variables"] = variables ?? [] },
                h => h.Add("X-Shopify-Access-Token", token), ct) ?? new JsonObject();

            if (node["errors"] is JsonArray errors && errors.Count > 0)
            {
                var throttled = errors.Any(e => e?["extensions"].Str("code") == "THROTTLED");
                if (throttled && attempt < 6)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(20, 2 * attempt)), ct);
                    continue;
                }
                throw new ShopifyException(string.Join("; ", errors.Select(e => e.Str("message"))));
            }
            // Slow down before the cost bucket runs dry.
            var available = node["extensions"]?["cost"]?["throttleStatus"].Dec("currentlyAvailable");
            if (available is < 100) await Task.Delay(TimeSpan.FromSeconds(2), ct);
            return node["data"] ?? new JsonObject();
        }
    }

    public async Task<string> GetShopNameAsync(CancellationToken ct = default) =>
        (await GraphQLAsync("{ shop { name currencyCode } }", ct: ct))["shop"].Str("name");

    private const string ProductFields = """
        id handle title descriptionHtml tags status updatedAt onlineStoreUrl
        featuredMedia { preview { image { url } } }
        variants(first: 100) { nodes { id title sku price inventoryQuantity inventoryItem { id } } }
        """;

    public async Task<IReadOnlyList<RemoteProduct>> GetProductsAsync(CancellationToken ct = default)
    {
        var all = new List<RemoteProduct>();
        string? cursor = null;
        do
        {
            var data = await GraphQLAsync($$"""
                query($after: String) { products(first: 50, after: $after) {
                  nodes { {{ProductFields}} }
                  pageInfo { hasNextPage endCursor } } }
                """, new JsonObject { ["after"] = cursor }, ct);
            var products = data["products"];
            all.AddRange(products.Arr("nodes").Select(ParseProduct));
            cursor = products?["pageInfo"].Bool("hasNextPage") == true ? products["pageInfo"].Str("endCursor") : null;
        } while (cursor is not null);
        return all;
    }

    public async Task<RemoteProduct?> GetProductAsync(string id, CancellationToken ct = default)
    {
        var data = await GraphQLAsync($$"""query($id: ID!) { product(id: $id) { {{ProductFields}} } }""",
            new JsonObject { ["id"] = Ids.ShopifyProductGid(id) }, ct);
        return data["product"] is JsonObject p ? ParseProduct(p) : null;
    }

    /// <summary>Updates only the given fields of a product. Variant prices are set per variant.</summary>
    public async Task UpdateProductAsync(string productId, string? title, string? descriptionHtml, IReadOnlyList<string>? tags, string? status,
        IReadOnlyList<RemoteVariant>? variantPrices, CancellationToken ct = default)
    {
        var gid = Ids.ShopifyProductGid(productId);
        var input = new JsonObject { ["id"] = gid };
        if (title is not null) input["title"] = title;
        if (descriptionHtml is not null) input["descriptionHtml"] = descriptionHtml;
        if (tags is not null) input["tags"] = new JsonArray(tags.Select(t => (JsonNode)t).ToArray());
        if (status is not null) input["status"] = status.ToUpperInvariant();
        if (input.Count > 1)
        {
            var data = await GraphQLAsync("""
                mutation($product: ProductUpdateInput!) { productUpdate(product: $product) { product { id } userErrors { field message } } }
                """, new JsonObject { ["product"] = input }, ct);
            ThrowOnUserErrors(data["productUpdate"]);
        }
        if (variantPrices is { Count: > 0 })
        {
            var data = await GraphQLAsync("""
                mutation($productId: ID!, $variants: [ProductVariantsBulkInput!]!) {
                  productVariantsBulkUpdate(productId: $productId, variants: $variants) { userErrors { field message } } }
                """, new JsonObject
            {
                ["productId"] = gid,
                ["variants"] = new JsonArray(variantPrices.Select(v => (JsonNode)new JsonObject
                {
                    ["id"] = v.Id,
                    ["price"] = (v.Price ?? 0).ToString("0.00", CultureInfo.InvariantCulture),
                }).ToArray()),
            }, ct);
            ThrowOnUserErrors(data["productVariantsBulkUpdate"]);
        }
    }

    /// <summary>Creates a product you make yourselves (as a draft), with its variants and prices. Returns its id.</summary>
    public async Task<string> CreateProductAsync(string title, string descriptionHtml, IReadOnlyList<string> tags,
        IReadOnlyList<(string Title, string Sku, decimal Price)> variants, CancellationToken ct = default)
    {
        var options = variants.Count > 1
            ? new JsonArray(new JsonObject
            {
                ["name"] = "Variant",
                ["values"] = new JsonArray(variants.Select(v => (JsonNode)new JsonObject { ["name"] = v.Title }).ToArray()),
            })
            : null;
        var input = new JsonObject
        {
            ["title"] = title,
            ["descriptionHtml"] = descriptionHtml,
            ["tags"] = new JsonArray(tags.Select(t => (JsonNode)t).ToArray()),
            ["status"] = "DRAFT",
        };
        if (options is not null) input["productOptions"] = options;
        var data = await GraphQLAsync("""
            mutation($product: ProductCreateInput!) { productCreate(product: $product) {
              product { id variants(first: 100) { nodes { id title } } } userErrors { field message } } }
            """, new JsonObject { ["product"] = input }, ct);
        ThrowOnUserErrors(data["productCreate"]);
        var product = data["productCreate"]?["product"];
        var id = product.Str("id");

        // Set prices and SKUs on the variants Shopify created (one per option value, or the single default variant).
        var created = product?["variants"].Arr("nodes").ToList() ?? [];
        var bulk = new JsonArray();
        foreach (var node in created)
        {
            var match = variants.Count == 1 ? variants[0] : variants.FirstOrDefault(v => v.Title == node.Str("title"));
            if (match == default) continue;
            bulk.Add((JsonNode)new JsonObject
            {
                ["id"] = node.Str("id"),
                ["price"] = match.Price.ToString("0.00", CultureInfo.InvariantCulture),
                ["inventoryItem"] = new JsonObject { ["sku"] = match.Sku, ["tracked"] = true },
            });
        }
        if (bulk.Count > 0)
        {
            var result = await GraphQLAsync("""
                mutation($productId: ID!, $variants: [ProductVariantsBulkInput!]!) {
                  productVariantsBulkUpdate(productId: $productId, variants: $variants) { userErrors { field message } } }
                """, new JsonObject { ["productId"] = id, ["variants"] = bulk }, ct);
            ThrowOnUserErrors(result["productVariantsBulkUpdate"]);
        }
        return id;
    }

    /// <summary>The store's first location, used for stock of products you make yourselves.</summary>
    public async Task<string?> GetPrimaryLocationIdAsync(CancellationToken ct = default) =>
        (await GraphQLAsync("{ locations(first: 1) { nodes { id } } }", ct: ct))["locations"].Arr("nodes").FirstOrDefault().Str("id") is { Length: > 0 } id ? id : null;

    /// <summary>
    /// Sets stock levels, but only where Shopify still has the quantity we last saw (compare-and-set),
    /// so a sale that happened in the meantime is never overwritten.
    /// </summary>
    public async Task SetStockAsync(string locationId, IReadOnlyList<(string InventoryItemId, int Quantity, int? ExpectedCurrent)> items, CancellationToken ct = default)
    {
        if (items.Count == 0) return;
        var data = await GraphQLAsync("""
            mutation($input: InventorySetQuantitiesInput!) { inventorySetQuantities(input: $input) { userErrors { field message code } } }
            """, new JsonObject
        {
            ["input"] = new JsonObject
            {
                ["name"] = "available",
                ["reason"] = "correction",
                ["quantities"] = new JsonArray(items.Select(i =>
                {
                    var q = new JsonObject { ["inventoryItemId"] = i.InventoryItemId, ["locationId"] = locationId, ["quantity"] = i.Quantity };
                    if (i.ExpectedCurrent is { } expected) q["changeFromQuantity"] = expected;
                    return (JsonNode)q;
                }).ToArray()),
            },
        }, ct);
        ThrowOnUserErrors(data["inventorySetQuantities"]);
    }

    /// <summary>Orders updated since <paramref name="since"/> (all orders when null), oldest first.</summary>
    public async Task<IReadOnlyList<RemoteOrder>> GetOrdersAsync(DateTimeOffset? since, CancellationToken ct = default)
    {
        var all = new List<RemoteOrder>();
        string? cursor = null;
        var filter = since is { } s ? $"updated_at:>='{s.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}'" : null;
        var withCustomer = true;
        do
        {
            JsonNode data;
            try
            {
                data = await GraphQLAsync(OrdersQuery(withCustomer), new JsonObject { ["after"] = cursor, ["query"] = filter }, ct);
            }
            catch (ShopifyException ex) when (withCustomer && ex.Message.Contains("customer", StringComparison.OrdinalIgnoreCase))
            {
                // Without access to protected customer data, import the orders without customer names.
                withCustomer = false;
                continue;
            }
            var orders = data["orders"];
            all.AddRange(orders.Arr("nodes").Select(ParseOrder));
            cursor = orders?["pageInfo"].Bool("hasNextPage") == true ? orders["pageInfo"].Str("endCursor") : null;
        } while (cursor is not null);
        return all;
    }

    private static string OrdersQuery(bool withCustomer) => $$"""
        query($after: String, $query: String) { orders(first: 50, after: $after, query: $query, sortKey: UPDATED_AT) {
          nodes {
            id name createdAt updatedAt cancelledAt currencyCode displayFinancialStatus displayFulfillmentStatus
            {{(withCustomer ? "customer { displayName }" : "")}}
            totalPriceSet { shopMoney { amount } }
            totalTaxSet { shopMoney { amount } }
            fulfillments(first: 5) { createdAt displayStatus trackingInfo(first: 1) { company number } }
            lineItems(first: 100) { nodes { id title sku quantity
              originalUnitPriceSet { shopMoney { amount } }
              product { id } variant { id } } }
          }
          pageInfo { hasNextPage endCursor } } }
        """;

    public static RemoteProduct ParseProduct(JsonNode p) => new()
    {
        Channel = "shopify",
        Id = p.Str("id"),
        Handle = p.Str("handle"),
        Title = p.Str("title"),
        Description = p.Str("descriptionHtml"),
        Tags = p.Arr("tags").Select(t => t.GetValue<string>()).ToList(),
        Status = p.Str("status").ToLowerInvariant(),
        UpdatedAt = p.Str("updatedAt"),
        Url = p.Str("onlineStoreUrl"),
        ImageUrl = p["featuredMedia"]?["preview"]?["image"].Str("url") is { Length: > 0 } u ? u : null,
        Variants = p["variants"].Arr("nodes").Select(v => new RemoteVariant
        {
            Id = v.Str("id"),
            Title = v.Str("title"),
            Sku = v.Str("sku"),
            Price = v.Dec("price"),
            Stock = v.Long("inventoryQuantity") is { } q ? (int)q : null,
            InventoryItemId = v["inventoryItem"].Str("id") is { Length: > 0 } i ? i : null,
        }).ToList(),
    };

    public static RemoteOrder ParseOrder(JsonNode o)
    {
        var financial = o.Str("displayFinancialStatus");
        var fulfillment = o.Str("displayFulfillmentStatus");
        var shipment = o.Arr("fulfillments").FirstOrDefault();
        var tracking = shipment?["trackingInfo"] is JsonArray t ? t.FirstOrDefault() : null;
        return new RemoteOrder
        {
            Channel = "shopify",
            Id = o.Str("id"),
            Name = o.Str("name"),
            CreatedAt = PrintifyClient.ParseDate(o.Str("createdAt")) ?? DateTimeOffset.MinValue,
            UpdatedAt = o.Str("updatedAt"),
            Currency = o.Str("currencyCode"),
            Customer = o["customer"].Str("displayName"),
            Cancelled = o.Str("cancelledAt").Length > 0,
            Refunded = financial is "REFUNDED",
            Paid = financial is "PAID" or "PARTIALLY_REFUNDED" or "REFUNDED",
            Fulfilled = fulfillment is "FULFILLED",
            Delivered = shipment.Str("displayStatus") == "DELIVERED",
            TotalPaid = o["totalPriceSet"]?["shopMoney"].Dec("amount"),
            TotalTax = o["totalTaxSet"]?["shopMoney"].Dec("amount"),
            Carrier = tracking.Str("company"),
            TrackingCode = tracking.Str("number"),
            ShippedOn = PrintifyClient.ParseDate(shipment.Str("createdAt")) is { } d ? DateOnly.FromDateTime(d.LocalDateTime) : null,
            Lines = o["lineItems"].Arr("nodes").Select(l => new RemoteOrderLine
            {
                Id = l.Str("id"),
                Title = l.Str("title"),
                Sku = l.Str("sku"),
                Quantity = (int)(l.Long("quantity") ?? 1),
                UnitPrice = l["originalUnitPriceSet"]?["shopMoney"].Dec("amount"),
                ProductId = l["product"].Str("id") is { Length: > 0 } pid ? pid : null,
                VariantId = l["variant"].Str("id") is { Length: > 0 } vid ? vid : null,
            }).ToList(),
        };
    }

    /// <summary>Marks the self-shipped items of an order as shipped in Shopify, with tracking (Shopify emails the customer).</summary>
    public async Task FulfillAsync(string orderId, IReadOnlyList<string> lineItemIds, string carrier, string trackingNumber, CancellationToken ct = default)
    {
        var data = await GraphQLAsync("""
            query($id: ID!) { order(id: $id) { fulfillmentOrders(first: 10) { nodes { id status lineItems(first: 100) { nodes { id remainingQuantity lineItem { id } } } } } } }
            """, new JsonObject { ["id"] = orderId }, ct);
        var groups = new JsonArray();
        foreach (var fo in data["order"]?["fulfillmentOrders"].Arr("nodes") ?? [])
        {
            if (fo.Str("status") is "CLOSED" or "CANCELLED") continue;
            var items = new JsonArray();
            foreach (var li in fo["lineItems"].Arr("nodes"))
            {
                if (!lineItemIds.Contains(li["lineItem"].Str("id")) || (li.Long("remainingQuantity") ?? 0) <= 0) continue;
                items.Add((JsonNode)new JsonObject { ["id"] = li.Str("id"), ["quantity"] = li.Long("remainingQuantity") });
            }
            if (items.Count > 0) groups.Add((JsonNode)new JsonObject { ["fulfillmentOrderId"] = fo.Str("id"), ["fulfillmentOrderLineItems"] = items });
        }
        if (groups.Count == 0) throw new ShopifyException("Nothing left to ship in Shopify for these items.");
        var result = await GraphQLAsync("""
            mutation($f: FulfillmentInput!) { fulfillmentCreate(fulfillment: $f) { fulfillment { id } userErrors { field message } } }
            """, new JsonObject
        {
            ["f"] = new JsonObject
            {
                ["lineItemsByFulfillmentOrder"] = groups,
                ["notifyCustomer"] = true,
                ["trackingInfo"] = new JsonObject { ["company"] = carrier, ["number"] = trackingNumber },
            },
        }, ct);
        ThrowOnUserErrors(result["fulfillmentCreate"]);
    }

    private static void ThrowOnUserErrors(JsonNode? payload)
    {
        var errors = payload.Arr("userErrors").Select(e => e.Str("message")).Where(m => m.Length > 0).ToList();
        if (errors.Count > 0) throw new ShopifyException(string.Join("; ", errors));
    }
}

public sealed class ShopifyException(string message) : Exception("Shopify: " + message);

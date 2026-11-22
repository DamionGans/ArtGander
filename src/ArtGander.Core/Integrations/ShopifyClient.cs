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

    /// <summary>
    /// "my-shop", "my-shop.myshopify.com", "https://my-shop.myshopify.com/admin" or the admin address
    /// "https://admin.shopify.com/store/my-shop" → "my-shop.myshopify.com".
    /// </summary>
    public static string NormalizeDomain(string input)
    {
        var s = input.Trim().ToLowerInvariant();
        if (s.Contains("://", StringComparison.Ordinal)) s = s[(s.IndexOf("://", StringComparison.Ordinal) + 3)..];
        var parts = s.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "";
        // The Shopify admin address names the store after /store/.
        if (parts[0] == "admin.shopify.com" && parts.Length >= 3 && parts[1] == "store") return parts[2] + ".myshopify.com";
        var host = parts[0];
        return host.Contains('.') ? host : host + ".myshopify.com";
    }

    /// <summary>The API only works on the store's own ….myshopify.com address, not on a custom domain.</summary>
    public static bool IsMyShopifyDomain(string domain) => domain.EndsWith(".myshopify.com", StringComparison.OrdinalIgnoreCase);

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
            if (LooksLikeHtml(text)) throw new ShopifyException(HtmlAnswer(ShopDomain, response.StatusCode, text, response.RequestMessage?.RequestUri));
            if (!response.IsSuccessStatusCode)
                throw new ShopifyException(TokenError(response.StatusCode, text));
            var node = JsonNode.Parse(text);
            _token = node.Str("access_token");
            var seconds = node.Long("expires_in") ?? 86399;
            _expires = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, seconds - 300)); // renew 5 minutes early
            return _token;
        }
        finally { _gate.Release(); }
    }

    internal static bool LooksLikeHtml(string text) => text.TrimStart().StartsWith('<');

    /// <summary>Explains a web page where data was expected: usually there is no store at this address.</summary>
    internal static string HtmlAnswer(string domain, System.Net.HttpStatusCode status, string html, Uri? finalUrl)
    {
        var title = System.Text.RegularExpressions.Regex.Match(html, @"<title[^>]*>\s*(.*?)\s*</title>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline).Groups[1].Value;
        // Login problems come back as a web page titled e.g. "400 - Oauth error application_cannot_be_found".
        var oauth = System.Text.RegularExpressions.Regex.Match(title, @"oauth error\s+(\w+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (oauth.Success) return OAuthAdvice(oauth.Groups[1].Value.ToLowerInvariant(), domain);
        if (status == System.Net.HttpStatusCode.NotFound || title.Contains("unavailable", StringComparison.OrdinalIgnoreCase))
            return $"there is no Shopify store at \"{domain}\". Shopify often gives a store a generated address (like a1b2c3-4d.myshopify.com) "
                   + "that differs from the shop's name. Find it in Shopify admin → Settings → Domains, or copy the address of your Shopify admin "
                   + "(admin.shopify.com/store/…) from the browser and paste it here.";
        var moved = finalUrl is not null && !finalUrl.Host.Equals(domain, StringComparison.OrdinalIgnoreCase) ? $", after being sent on to {finalUrl.Host}" : "";
        return $"Shopify answered with a web page (\"{title}\", {(int)status}{moved}) instead of data. Check the store address, and that the ArtGander app is installed on this store.";
    }

    /// <summary>What to do about a Shopify login (OAuth) error.</summary>
    internal static string OAuthAdvice(string code, string domain) => code switch
    {
        "application_cannot_be_found" =>
            "Shopify doesn't know this Client ID. Copy the Client ID from your app in the Shopify Dev Dashboard (dev.shopify.com → your app → Settings). "
            + "An API key from an old custom app made in the Shopify admin doesn't work for this.",
        "invalid_client" or "invalid_client_secret" or "invalid_request" =>
            "the Client secret doesn't match the Client ID. Copy both again from the app's Settings in the Shopify Dev Dashboard.",
        "shop_not_permitted" =>
            $"this app may only be used on stores of its own organization. Create the app in the Dev Dashboard with the account that owns {domain}, and install it on that store.",
        "app_not_installed" or "invalid_scope" =>
            $"the app isn't installed on {domain} (or its access was not approved). In the Dev Dashboard, release a version with the access scopes and install the app on the store.",
        _ => $"Shopify refused the login ({code}). Check the Client ID and secret, and that the app is installed on {domain}.",
    };

    /// <summary>Turns the token endpoint's error into advice.</summary>
    private static string TokenError(System.Net.HttpStatusCode status, string text)
    {
        var detail = text.Length > 200 ? text[..200] : text;
        foreach (var code in new[] { "application_cannot_be_found", "invalid_client", "shop_not_permitted", "app_not_installed" })
            if (detail.Contains(code, StringComparison.OrdinalIgnoreCase)) return OAuthAdvice(code, "this store");
        if (detail.Contains("shop_not_permitted", StringComparison.OrdinalIgnoreCase))
            return "this app may only be used on stores of its own organization. Create the app in the Dev Dashboard with the account that owns the store, and install it on the store.";
        if (status is System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.Unauthorized)
            return $"the Client ID or Client secret wasn't accepted, or the app isn't installed on this store ({(int)status}: {detail}).";
        return $"{(int)status} {status}: {detail}";
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
            JsonNode node;
            try
            {
                node = await http.SendJsonAsync(HttpMethod.Post, $"https://{auth.ShopDomain}/admin/api/{apiVersion}/graphql.json",
                    new JsonObject { ["query"] = query, ["variables"] = variables ?? [] },
                    h => h.Add("X-Shopify-Access-Token", token), ct) ?? new JsonObject();
            }
            catch (NotJsonException ex) { throw new ShopifyException(ShopifyAuth.HtmlAnswer(auth.ShopDomain, ex.Status, ex.Html, null)); }
            catch (ApiException ex) when (ex.Status == System.Net.HttpStatusCode.NotFound)
            {
                throw new ShopifyException($"API version \"{apiVersion}\" wasn't found. Leave the API version empty in Settings → Shops to use the default.");
            }

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
        media(first: 100) { nodes { id alt mediaContentType ... on MediaImage { image { url } } } }
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

    // ---- Pictures (media) ----------------------------------------------------------------------------

    /// <summary>
    /// Uploads pictures and adds them to the end of the product's media, in order. Returns the new media ids.
    /// Files are sent to Shopify's staged upload storage first, then attached with <c>productUpdate</c>.
    /// </summary>
    public async Task<IReadOnlyList<string>> AddMediaAsync(string productId, IReadOnlyList<(string Path, string Alt)> pictures, CancellationToken ct = default)
    {
        if (pictures.Count == 0) return [];
        var staged = await GraphQLAsync("""
            mutation($input: [StagedUploadInput!]!) { stagedUploadsCreate(input: $input) {
              stagedTargets { url resourceUrl parameters { name value } } userErrors { field message } } }
            """, new JsonObject
        {
            ["input"] = new JsonArray(pictures.Select(p => (JsonNode)new JsonObject
            {
                ["filename"] = System.IO.Path.GetFileName(p.Path),
                ["mimeType"] = MimeType(p.Path),
                ["resource"] = "IMAGE",
                ["httpMethod"] = "POST",
                ["fileSize"] = new FileInfo(p.Path).Length.ToString(CultureInfo.InvariantCulture),
            }).ToArray()),
        }, ct);
        ThrowOnUserErrors(staged["stagedUploadsCreate"]);
        var targets = staged["stagedUploadsCreate"].Arr("stagedTargets").ToList();
        if (targets.Count != pictures.Count) throw new ShopifyException("Shopify didn't prepare an upload for every picture.");

        var sources = new List<string>();
        for (var i = 0; i < pictures.Count; i++)
        {
            var target = targets[i];
            var path = pictures[i].Path;
            await http.SendAsync(() =>
            {
                var form = new MultipartFormDataContent();
                foreach (var prm in target.Arr("parameters"))
                    form.Add(new StringContent(prm.Str("value")), prm.Str("name"));
                var file = new ByteArrayContent(File.ReadAllBytes(path));
                file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(MimeType(path));
                form.Add(file, "file", System.IO.Path.GetFileName(path));
                return new HttpRequestMessage(HttpMethod.Post, target.Str("url")) { Content = form };
            }, ct);
            sources.Add(target.Str("resourceUrl"));
        }

        var data = await GraphQLAsync("""
            mutation($product: ProductUpdateInput!, $media: [CreateMediaInput!], $count: Int!) {
              productUpdate(product: $product, media: $media) {
                product { media(last: $count) { nodes { id } } } userErrors { field message } } }
            """, new JsonObject
        {
            ["product"] = new JsonObject { ["id"] = Ids.ShopifyProductGid(productId) },
            ["media"] = new JsonArray(pictures.Select((p, i) => (JsonNode)new JsonObject
            {
                ["originalSource"] = sources[i],
                ["alt"] = p.Alt,
                ["mediaContentType"] = "IMAGE",
            }).ToArray()),
            ["count"] = pictures.Count,
        }, ct);
        ThrowOnUserErrors(data["productUpdate"]);
        return data["productUpdate"]?["product"]?["media"].Arr("nodes").Select(n => n.Str("id")).ToList() ?? [];
    }

    /// <summary>Changes the alt text of pictures.</summary>
    public async Task UpdateMediaAltAsync(IReadOnlyList<(string MediaId, string Alt)> changes, CancellationToken ct = default)
    {
        if (changes.Count == 0) return;
        var data = await GraphQLAsync("""
            mutation($files: [FileUpdateInput!]!) { fileUpdate(files: $files) { userErrors { field message } } }
            """, new JsonObject
        {
            ["files"] = new JsonArray(changes.Select(c => (JsonNode)new JsonObject { ["id"] = c.MediaId, ["alt"] = c.Alt }).ToArray()),
        }, ct);
        ThrowOnUserErrors(data["fileUpdate"]);
    }

    /// <summary>Takes pictures off the product (the files stay in the store's Files).</summary>
    public async Task RemoveMediaAsync(string productId, IReadOnlyList<string> mediaIds, CancellationToken ct = default)
    {
        if (mediaIds.Count == 0) return;
        var gid = Ids.ShopifyProductGid(productId);
        var data = await GraphQLAsync("""
            mutation($files: [FileUpdateInput!]!) { fileUpdate(files: $files) { userErrors { field message } } }
            """, new JsonObject
        {
            ["files"] = new JsonArray(mediaIds.Select(id => (JsonNode)new JsonObject
            {
                ["id"] = id,
                ["referencesToRemove"] = new JsonArray((JsonNode)gid),
            }).ToArray()),
        }, ct);
        ThrowOnUserErrors(data["fileUpdate"]);
    }

    /// <summary>Puts the product's pictures in this order (Shopify does it in the background).</summary>
    public async Task ReorderMediaAsync(string productId, IReadOnlyList<string> orderedMediaIds, CancellationToken ct = default)
    {
        if (orderedMediaIds.Count < 2) return;
        var data = await GraphQLAsync("""
            mutation($id: ID!, $moves: [MoveInput!]!) { productReorderMedia(id: $id, moves: $moves) { userErrors { field message } } }
            """, new JsonObject
        {
            ["id"] = Ids.ShopifyProductGid(productId),
            ["moves"] = new JsonArray(orderedMediaIds.Select((id, i) => (JsonNode)new JsonObject
            {
                ["id"] = id,
                ["newPosition"] = i.ToString(CultureInfo.InvariantCulture),
            }).ToArray()),
        }, ct);
        ThrowOnUserErrors(data["productReorderMedia"]);
    }

    private static string MimeType(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".heic" => "image/heic",
        ".avif" => "image/avif",
        ".tif" or ".tiff" => "image/tiff",
        ".bmp" => "image/bmp",
        _ => "image/jpeg",
    };

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
        Images = p["media"].Arr("nodes").Where(m => m.Str("mediaContentType") == "IMAGE")
            .Select(m => new RemoteImage(m.Str("id"), m["image"].Str("url"), m.Str("alt"))).ToList(),
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

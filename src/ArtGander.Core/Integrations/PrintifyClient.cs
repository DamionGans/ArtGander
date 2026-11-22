using System.Globalization;
using System.Text.Json.Nodes;

namespace ArtGander.Core.Integrations;

/// <summary>
/// Printify REST API v1 (https://developers.printify.com). Authenticated with a personal access token.
/// Prices and costs are whole cents in the API and decimals here.
/// </summary>
public sealed class PrintifyClient(ApiHttp http, string token) : IPrintifyApi
{
    public const string BaseUrl = "https://api.printify.com/v1/";

    private Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken ct) =>
        http.SendJsonAsync(method, BaseUrl + path, body, h => h.Authorization = new("Bearer", token), ct);

    public async Task<IReadOnlyList<PrintifyShop>> GetShopsAsync(CancellationToken ct = default)
    {
        var node = await SendAsync(HttpMethod.Get, "shops.json", null, ct);
        return (node as JsonArray ?? []).OfType<JsonNode>()
            .Select(s => new PrintifyShop(s.Long("id") ?? 0, s.Str("title"), s.Str("sales_channel")))
            .ToList();
    }

    public async Task<IReadOnlyList<RemoteProduct>> GetProductsAsync(long shopId, CancellationToken ct = default)
    {
        var all = new List<RemoteProduct>();
        for (var page = 1; ; page++)
        {
            var node = await SendAsync(HttpMethod.Get, $"shops/{shopId}/products.json?limit=50&page={page}", null, ct);
            all.AddRange(node.Arr("data").Select(p => ParseProduct(p, shopId)));
            var last = node.Long("last_page") ?? page;
            if (page >= last || node.Arr("data").Count() == 0) break;
        }
        return all;
    }

    public async Task<RemoteProduct> GetProductAsync(long shopId, string productId, CancellationToken ct = default) =>
        ParseProduct(await SendAsync(HttpMethod.Get, $"shops/{shopId}/products/{productId}.json", null, ct), shopId);

    /// <summary>
    /// Updates only the given fields. Variant prices must list every variant that changes (with its id).
    /// Fields left null are not sent, so Printify keeps them as they are.
    /// </summary>
    public async Task UpdateProductAsync(long shopId, string productId, PrintifyUpdate update, CancellationToken ct = default)
    {
        var body = new JsonObject();
        if (update.Title is not null) body["title"] = update.Title;
        if (update.Description is not null) body["description"] = update.Description;
        if (update.Tags is not null) body["tags"] = new JsonArray(update.Tags.Select(t => (JsonNode)t).ToArray());
        if (update.Variants is { Count: > 0 })
            body["variants"] = new JsonArray(update.Variants.Select(v => (JsonNode)new JsonObject
            {
                ["id"] = long.Parse(v.Id, CultureInfo.InvariantCulture),
                ["price"] = ToCents(v.Price ?? 0),
                ["is_enabled"] = v.Enabled,
            }).ToArray());
        if (body.Count == 0) return;
        await SendAsync(HttpMethod.Put, $"shops/{shopId}/products/{productId}.json", body, ct);
    }

    /// <summary>
    /// Asks Printify to push the product to the connected store (e.g. Shopify). Only the parts set to true are
    /// pushed; everything else in the store (collections, SEO, images…) stays as it is.
    /// </summary>
    public Task PublishAsync(long shopId, string productId, PublishParts parts, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"shops/{shopId}/products/{productId}/publish.json", new JsonObject
        {
            ["title"] = parts.Title,
            ["description"] = parts.Description,
            ["images"] = parts.Images,
            ["variants"] = parts.Variants,
            ["tags"] = parts.Tags,
            ["keyFeatures"] = false,
            ["shipping_template"] = false,
        }, ct);

    /// <summary>Uploads an image to the Printify media library and returns its id.</summary>
    public async Task<string> UploadImageAsync(string fileName, byte[] contents, CancellationToken ct = default)
    {
        var node = await SendAsync(HttpMethod.Post, "uploads/images.json", new JsonObject
        {
            ["file_name"] = fileName,
            ["contents"] = Convert.ToBase64String(contents),
        }, ct);
        return node.Str("id");
    }

    /// <summary>Creates a product by copying a template product's blueprint, provider, variants and placement with a new image.</summary>
    public async Task<string> CreateFromTemplateAsync(long shopId, JsonNode template, string imageId, string title, string description,
        IReadOnlyList<string> tags, IReadOnlyDictionary<string, decimal> prices, CancellationToken ct = default)
    {
        var variants = new JsonArray();
        foreach (var v in template.Arr("variants").Where(v => v.Bool("is_enabled")))
        {
            var id = v.Long("id")!.Value;
            var price = prices.TryGetValue(id.ToString(CultureInfo.InvariantCulture), out var p) ? p : (v.Cents("price") ?? 0);
            variants.Add((JsonNode)new JsonObject { ["id"] = id, ["price"] = ToCents(price), ["is_enabled"] = true });
        }

        var printAreas = new JsonArray();
        foreach (var area in template.Arr("print_areas"))
        {
            var placeholders = new JsonArray();
            foreach (var ph in area.Arr("placeholders"))
            {
                var first = ph.Arr("images").FirstOrDefault();
                placeholders.Add((JsonNode)new JsonObject
                {
                    ["position"] = ph.Str("position"),
                    ["images"] = new JsonArray(new JsonObject
                    {
                        ["id"] = imageId,
                        ["x"] = first.Dec("x") ?? 0.5m,
                        ["y"] = first.Dec("y") ?? 0.5m,
                        ["scale"] = first.Dec("scale") ?? 1m,
                        ["angle"] = first.Dec("angle") ?? 0m,
                    }),
                });
            }
            printAreas.Add((JsonNode)new JsonObject
            {
                ["variant_ids"] = area["variant_ids"]?.DeepClone(),
                ["placeholders"] = placeholders,
            });
        }

        var node = await SendAsync(HttpMethod.Post, $"shops/{shopId}/products.json", new JsonObject
        {
            ["title"] = title,
            ["description"] = description,
            ["tags"] = new JsonArray(tags.Select(t => (JsonNode)t).ToArray()),
            ["blueprint_id"] = template.Long("blueprint_id"),
            ["print_provider_id"] = template.Long("print_provider_id"),
            ["variants"] = variants,
            ["print_areas"] = printAreas,
        }, ct);
        return node.Str("id");
    }

    /// <summary>The raw product JSON, used as a template for new products.</summary>
    public Task<JsonNode?> GetProductJsonAsync(long shopId, string productId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, $"shops/{shopId}/products/{productId}.json", null, ct);

    /// <summary>Orders, newest first. Stops after <paramref name="since"/> (orders created before it aren't fetched).</summary>
    public async Task<IReadOnlyList<RemoteOrder>> GetOrdersAsync(long shopId, DateTimeOffset? since, CancellationToken ct = default)
    {
        var all = new List<RemoteOrder>();
        for (var page = 1; ; page++)
        {
            var node = await SendAsync(HttpMethod.Get, $"shops/{shopId}/orders.json?limit=50&page={page}", null, ct);
            var orders = node.Arr("data").Select(ParseOrder).ToList();
            all.AddRange(orders.Where(o => since is null || o.CreatedAt >= since));
            var last = node.Long("last_page") ?? page;
            if (page >= last || orders.Count == 0 || (since is not null && orders.All(o => o.CreatedAt < since))) break;
        }
        return all;
    }

    public static RemoteProduct ParseProduct(JsonNode? p, long shopId) => new()
    {
        Channel = "printify",
        ShopId = shopId,
        Id = p.Str("id"),
        Title = p.Str("title"),
        Description = p.Str("description"),
        Tags = p.Arr("tags").Select(t => t.GetValue<string>()).ToList(),
        Status = p.Bool("visible", true) ? "visible" : "hidden",
        UpdatedAt = p.Str("updated_at"),
        IsLocked = p.Bool("is_locked"),
        BlueprintId = p.Long("blueprint_id"),
        PrintProviderId = p.Long("print_provider_id"),
        ExternalId = p?["external"] is JsonObject ext && ext.Str("id") is { Length: > 0 } eid ? eid : null,
        Handle = p?["external"].Str("handle") ?? "",
        ImageUrl = p.Arr("images").FirstOrDefault(i => i.Bool("is_default")).Str("src") is { Length: > 0 } src ? src : p.Arr("images").FirstOrDefault().Str("src"),
        Images = p.Arr("images").Where(i => i.Str("src").Length > 0)
            .Select(i => new RemoteImage(i.Str("src"), i.Str("src"), "", i.Bool("is_selected_for_publishing", true), i.Bool("is_default"))).ToList(),
        Variants = p.Arr("variants").Select(v => new RemoteVariant
        {
            Id = v.Str("id"),
            Title = v.Str("title"),
            Sku = v.Str("sku"),
            Price = v.Cents("price"),
            Cost = v.Cents("cost"),
            Enabled = v.Bool("is_enabled"),
        }).ToList(),
    };

    public static RemoteOrder ParseOrder(JsonNode o)
    {
        var shipment = o.Arr("shipments").FirstOrDefault();
        var meta = o["metadata"];
        var status = o.Str("status");
        return new RemoteOrder
        {
            Channel = "printify",
            Id = o.Str("id"),
            Name = meta.Str("shop_order_label"),
            CreatedAt = ParseDate(o.Str("created_at")) ?? DateTimeOffset.MinValue,
            UpdatedAt = o.Str("updated_at") is { Length: > 0 } u ? u : status + "|" + o.Arr("shipments").Count(),
            ShopOrderId = meta.Str("shop_order_id") is { Length: > 0 } sid ? sid : null,
            ShopOrderLabel = meta.Str("shop_order_label") is { Length: > 0 } label ? label : null,
            Cancelled = status is "canceled" or "cancelled",
            Fulfilled = status is "fulfilled" or "partially-fulfilled" || shipment is not null,
            Delivered = shipment is not null && shipment.Str("delivered_at").Length > 0,
            ProductionCost = o.Cents("total_price"),
            ShippingCost = o.Cents("total_shipping"),
            ProductionTax = o.Cents("total_tax"),
            Carrier = shipment.Str("carrier"),
            TrackingCode = shipment.Str("number"),
            ShippedOn = o.Arr("line_items").Select(l => ParseDate(l.Str("fulfilled_at"))).FirstOrDefault(d => d is not null) is { } shipped
                ? DateOnly.FromDateTime(shipped.LocalDateTime) : null,
            Lines = o.Arr("line_items").Select(l => new RemoteOrderLine
            {
                Id = l.Str("id") is { Length: > 0 } id ? id : $"{l.Str("product_id")}:{l.Str("variant_id")}",
                Title = l["metadata"].Str("title"),
                Sku = l["metadata"].Str("sku"),
                Quantity = (int)(l.Long("quantity") ?? 1),
                UnitPrice = l["metadata"].Cents("price"),
                UnitCost = l.Cents("cost") is { } c && (l.Long("quantity") ?? 1) > 0 ? c / (l.Long("quantity") ?? 1) : null,
                ProductId = l.Str("product_id"),
                VariantId = l.Str("variant_id"),
            }).ToList(),
        };
    }

    internal static DateTimeOffset? ParseDate(string s) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    private static long ToCents(decimal amount) => (long)Math.Round(amount * 100m, MidpointRounding.AwayFromZero);
}

/// <summary>The fields to change on a Printify product; null means "leave as it is".</summary>
public sealed record PrintifyUpdate(string? Title = null, string? Description = null, IReadOnlyList<string>? Tags = null, IReadOnlyList<RemoteVariant>? Variants = null);

/// <summary>Which parts Printify should push to the connected store when publishing.</summary>
public sealed record PublishParts(bool Title, bool Description, bool Tags, bool Variants, bool Images = false)
{
    public bool Any => Title || Description || Tags || Variants || Images;
}

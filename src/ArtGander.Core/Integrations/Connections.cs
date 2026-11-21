using System.Text.Json.Nodes;
using ArtGander.Core.Model;
using ArtGander.Core.Schema;
using ArtGander.Core.Storage;

namespace ArtGander.Core.Integrations;

/// <summary>Builds the Printify and Shopify clients from the library's settings and this computer's secret keys.</summary>
public sealed class Connections(HttpClient http, ISecretStore secrets)
{
    private ShopifyAuth? _auth;
    private string _authKey = "";

    public ISecretStore Secrets => secrets;

    public async Task<PrintifyClient?> PrintifyAsync()
    {
        var token = await secrets.GetAsync(SecretKeys.PrintifyToken);
        return string.IsNullOrWhiteSpace(token) ? null : new PrintifyClient(new ApiHttp(http), token.Trim());
    }

    public async Task<ShopifyClient?> ShopifyAsync(IntegrationSettings settings)
    {
        var secret = await secrets.GetAsync(SecretKeys.ShopifyClientSecret);
        if (string.IsNullOrWhiteSpace(settings.ShopifyDomain) || string.IsNullOrWhiteSpace(settings.ShopifyClientId) || string.IsNullOrWhiteSpace(secret))
            return null;
        var key = $"{settings.ShopifyDomain}|{settings.ShopifyClientId}|{secret.GetHashCode()}";
        if (_auth is null || _authKey != key)
        {
            _auth = new ShopifyAuth(http, settings.ShopifyDomain, settings.ShopifyClientId.Trim(), secret.Trim());
            _authKey = key;
        }
        var version = string.IsNullOrWhiteSpace(settings.ShopifyApiVersion) ? ShopifyClient.DefaultApiVersion : settings.ShopifyApiVersion.Trim();
        return new ShopifyClient(new ApiHttp(http), _auth, version);
    }
}

/// <summary>Library-wide sync bookkeeping in <c>.artgander/sync/state.json</c>: when orders were last imported.</summary>
public sealed class SyncState
{
    public DateTimeOffset? LastOrderImport { get; set; }
    public DateTimeOffset? LastProductImport { get; set; }

    public static string PathFor(string root) => Path.Combine(root, CurrentSchema.LibraryFolderName, "sync", "state.json");

    public static SyncState Load(string root)
    {
        try
        {
            var path = PathFor(root);
            if (!File.Exists(path)) return new SyncState();
            var node = JsonNode.Parse(AtomicFile.DecodeUtf8(AtomicFile.ReadAllBytes(path)));
            return new SyncState
            {
                LastOrderImport = PrintifyClient.ParseDate(node.Str("lastOrderImport")),
                LastProductImport = PrintifyClient.ParseDate(node.Str("lastProductImport")),
            };
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { return new SyncState(); }
    }

    public void Save(string root)
    {
        var path = PathFor(root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var node = new JsonObject
        {
            ["lastOrderImport"] = LastOrderImport?.ToString("O"),
            ["lastProductImport"] = LastProductImport?.ToString("O"),
        };
        AtomicFile.WriteAllText(path, node.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
    }
}

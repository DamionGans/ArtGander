namespace ArtGander.Core.Integrations;

/// <summary>
/// Stores secret keys (API tokens) per computer and per user, in the operating system's keychain.
/// Never in the art folder, which is shared.
/// </summary>
public interface ISecretStore
{
    Task<string?> GetAsync(string key);
    Task SetAsync(string key, string value);
    Task RemoveAsync(string key);
}

public static class SecretKeys
{
    public const string PrintifyToken = "printify.token";
    public const string ShopifyClientSecret = "shopify.clientSecret";
}

/// <summary>An in-memory store, for tests.</summary>
public sealed class MemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _values = [];
    public Task<string?> GetAsync(string key) => Task.FromResult(_values.TryGetValue(key, out var v) ? v : null);
    public Task SetAsync(string key, string value) { _values[key] = value; return Task.CompletedTask; }
    public Task RemoveAsync(string key) { _values.Remove(key); return Task.CompletedTask; }
}

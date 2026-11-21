using ArtGander.Core.Integrations;

namespace ArtGander.App.Services;

/// <summary>
/// Keeps API keys in the operating system's keychain for the signed-in user: the Windows Credential Locker or the
/// macOS Keychain. They never go into the art folder, which other computers share.
/// </summary>
public sealed class SecretStore : ISecretStore
{
#if WINDOWS
    private const string Resource = "ArtGander";

    public Task<string?> GetAsync(string key)
    {
        try
        {
            var vault = new Windows.Security.Credentials.PasswordVault();
            var credential = vault.Retrieve(Resource, key);
            credential.RetrievePassword();
            return Task.FromResult<string?>(credential.Password);
        }
        catch (Exception) { return Task.FromResult<string?>(null); } // not stored yet
    }

    public Task SetAsync(string key, string value)
    {
        var vault = new Windows.Security.Credentials.PasswordVault();
        try { vault.Remove(vault.Retrieve(Resource, key)); } catch (Exception) { }
        vault.Add(new Windows.Security.Credentials.PasswordCredential(Resource, key, value));
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key)
    {
        try
        {
            var vault = new Windows.Security.Credentials.PasswordVault();
            vault.Remove(vault.Retrieve(Resource, key));
        }
        catch (Exception) { }
        return Task.CompletedTask;
    }
#else
    public Task<string?> GetAsync(string key) => SecureStorage.Default.GetAsync(key);

    public Task SetAsync(string key, string value) => SecureStorage.Default.SetAsync(key, value);

    public Task RemoveAsync(string key)
    {
        SecureStorage.Default.Remove(key);
        return Task.CompletedTask;
    }
#endif
}

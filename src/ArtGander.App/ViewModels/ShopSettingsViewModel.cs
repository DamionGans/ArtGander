using System.Collections.ObjectModel;
using ArtGander.App.Services;
using ArtGander.Core.Integrations;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ArtGander.App.ViewModels;

/// <summary>
/// Settings → Shops: connects the library to Printify and Shopify. Keys are typed by you and stored in this computer's
/// keychain; the shop ids are stored in the art folder so every computer knows which shops belong to the library.
/// </summary>
public sealed partial class ShopSettingsViewModel(AppState app, Connections connections) : ObservableObject
{
    public const string GuideUrl = "https://github.com/DamionGans/ArtGander/blob/main/docs/integrations.md";

    [ObservableProperty] public partial string PrintifyToken { get; set; } = "";
    [ObservableProperty] public partial bool HasPrintifyToken { get; set; }
    [ObservableProperty] public partial PrintifyShop? SelectedPrintifyShop { get; set; }
    [ObservableProperty] public partial string PrintifyStatus { get; set; } = "";

    [ObservableProperty] public partial string ShopifyDomain { get; set; } = "";
    [ObservableProperty] public partial string ShopifyClientId { get; set; } = "";
    [ObservableProperty] public partial string ShopifyClientSecret { get; set; } = "";
    [ObservableProperty] public partial bool HasShopifySecret { get; set; }
    [ObservableProperty] public partial string ShopifyApiVersion { get; set; } = "";
    [ObservableProperty] public partial string ShopifyStatus { get; set; } = "";
    [ObservableProperty] public partial bool IsBusy { get; set; }

    public ObservableCollection<PrintifyShop> PrintifyShops { get; } = [];
    public bool HasShops => PrintifyShops.Count > 0;
    public string DefaultApiVersion => ShopifyClient.DefaultApiVersion;

    public async Task LoadAsync()
    {
        var s = app.Settings.Integrations;
        ShopifyDomain = s.ShopifyDomain;
        ShopifyClientId = s.ShopifyClientId;
        ShopifyApiVersion = s.ShopifyApiVersion;
        HasPrintifyToken = !string.IsNullOrEmpty(await connections.Secrets.GetAsync(SecretKeys.PrintifyToken));
        HasShopifySecret = !string.IsNullOrEmpty(await connections.Secrets.GetAsync(SecretKeys.ShopifyClientSecret));
        PrintifyToken = "";
        ShopifyClientSecret = "";
        PrintifyStatus = s.PrintifyShopId is { } id ? $"Connected to Printify shop \"{s.PrintifyShopTitle}\" ({id})." : HasPrintifyToken ? "Token saved. Pick your shop." : "Not connected.";
        ShopifyStatus = s.ShopifyDomain.Length > 0 && HasShopifySecret ? $"Set up for {s.ShopifyDomain}." : "Not connected.";
    }

    [RelayCommand]
    private void OpenGuide() => Ui.Fire(() => Launcher.Default.OpenAsync(new Uri(GuideUrl)));

    [RelayCommand]
    private void ConnectPrintify() => Ui.Fire(ConnectPrintifyAsync);

    private async Task ConnectPrintifyAsync()
    {
        if (!app.HasRoot) return;
        IsBusy = true;
        try
        {
            if (PrintifyToken.Trim().Length > 0)
            {
                await connections.Secrets.SetAsync(SecretKeys.PrintifyToken, PrintifyToken.Trim());
                PrintifyToken = "";
                HasPrintifyToken = true;
            }
            var client = await connections.PrintifyAsync();
            if (client is null) { PrintifyStatus = "Paste your Printify personal access token first."; return; }
            var shops = await client.GetShopsAsync();
            PrintifyShops.Clear();
            foreach (var shop in shops) PrintifyShops.Add(shop);
            OnPropertyChanged(nameof(HasShops));
            SelectedPrintifyShop = shops.FirstOrDefault(x => x.Id == app.Settings.Integrations.PrintifyShopId)
                                   ?? shops.FirstOrDefault(x => x.SalesChannel == "shopify") ?? shops.FirstOrDefault();
            PrintifyStatus = shops.Count == 0 ? "The token works, but there are no shops in this Printify account." : $"Found {shops.Count} shop(s). Check the one below and press Use this shop.";
        }
        catch (Exception ex) { PrintifyStatus = "Couldn't connect: " + ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void UsePrintifyShop() => Ui.Fire(UsePrintifyShopAsync);

    private async Task UsePrintifyShopAsync()
    {
        if (SelectedPrintifyShop is not { } shop) return;
        var s = app.Settings.Integrations;
        s.PrintifyShopId = shop.Id;
        s.PrintifyShopTitle = shop.Title;
        await SaveSettingsAsync();
        PrintifyStatus = $"Connected to Printify shop \"{shop.Title}\" ({shop.Id})."
                         + (shop.SalesChannel == "shopify" ? " Products published there appear in your Shopify store." : $" Its sales channel is {shop.SalesChannel}.");
    }

    [RelayCommand]
    private void ConnectShopify() => Ui.Fire(ConnectShopifyAsync);

    private async Task ConnectShopifyAsync()
    {
        if (!app.HasRoot) return;
        IsBusy = true;
        try
        {
            var s = app.Settings.Integrations;
            s.ShopifyDomain = ShopifyDomain.Trim().Length == 0 ? "" : ShopifyAuth.NormalizeDomain(ShopifyDomain);
            s.ShopifyClientId = ShopifyClientId.Trim();
            s.ShopifyApiVersion = ShopifyApiVersion.Trim();
            ShopifyDomain = s.ShopifyDomain;
            if (ShopifyClientSecret.Trim().Length > 0)
            {
                await connections.Secrets.SetAsync(SecretKeys.ShopifyClientSecret, ShopifyClientSecret.Trim());
                ShopifyClientSecret = "";
                HasShopifySecret = true;
            }
            await SaveSettingsAsync();
            var client = await connections.ShopifyAsync(s);
            if (client is null) { ShopifyStatus = "Fill in the store address, Client ID and Client secret."; return; }
            var name = await client.GetShopNameAsync();
            ShopifyStatus = $"Connected to \"{name}\" ({s.ShopifyDomain}).";
        }
        catch (Exception ex) { ShopifyStatus = "Couldn't connect: " + ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void ForgetKeys() => Ui.Fire(ForgetKeysAsync);

    private async Task ForgetKeysAsync()
    {
        if (!await Dialogs.ConfirmAsync("Forget the keys on this computer?", "The Printify token and Shopify client secret are removed from this computer's keychain. The other computers keep theirs.", "Forget")) return;
        await connections.Secrets.RemoveAsync(SecretKeys.PrintifyToken);
        await connections.Secrets.RemoveAsync(SecretKeys.ShopifyClientSecret);
        await LoadAsync();
    }

    private async Task SaveSettingsAsync()
    {
        try { app.SaveSettings(); }
        catch (Exception ex) { await Dialogs.ErrorAsync(ex); }
    }
}

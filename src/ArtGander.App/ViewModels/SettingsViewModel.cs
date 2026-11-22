using ArtGander.App.Services;
using ArtGander.Core.Schema;
using ArtGander.Core.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ArtGander.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppState _app;
    private readonly IPlatformServices _platform;
    private readonly ProtectionService _protection;
    private readonly Core.Imaging.IColorConverter _colors;

    public SettingsViewModel(AppState app, IPlatformServices platform, ShopSettingsViewModel shops, ProtectionService protection, Core.Imaging.IColorConverter colors)
    {
        _app = app;
        _platform = platform;
        _protection = protection;
        _colors = colors;
        Categories[0].IsCurrent = true;
        Shops = shops;
        _app.LibraryChanged += (_, _) => Load();
        Load();
    }

    [ObservableProperty] public partial string RootPath { get; set; } = "";
    [ObservableProperty] public partial string DefaultCurrency { get; set; } = "EUR";
    [ObservableProperty] public partial string DefaultCopyrightOwner { get; set; } = "";
    [ObservableProperty] public partial string Channels { get; set; } = "";
    [ObservableProperty] public partial string FileKinds { get; set; } = "";
    [ObservableProperty] public partial string? Status { get; set; }

    // Image protection (Mistcoat)
    public static IReadOnlyList<string> ProtectPresets => ArtGander.Core.Integrations.MistcoatPasses.Presets;
    [ObservableProperty] public partial string MistcoatPath { get; set; } = "";
    [ObservableProperty] public partial string MistcoatStatus { get; set; } = "";
    [ObservableProperty] public partial string ProtectPreset { get; set; } = "balanced";
    [ObservableProperty] public partial string ProtectCredit { get; set; } = "";
    [ObservableProperty] public partial bool ProtectMetadata { get; set; } = true;

    public bool HasRoot => _app.HasRoot;

    /// <summary>The Printify and Shopify connection.</summary>
    public ShopSettingsViewModel Shops { get; }
    public string Version => $"ArtGander {ArtPieceStore.AppVersion}";
    public string FormatVersion => $"Details file format version {CurrentSchema.Version}";

    private void Load()
    {
        RootPath = _app.RootPath ?? "No art folder chosen yet";
        DefaultCurrency = _app.Settings.DefaultCurrency;
        DefaultCopyrightOwner = _app.Settings.DefaultCopyrightOwner;
        Channels = string.Join(", ", _app.Settings.Channels);
        FileKinds = string.Join(", ", _app.Settings.FileKinds);
        ProtectPreset = _app.Settings.Protection.Preset;
        ProtectCredit = _app.Settings.Protection.Credit;
        ProtectMetadata = _app.Settings.Protection.Metadata;
        MistcoatPath = _protection.ConfiguredPath;
        UpdateMistcoatStatus();
        _profilesLoaded = false;
        if (Category == "colors") LoadProfiles();
        OnPropertyChanged(nameof(HasRoot));
    }

    [RelayCommand]
    private async Task ChooseFolderAsync()
    {
        var path = await _platform.PickFolderAsync();
        if (path is null) return;
        await _app.OpenAsync(path);
        Load();
    }

    private void UpdateMistcoatStatus()
    {
        var found = ArtGander.Core.Integrations.MistcoatRunner.Find(_protection.ConfiguredPath);
        MistcoatStatus = found is null
            ? (string.IsNullOrWhiteSpace(_protection.ConfiguredPath)
                ? "Mistcoat wasn't found on this computer. Install it, then choose Mistcoat.exe (Windows) or Mistcoat.app (Mac) here."
                : "That program doesn't exist on this computer. Choose where Mistcoat is.")
            : $"Using {found}";
    }

    /// <summary>Saves the Mistcoat location typed in the box (per computer) and checks it.</summary>
    [RelayCommand]
    private void SaveMistcoatPath()
    {
        _protection.ConfiguredPath = MistcoatPath;
        UpdateMistcoatStatus();
    }

    [RelayCommand]
    private async Task ChooseMistcoatAsync()
    {
        var start = OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) : "/Applications";
        IReadOnlyList<string> picked;
        try { picked = await _platform.PickFilesAsync(start); }
        catch (Exception ex) { await Dialogs.ErrorAsync(ex); return; }
        if (picked.Count == 0) return;
        MistcoatPath = picked[0];
        SaveMistcoatPath();
    }

    [RelayCommand]
    private void FindMistcoat()
    {
        MistcoatPath = "";
        SaveMistcoatPath();
    }

    [RelayCommand]
    private void RevealFolder()
    {
        if (_app.HasRoot) _platform.RevealInFileManager(_app.RootPath!);
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (!_app.HasRoot) return;
        var s = _app.Settings;
        s.DefaultCurrency = string.IsNullOrWhiteSpace(DefaultCurrency) ? "EUR" : DefaultCurrency.Trim().ToUpperInvariant();
        s.DefaultCopyrightOwner = DefaultCopyrightOwner.Trim();
        var channels = Channels.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        s.Channels.Clear();
        foreach (var c in channels) s.Channels.Add(c);
        var kinds = FileKinds.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (kinds.Count == 0) kinds = [.. Core.Model.FileKind.Defaults];
        s.FileKinds.Clear();
        foreach (var k in kinds) s.FileKinds.Add(k);
        s.Protection.Preset = ProtectPresets.Contains(ProtectPreset) ? ProtectPreset : "balanced";
        s.Protection.Credit = ProtectCredit.Trim();
        s.Protection.Metadata = ProtectMetadata;
        try
        {
            ApplyColors();
            _app.SaveSettings();
            Status = "Saved to the art folder, so these settings are shared with your other computers.";
            await _app.RefreshAsync();
        }
        catch (Exception ex) { await Dialogs.ErrorAsync(ex); }
    }
}

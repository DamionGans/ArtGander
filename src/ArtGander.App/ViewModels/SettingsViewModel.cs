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

    public SettingsViewModel(AppState app, IPlatformServices platform)
    {
        _app = app;
        _platform = platform;
        _app.LibraryChanged += (_, _) => Load();
        Load();
    }

    [ObservableProperty] public partial string RootPath { get; set; } = "";
    [ObservableProperty] public partial string DefaultCurrency { get; set; } = "EUR";
    [ObservableProperty] public partial string DefaultCopyrightOwner { get; set; } = "";
    [ObservableProperty] public partial string Channels { get; set; } = "";
    [ObservableProperty] public partial string? Status { get; set; }

    public bool HasRoot => _app.HasRoot;
    public string Version => $"ArtGander {ArtPieceStore.AppVersion}";
    public string FormatVersion => $"Details file format version {CurrentSchema.Version}";

    private void Load()
    {
        RootPath = _app.RootPath ?? "No art folder chosen yet";
        DefaultCurrency = _app.Settings.DefaultCurrency;
        DefaultCopyrightOwner = _app.Settings.DefaultCopyrightOwner;
        Channels = string.Join(", ", _app.Settings.Channels);
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
        try
        {
            _app.SaveSettings();
            Status = "Saved to the art folder, so these settings are shared with your other computers.";
            await _app.RefreshAsync();
        }
        catch (Exception ex) { await Dialogs.ErrorAsync(ex); }
    }
}

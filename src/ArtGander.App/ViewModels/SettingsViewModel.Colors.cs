using System.Collections.ObjectModel;
using ArtGander.App.Services;
using ArtGander.Core.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ArtGander.App.ViewModels;

/// <summary>A settings category in the list on the left.</summary>
public sealed partial class SettingsCategory(string key, string title, string description) : ObservableObject
{
    public string Key { get; } = key;
    public string Title { get; } = title;
    public string Description { get; } = description;
    [ObservableProperty] public partial bool IsCurrent { get; set; }
}

/// <summary>A color profile to choose: the computer's default, one stored in the library, built in, on this computer, or a chosen file.</summary>
public sealed record ProfileChoice(string Title, IccProfileInfo? Profile, string? LibraryFile)
{
    public bool IsDefault => Profile is null && LibraryFile is null;
}

public sealed record IntentChoice(string Key, string Title);

/// <summary>The navigable categories, and the color profiles for CMYK, Adobe RGB and sRGB versions.</summary>
public sealed partial class SettingsViewModel
{
    public ObservableCollection<SettingsCategory> Categories { get; } =
    [
        new("folder", "Art folder", "Where your art lives"),
        new("library", "Library", "Currency, copyright, channels, file kinds"),
        new("shops", "Shops", "Printify and Shopify"),
        new("protection", "Image protection", "Mistcoat"),
        new("colors", "Colors", "Color profiles for CMYK, Adobe RGB and sRGB"),
        new("about", "About", "Version"),
    ];

    [ObservableProperty] public partial string Category { get; set; } = "folder";
    public bool IsFolder => Category == "folder";
    public bool IsLibrary => Category == "library";
    public bool IsShops => Category == "shops";
    public bool IsProtection => Category == "protection";
    public bool IsColors => Category == "colors";
    public bool IsAbout => Category == "about";
    public string CategoryTitle => Categories.FirstOrDefault(c => c.Key == Category)?.Title ?? "";

    partial void OnCategoryChanged(string value)
    {
        foreach (var c in Categories) c.IsCurrent = c.Key == value;
        foreach (var name in new[] { nameof(IsFolder), nameof(IsLibrary), nameof(IsShops), nameof(IsProtection), nameof(IsColors), nameof(IsAbout), nameof(CategoryTitle) })
            OnPropertyChanged(name);
        if (value == "colors") LoadProfiles();
        Status = null;
    }

    [RelayCommand]
    private void SelectCategory(string? key)
    {
        if (key is not null) Category = key;
    }

    // ---- Colors ----

    public static IReadOnlyList<IntentChoice> Intents { get; } =
    [
        new("relative", "Relative colorimetric (keeps colors that fit exactly; best for most art)"),
        new("perceptual", "Perceptual (squeezes all colors in; smoother for very saturated art)"),
    ];

    public ObservableCollection<ProfileChoice> CmykProfiles { get; } = [];
    public ObservableCollection<ProfileChoice> AdobeRgbProfiles { get; } = [];
    public ObservableCollection<ProfileChoice> SrgbProfiles { get; } = [];
    [ObservableProperty] public partial ProfileChoice? CmykProfile { get; set; }
    [ObservableProperty] public partial ProfileChoice? AdobeRgbProfile { get; set; }
    [ObservableProperty] public partial ProfileChoice? SrgbProfile { get; set; }
    [ObservableProperty] public partial IntentChoice Intent { get; set; } = Intents[0];
    [ObservableProperty] public partial bool BlackPointCompensation { get; set; } = true;
    public bool ColorsAvailable => _colors.IsAvailable;

    private bool _profilesLoaded;

    /// <summary>Lists the profiles (reads the system's profile folders, so only when the Colors category is opened).</summary>
    private void LoadProfiles()
    {
        if (!_app.HasRoot) return;
        var root = _app.RootPath!;
        var library = ColorVersions.LibraryProfiles(root);
        var others = _colors.BuiltInProfiles.Concat(ColorVersions.SystemProfiles()).ToList();
        var s = _app.Settings.Colors;
        Fill(CmykProfiles, ColorTarget.Cmyk, library, others, s.CmykProfile, root, p => CmykProfile = p);
        Fill(AdobeRgbProfiles, ColorTarget.AdobeRgb, library, others, s.AdobeRgbProfile, root, p => AdobeRgbProfile = p);
        Fill(SrgbProfiles, ColorTarget.Srgb, library, others, s.SrgbProfile, root, p => SrgbProfile = p);
        Intent = Intents.FirstOrDefault(i => i.Key == s.Intent) ?? Intents[0];
        BlackPointCompensation = s.BlackPointCompensation;
        _profilesLoaded = true;
    }

    private void Fill(ObservableCollection<ProfileChoice> list, ColorTarget target, IReadOnlyList<IccProfileInfo> library,
        IReadOnlyList<IccProfileInfo> others, string current, string root, Action<ProfileChoice?> select)
    {
        var space = ColorVersions.ColorSpaceOf(target);
        list.Clear();
        list.Add(new ProfileChoice($"Default: {_colors.DefaultProfileName(target)}", null, null));
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in library.Where(p => p.ColorSpace == space))
        {
            var file = Path.GetFileName(FileOf(root, p) ?? p.Name);
            if (names.Add(p.Name)) list.Add(new ProfileChoice($"{p.Name} (in the art folder)", p, file));
        }
        foreach (var p in others.Where(p => p.ColorSpace == space).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
            if (names.Add(p.Name)) list.Add(new ProfileChoice($"{p.Name} ({p.Source.ToLowerInvariant()})", p, null));
        select(list.FirstOrDefault(c => c.LibraryFile is not null && string.Equals(c.LibraryFile, current, StringComparison.OrdinalIgnoreCase)) ?? list[0]);
    }

    /// <summary>Finds the file in the library's profile folder holding this profile.</summary>
    private static string? FileOf(string root, IccProfileInfo profile)
    {
        var folder = ColorVersions.ProfilesFolder(root);
        if (!Directory.Exists(folder)) return null;
        return Directory.EnumerateFiles(folder).FirstOrDefault(f => new FileInfo(f).Length == profile.Data.Length && File.ReadAllBytes(f).AsSpan().SequenceEqual(profile.Data));
    }

    [RelayCommand]
    private void ChooseCmykProfile() => Ui.Fire(() => ChooseProfileFileAsync(ColorTarget.Cmyk));

    [RelayCommand]
    private void ChooseAdobeRgbProfile() => Ui.Fire(() => ChooseProfileFileAsync(ColorTarget.AdobeRgb));

    [RelayCommand]
    private void ChooseSrgbProfile() => Ui.Fire(() => ChooseProfileFileAsync(ColorTarget.Srgb));

    /// <summary>Lets you pick an .icc/.icm file (e.g. one from your print shop) for a color space.</summary>
    private async Task ChooseProfileFileAsync(ColorTarget target)
    {
        var start = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "spool", "drivers", "color")
            : "/Library/ColorSync/Profiles";
        IReadOnlyList<string> picked;
        try { picked = await _platform.PickFilesAsync(start); }
        catch (Exception ex) { await Dialogs.ErrorAsync(ex); return; }
        if (picked.Count == 0) return;
        byte[] data;
        try { data = await File.ReadAllBytesAsync(picked[0]); }
        catch (Exception ex) { await Dialogs.ErrorAsync(ex); return; }
        var info = Icc.Read(data, Path.GetFileNameWithoutExtension(picked[0]), "Chosen file");
        var space = ColorVersions.ColorSpaceOf(target);
        if (info is null || info.ColorSpace != space)
        {
            await Dialogs.AlertAsync("Not a suitable profile", info is null
                ? $"\"{Path.GetFileName(picked[0])}\" isn't an ICC color profile."
                : $"\"{info.Name}\" is a{(info.ColorSpace == "CMYK" ? " CMYK" : "n RGB")} profile; this one needs a{(space == "CMYK" ? " CMYK" : "n RGB")} profile.");
            return;
        }
        var choice = new ProfileChoice($"{info.Name} (chosen file)", info, null);
        var list = target switch { ColorTarget.Cmyk => CmykProfiles, ColorTarget.AdobeRgb => AdobeRgbProfiles, _ => SrgbProfiles };
        list.Insert(1, choice);
        switch (target)
        {
            case ColorTarget.Cmyk: CmykProfile = choice; break;
            case ColorTarget.AdobeRgb: AdobeRgbProfile = choice; break;
            default: SrgbProfile = choice; break;
        }
        Status = "Chosen. Save settings to use it (it's copied into the art folder, so every computer uses the same profile).";
    }

    /// <summary>Stores chosen profiles in the library (<c>.artgander/profiles</c>) and records their file names.</summary>
    private void ApplyColors()
    {
        if (!_profilesLoaded || !_app.HasRoot) return;
        var root = _app.RootPath!;
        var s = _app.Settings.Colors;
        s.CmykProfile = Store(root, CmykProfile);
        s.AdobeRgbProfile = Store(root, AdobeRgbProfile);
        s.SrgbProfile = Store(root, SrgbProfile);
        s.Intent = Intent.Key;
        s.BlackPointCompensation = BlackPointCompensation;
    }

    private static string Store(string root, ProfileChoice? choice) =>
        choice is null || choice.IsDefault ? "" : choice.LibraryFile ?? ColorVersions.StoreProfile(root, choice.Profile!);
}

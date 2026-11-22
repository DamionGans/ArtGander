using ArtGander.App.Services;
using ArtGander.Core.Imaging;
using ArtGander.Core.Naming;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ArtGander.App.ViewModels;

public sealed record ColorTargetChoice(ColorTarget Target, string Title);

/// <summary>
/// A cover: a picture named after the piece (or product) without a version, the official fully cropped art.
/// New versions are made from it: a plain copy, or a CMYK, Adobe RGB or sRGB version with the profiles from Settings.
/// </summary>
public sealed partial class CoverRow : ProtectableRow
{
    public static IReadOnlyList<ColorTargetChoice> Targets { get; } =
        Enum.GetValues<ColorTarget>().Select(t => new ColorTargetChoice(t, ColorVersions.Title(t))).ToList();

    public CoverRow(string fullPath)
    {
        FullPath = fullPath;
        Target = Targets[0];
        RefreshProtectedCopy();
    }

    public override string FullPath { get; }
    public override bool IsMissing => !File.Exists(FullPath);
    public string FileName => Path.GetFileName(FullPath);

    [ObservableProperty] public partial ImageSource? Thumbnail { get; set; }
    /// <summary>The version label for the new file; empty uses a suggestion ("cmyk", "srgb"…).</summary>
    [ObservableProperty] public partial string NewVersion { get; set; } = "";
    [ObservableProperty] public partial ColorTargetChoice Target { get; set; }
    [ObservableProperty] public partial bool IsMaking { get; set; }
    [ObservableProperty] public partial string VersionMessage { get; set; } = "";

    public string VersionPlaceholder => $"e.g. {ColorVersions.SuggestedLabel(Target.Target)}, print-a4, web";
    public bool HasVersionMessage => VersionMessage.Length > 0;
    public bool CanMake => !IsMaking && !IsMissing;

    /// <summary>The label that will be used: the typed one, or the suggestion for the chosen kind.</summary>
    public string LabelToUse => FileNamer.Label(NewVersion) is { Length: > 0 } l ? l : ColorVersions.SuggestedLabel(Target.Target);

    partial void OnTargetChanged(ColorTargetChoice value) => OnPropertyChanged(nameof(VersionPlaceholder));
    partial void OnVersionMessageChanged(string value) => OnPropertyChanged(nameof(HasVersionMessage));
    partial void OnIsMakingChanged(bool value) => OnPropertyChanged(nameof(CanMake));

    public void CarryOverCover(CoverRow? from)
    {
        if (from is null) return;
        CarryOverFrom(from);
        NewVersion = from.NewVersion;
        Target = from.Target;
        IsMaking = from.IsMaking;
        VersionMessage = from.VersionMessage;
    }

    public async Task LoadThumbnailAsync(ThumbnailService thumbs)
    {
        var path = await thumbs.GetAsync(FullPath);
        if (path is not null) MainThread.BeginInvokeOnMainThread(() => Thumbnail = ImageSource.FromFile(path));
    }

    /// <summary>
    /// Makes the new version's file in a temporary place (a copy, or a color conversion) and returns its path;
    /// the caller moves it into the folder under the naming convention.
    /// </summary>
    public static async Task<(string Path, string Note)> MakeAsync(IColorConverter colors, string? rootPath, Core.Model.ColorSettings settings,
        string cover, ColorTarget target, CancellationToken ct = default)
    {
        var ext = ColorVersions.OutputExtension(Path.GetExtension(cover), target);
        var temp = Path.Combine(Path.GetTempPath(), "ArtGander", "versions", Guid.NewGuid().ToString("N") + ext);
        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
        if (target == ColorTarget.Copy)
        {
            File.Copy(cover, temp);
            return (temp, $"Copy of the cover {Path.GetFileName(cover)}.");
        }
        if (!colors.IsAvailable) throw new InvalidOperationException("Color conversion isn't available on this computer.");
        var profile = rootPath is null ? null : ColorVersions.ProfileData(rootPath, settings, target);
        var profileName = profile is not null && Icc.Read(profile, ColorVersions.SettingFor(settings, target)) is { } info ? info.Name : colors.DefaultProfileName(target);
        var intent = settings.Intent == "perceptual" ? ColorIntent.Perceptual : ColorIntent.RelativeColorimetric;
        await colors.ConvertAsync(cover, temp, target, profile, intent, settings.BlackPointCompensation, ct);
        return (temp, $"{ColorVersions.Title(target)} of the cover {Path.GetFileName(cover)}, profile {profileName}, {(intent == ColorIntent.Perceptual ? "perceptual" : "relative colorimetric")}{(settings.BlackPointCompensation ? " with black point compensation" : "")}.");
    }
}

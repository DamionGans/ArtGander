using ArtGander.Core.Imaging;
using ImageMagick;

namespace ArtGander.Imaging;

/// <summary>
/// Color conversion with ImageMagick and its color management (Little CMS). The source's embedded profile is used
/// (sRGB when it has none; a CMYK image without one counts as Coated FOGRA39), and the target profile is embedded in
/// the new file. CMYK can't be transparent: transparent areas become white.
/// </summary>
public sealed class MagickColorConverter : IColorConverter
{
    public bool IsAvailable => true;

    public IReadOnlyList<IccProfileInfo> BuiltInProfiles { get; } =
    [
        Info(ColorProfiles.CoatedFOGRA39, "Coated FOGRA39 (ISO 12647-2:2004)", "CMYK"),
        Info(ColorProfiles.USWebCoatedSWOP, "U.S. Web Coated (SWOP) v2", "CMYK"),
        Info(ColorProfiles.AdobeRGB1998, "Adobe RGB (1998)", "RGB"),
        Info(ColorProfiles.SRGB, "sRGB IEC61966-2.1", "RGB"),
        Info(ColorProfiles.AppleRGB, "Apple RGB", "RGB"),
        Info(ColorProfiles.ColorMatchRGB, "ColorMatch RGB", "RGB"),
    ];

    private static IccProfileInfo Info(IColorProfile profile, string fallback, string space) =>
        Icc.Read(profile.ToByteArray(), fallback, "Built into ArtGander") ?? new IccProfileInfo(fallback, space, profile.ToByteArray(), "Built into ArtGander");

    public string DefaultProfileName(ColorTarget target) => target switch
    {
        ColorTarget.Cmyk => "Coated FOGRA39",
        ColorTarget.AdobeRgb => "Adobe RGB (1998)",
        _ => "sRGB",
    };

    private static IColorProfile DefaultProfile(ColorTarget target) => target switch
    {
        ColorTarget.Cmyk => ColorProfiles.CoatedFOGRA39,
        ColorTarget.AdobeRgb => ColorProfiles.AdobeRGB1998,
        _ => ColorProfiles.SRGB,
    };

    public Task ConvertAsync(string source, string target, ColorTarget kind, byte[]? profile, ColorIntent intent, bool blackPointCompensation, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            if (kind == ColorTarget.Copy) { File.Copy(source, target, overwrite: true); return; }
            using var image = new MagickImage(source);
            ct.ThrowIfCancellationRequested();
            // A palette image with few colors (common for PNG) loads at 1-4 bits per channel; the converted colors need at least 8.
            image.ClassType = ClassType.Direct;
            if (image.Depth < 8) image.Depth = 8;

            var targetProfile = profile is null ? DefaultProfile(kind) : new ColorProfile(profile);
            if (kind == ColorTarget.Cmyk ? targetProfile.ColorSpace != ColorSpace.CMYK : targetProfile.ColorSpace == ColorSpace.CMYK)
                throw new InvalidOperationException($"The color profile chosen for {ColorVersions.Title(kind)} isn't a{(kind == ColorTarget.Cmyk ? " CMYK" : "n RGB")} profile. Choose another one in Settings → Colors.");

            var sourceProfile = image.GetColorProfile() ?? (image.ColorSpace == ColorSpace.CMYK ? ColorProfiles.CoatedFOGRA39 : ColorProfiles.SRGB);
            if (kind == ColorTarget.Cmyk && image.HasAlpha)
            {
                image.BackgroundColor = MagickColors.White;
                image.Alpha(AlphaAction.Remove);
            }
            image.RenderingIntent = intent == ColorIntent.Perceptual ? RenderingIntent.Perceptual : RenderingIntent.Relative;
            image.BlackPointCompensation = blackPointCompensation;
            image.TransformColorSpace(sourceProfile, targetProfile, ColorTransformMode.HighRes);
            ct.ThrowIfCancellationRequested();

            var ext = Path.GetExtension(target).ToLowerInvariant();
            if (ext is ".jpg" or ".jpeg") image.Quality = 95;
            if (ext is ".tif" or ".tiff") image.Settings.Compression = CompressionMethod.LZW;
            image.Write(target);
        }, ct);

    /// <summary>
    /// A small sRGB preview of an image (for thumbnails): CMYK and wide-gamut images are converted through their
    /// embedded profile so they show their real colors. Returns false when ImageMagick can't read the file.
    /// </summary>
    public static bool TryCreatePreview(string source, string target, int size)
    {
        try
        {
            using var image = new MagickImage();
            image.Settings.SetDefine(MagickFormat.Jpeg, "size", $"{size * 2}x{size * 2}"); // decode large JPEGs at a lower size
            image.Read(source);
            image.AutoOrient();
            image.ClassType = ClassType.Direct;
            if (image.Depth < 8) image.Depth = 8;
            var profile = image.GetColorProfile();
            if (profile is not null || image.ColorSpace == ColorSpace.CMYK)
            {
                image.RenderingIntent = RenderingIntent.Perceptual;
                image.BlackPointCompensation = true;
                image.TransformColorSpace(profile ?? ColorProfiles.CoatedFOGRA39, ColorProfiles.SRGB);
            }
            image.Thumbnail(new MagickGeometry((uint)size, (uint)size) { Greater = true });
            image.Strip();
            image.Write(target, MagickFormat.Png);
            return true;
        }
        catch (MagickException) { return false; }
    }

    /// <summary>Whether a file needs a color-managed preview: it has a non-sRGB profile or is CMYK.</summary>
    public static bool NeedsColorManagedPreview(string path)
    {
        try
        {
            var info = new MagickImageInfo(path);
            if (info.ColorSpace == ColorSpace.CMYK) return true;
            using var image = new MagickImage();
            image.Ping(path);
            return image.GetColorProfile() is { } p && !(p.Description ?? "").Contains("sRGB", StringComparison.OrdinalIgnoreCase);
        }
        catch (MagickException) { return false; }
    }
}

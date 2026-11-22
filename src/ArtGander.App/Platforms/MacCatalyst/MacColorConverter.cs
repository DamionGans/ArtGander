using ArtGander.Core.Imaging;
using CoreGraphics;
using Foundation;
using ImageIO;

namespace ArtGander.App.Services;

/// <summary>
/// Color conversion with macOS's own color management (ColorSync, through Core Graphics). The picture is drawn into a
/// canvas in the target color space, which converts it through the source's embedded profile (sRGB when it has none);
/// the target profile is embedded in the new file. CMYK can't be transparent: transparent areas become white.
/// </summary>
public sealed class MacColorConverter : IColorConverter
{
    public bool IsAvailable => true;

    /// <summary>macOS brings its profiles in the ColorSync folders, which Settings lists as "On this computer".</summary>
    public IReadOnlyList<IccProfileInfo> BuiltInProfiles => [];

    public string DefaultProfileName(ColorTarget target) => target switch
    {
        ColorTarget.Cmyk => "Generic CMYK (macOS)",
        ColorTarget.AdobeRgb => "Adobe RGB (1998)",
        _ => "sRGB",
    };

    public Task ConvertAsync(string source, string target, ColorTarget kind, byte[]? profile, ColorIntent intent, bool blackPointCompensation, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            if (kind == ColorTarget.Copy) { File.Copy(source, target, overwrite: true); return; }
            using var space = profile is null ? DefaultSpace(kind) : CGColorSpace.CreateIccData(NSData.FromArray(profile));
            if (space is null) throw new InvalidOperationException("This color profile can't be used. Choose another one in Settings → Colors.");
            var isCmyk = space.Model == CGColorSpaceModel.Cmyk;
            if (isCmyk != (kind == ColorTarget.Cmyk))
                throw new InvalidOperationException($"The color profile chosen for {ColorVersions.Title(kind)} isn't a{(kind == ColorTarget.Cmyk ? " CMYK" : "n RGB")} profile. Choose another one in Settings → Colors.");

            using var input = CGImageSource.FromUrl(NSUrl.FromFilename(source)) ?? throw new InvalidOperationException($"Can't read {Path.GetFileName(source)}.");
            using var image = input.CreateImage(0, (CGImageOptions?)null) ?? throw new InvalidOperationException($"Can't read {Path.GetFileName(source)}.");
            ct.ThrowIfCancellationRequested();

            var width = (nint)image.Width;
            var height = (nint)image.Height;
            var components = isCmyk ? 4 : 4; // CMYK: 4 inks; RGB: red, green, blue + alpha
            using var context = new CGBitmapContext(IntPtr.Zero, width, height, 8, width * components, space,
                isCmyk ? CGImageAlphaInfo.None : CGImageAlphaInfo.PremultipliedLast);
            context.SetRenderingIntent(intent == ColorIntent.Perceptual ? CGColorRenderingIntent.Perceptual : CGColorRenderingIntent.RelativeColorimetric);
            context.InterpolationQuality = CGInterpolationQuality.High;
            var rect = new CGRect(0, 0, width, height);
            if (isCmyk)
            {
                context.SetFillColor(new nfloat[] { 0, 0, 0, 0, 1 }); // white paper behind transparent parts
                context.FillRect(rect);
            }
            context.DrawImage(rect, image);
            using var converted = context.ToImage() ?? throw new InvalidOperationException("The conversion failed.");
            ct.ThrowIfCancellationRequested();

            var type = Path.GetExtension(target).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => "public.jpeg",
                ".tif" or ".tiff" => "public.tiff",
                ".webp" => "org.webmproject.webp",
                _ => "public.png",
            };
            using var output = CGImageDestination.Create(NSUrl.FromFilename(target), type, 1)
                               ?? throw new InvalidOperationException($"Can't write {Path.GetFileName(target)} in this format.");
            var options = new CGImageDestinationOptions { LossyCompressionQuality = 0.95f };
            output.AddImage(converted, options);
            if (!output.Close()) throw new IOException($"Couldn't save {Path.GetFileName(target)}.");
        }, ct);

    private static CGColorSpace? DefaultSpace(ColorTarget target) => target switch
    {
        ColorTarget.Cmyk => CGColorSpace.CreateWithName(CGColorSpaceNames.GenericCmyk),
        ColorTarget.AdobeRgb => CGColorSpace.CreateWithName(CGColorSpaceNames.AdobeRgb1998),
        _ => CGColorSpace.CreateWithName(CGColorSpaceNames.Srgb),
    };
}

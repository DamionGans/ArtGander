using ArtGander.Core.Imaging;
using ArtGander.Core.Model;
using ArtGander.Imaging;
using ImageMagick;

namespace ArtGander.Core.Tests;

public class ColorVersionTests
{
    private static string MakePicture(TempLibrary lib, string name, bool transparent = false)
    {
        var path = Path.Combine(lib.Root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var image = new MagickImage(transparent ? MagickColors.Transparent : new MagickColor("#3A7D44"), 40, 30);
        image.Draw(new ImageMagick.Drawing.Drawables().FillColor(new MagickColor("#E8B04B")).Rectangle(5, 5, 20, 20));
        image.Write(path);
        return path;
    }

    [Fact]
    public async Task Makes_a_CMYK_version_with_the_profile_embedded()
    {
        using var lib = new TempLibrary();
        var cover = MakePicture(lib, "Goose/Goose.png", transparent: true);
        var target = Path.Combine(lib.Root, "Goose", "Goose-cmyk" + ColorVersions.OutputExtension(".png", ColorTarget.Cmyk));
        Assert.EndsWith(".tif", target);

        await new MagickColorConverter().ConvertAsync(cover, target, ColorTarget.Cmyk, null, ColorIntent.RelativeColorimetric, true, TestContext.Current.CancellationToken);

        using var result = new MagickImage(target);
        Assert.Equal(ColorSpace.CMYK, result.ColorSpace);
        Assert.Contains("FOGRA39", result.GetColorProfile()!.Description);
        Assert.False(result.HasAlpha); // CMYK can't be transparent: white instead

        // An opaque picture keeps its colors: the green background has lots of cyan and yellow.
        var solid = MakePicture(lib, "Goose/Goose-solid.png");
        var solidCmyk = Path.Combine(lib.Root, "Goose", "Goose-solid-cmyk.tif");
        await new MagickColorConverter().ConvertAsync(solid, solidCmyk, ColorTarget.Cmyk, null, ColorIntent.RelativeColorimetric, true, TestContext.Current.CancellationToken);
        using var green = new MagickImage(solidCmyk);
        var px = green.GetPixels().GetPixel(35, 25).ToColor()!;
        Assert.True(px.R > 30000 && px.B > 30000, $"expected cyan and yellow, got {px}");
        Assert.True(File.Exists(cover));
    }

    [Fact]
    public async Task Makes_Adobe_RGB_and_sRGB_versions_and_plain_copies()
    {
        using var lib = new TempLibrary();
        var cover = MakePicture(lib, "Goose.jpg");
        var converter = new MagickColorConverter();
        var adobe = Path.Combine(lib.Root, "Goose-adobe-rgb.jpg");
        await converter.ConvertAsync(cover, adobe, ColorTarget.AdobeRgb, null, ColorIntent.Perceptual, true, TestContext.Current.CancellationToken);
        using (var a = new MagickImage(adobe)) Assert.Contains("Adobe RGB", a.GetColorProfile()!.Description);

        var srgb = Path.Combine(lib.Root, "Goose-srgb.jpg");
        await converter.ConvertAsync(adobe, srgb, ColorTarget.Srgb, null, ColorIntent.Perceptual, true, TestContext.Current.CancellationToken);
        using (var s = new MagickImage(srgb)) Assert.Contains("sRGB", s.GetColorProfile()!.Description);

        var copy = Path.Combine(lib.Root, "Goose-copy.jpg");
        await converter.ConvertAsync(cover, copy, ColorTarget.Copy, null, ColorIntent.Perceptual, true, TestContext.Current.CancellationToken);
        Assert.Equal(File.ReadAllBytes(cover), File.ReadAllBytes(copy));
    }

    [Fact]
    public async Task A_profile_of_the_wrong_kind_is_refused()
    {
        using var lib = new TempLibrary();
        var cover = MakePicture(lib, "Goose.png");
        var rgbProfile = ColorProfiles.SRGB.ToByteArray();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new MagickColorConverter().ConvertAsync(
            cover, Path.Combine(lib.Root, "x.tif"), ColorTarget.Cmyk, rgbProfile, ColorIntent.Perceptual, true, TestContext.Current.CancellationToken));
        Assert.Contains("isn't a CMYK profile", ex.Message);
    }

    [Fact]
    public void Profiles_are_read_and_stored_in_the_library_once()
    {
        using var lib = new TempLibrary();
        var builtIn = new MagickColorConverter().BuiltInProfiles;
        var fogra = Assert.Single(builtIn, p => p.Name.Contains("FOGRA39"));
        Assert.Equal("CMYK", fogra.ColorSpace);
        Assert.Contains(builtIn, p => p.ColorSpace == "RGB" && p.Name.Contains("Adobe RGB"));

        var name = ColorVersions.StoreProfile(lib.Root, fogra);
        Assert.Equal(name, ColorVersions.StoreProfile(lib.Root, fogra)); // the same profile isn't stored twice
        var settings = new ColorSettings { CmykProfile = name };
        Assert.Equal(fogra.Data, ColorVersions.ProfileData(lib.Root, settings, ColorTarget.Cmyk));
        Assert.Null(ColorVersions.ProfileData(lib.Root, settings, ColorTarget.Srgb)); // not set: the default
        Assert.Contains(ColorVersions.LibraryProfiles(lib.Root), p => p.Name == fogra.Name);

        settings.CmykProfile = "gone.icc";
        Assert.Throws<FileNotFoundException>(() => ColorVersions.ProfileData(lib.Root, settings, ColorTarget.Cmyk));
        Assert.Null(Icc.Read([1, 2, 3], "x"));
    }

    [Theory]
    [InlineData(".png", ColorTarget.Cmyk, ".tif")]
    [InlineData(".JPG", ColorTarget.Cmyk, ".jpg")]
    [InlineData(".png", ColorTarget.AdobeRgb, ".png")]
    [InlineData(".psd", ColorTarget.Srgb, ".png")]
    [InlineData(".psd", ColorTarget.Copy, ".psd")]
    public void Picks_a_file_type_that_can_hold_the_colors(string source, ColorTarget target, string expected) =>
        Assert.Equal(expected, ColorVersions.OutputExtension(source, target));

    [Fact]
    public void Previews_of_CMYK_pictures_are_converted_to_screen_colors()
    {
        using var lib = new TempLibrary();
        var cover = MakePicture(lib, "Goose.png");
        var cmyk = Path.Combine(lib.Root, "Goose-cmyk.tif");
        new MagickColorConverter().ConvertAsync(cover, cmyk, ColorTarget.Cmyk, null, ColorIntent.Perceptual, true).GetAwaiter().GetResult();
        Assert.True(MagickColorConverter.NeedsColorManagedPreview(cmyk));
        Assert.False(MagickColorConverter.NeedsColorManagedPreview(cover));

        var preview = Path.Combine(lib.Root, "preview.png");
        Assert.True(MagickColorConverter.TryCreatePreview(cmyk, preview, 64));
        using var p = new MagickImage(preview);
        Assert.Equal(ColorSpace.sRGB, p.ColorSpace);
        // The green is still green on screen (CMYK shown without its profile would be far off).
        var pixel = p.GetPixels().GetPixel(35, 25).ToColor()!;
        Assert.InRange(pixel.G / 257, 100, 145);
        Assert.InRange(pixel.R / 257, 40, 80);
    }
}

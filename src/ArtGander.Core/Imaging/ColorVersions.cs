using System.Text;
using ArtGander.Core.Model;
using ArtGander.Core.Naming;
using ArtGander.Core.Schema;

namespace ArtGander.Core.Imaging;

/// <summary>What "Add new version" makes from a cover.</summary>
public enum ColorTarget { Copy, Cmyk, AdobeRgb, Srgb }

/// <summary>How colors that don't fit the target are handled.</summary>
public enum ColorIntent { Perceptual, RelativeColorimetric }

/// <summary>Converts an image to a color space, with an ICC profile (implemented per operating system).</summary>
public interface IColorConverter
{
    /// <summary>Whether conversion works on this computer.</summary>
    bool IsAvailable { get; }

    /// <summary>The profiles this converter brings along (name and ICC data), offered in Settings.</summary>
    IReadOnlyList<IccProfileInfo> BuiltInProfiles { get; }

    /// <summary>The profile used for a target when Settings doesn't name one, e.g. "Coated FOGRA39".</summary>
    string DefaultProfileName(ColorTarget target);

    /// <summary>
    /// Converts <paramref name="source"/> into <paramref name="target"/> (whose extension picks the format).
    /// <paramref name="profile"/> is the target ICC profile, or null for <see cref="DefaultProfileName"/>.
    /// A source without an embedded profile counts as sRGB.
    /// </summary>
    Task ConvertAsync(string source, string target, ColorTarget kind, byte[]? profile, ColorIntent intent, bool blackPointCompensation, CancellationToken ct = default);
}

/// <summary>An ICC profile: its name (from inside the file), its color space ("RGB" or "CMYK"), and where it comes from.</summary>
public sealed record IccProfileInfo(string Name, string ColorSpace, byte[] Data, string Source);

/// <summary>Making color versions of covers, and the ICC profiles the library uses.</summary>
public static class ColorVersions
{
    public const string ProfilesFolderName = "profiles";

    public static string Title(ColorTarget target) => target switch
    {
        ColorTarget.Cmyk => "CMYK version (for print)",
        ColorTarget.AdobeRgb => "Adobe RGB version",
        ColorTarget.Srgb => "sRGB version (for screens and the web)",
        _ => "Copy as it is",
    };

    /// <summary>The version label suggested when you leave the name empty.</summary>
    public static string SuggestedLabel(ColorTarget target) => target switch
    {
        ColorTarget.Cmyk => "cmyk",
        ColorTarget.AdobeRgb => "adobe-rgb",
        ColorTarget.Srgb => "srgb",
        _ => "copy",
    };

    /// <summary>The kind a new version gets: CMYK versions are for print.</summary>
    public static string KindFor(ColorTarget target, string label, IEnumerable<string>? libraryKinds) =>
        target == ColorTarget.Cmyk ? FileKind.Print : Services.PieceService.GuessKind(label, ".png", libraryKinds) is var k && k != FileKind.Cover ? k : FileKind.Edit;

    /// <summary>
    /// The file type of the new version: a copy keeps it; CMYK needs TIFF (or stays JPEG); RGB versions keep the
    /// type when it can hold the color profile (PNG, JPEG, TIFF, WebP) and become PNG otherwise.
    /// </summary>
    public static string OutputExtension(string sourceExtension, ColorTarget target)
    {
        var ext = sourceExtension.ToLowerInvariant();
        return target switch
        {
            ColorTarget.Copy => ext,
            ColorTarget.Cmyk => ext is ".jpg" or ".jpeg" or ".tif" or ".tiff" ? ext : ".tif",
            _ => ext is ".png" or ".jpg" or ".jpeg" or ".tif" or ".tiff" or ".webp" ? ext : ".png",
        };
    }

    public static string ColorSpaceOf(ColorTarget target) => target == ColorTarget.Cmyk ? "CMYK" : "RGB";

    // ---- The library's profiles -------------------------------------------------------------------

    /// <summary><c>.artgander/profiles</c>: the ICC profiles chosen in Settings, so every computer uses the very same.</summary>
    public static string ProfilesFolder(string rootPath) => Path.Combine(rootPath, CurrentSchema.LibraryFolderName, ProfilesFolderName);

    /// <summary>The profile file name set for a target (empty: the converter's default).</summary>
    public static string SettingFor(ColorSettings settings, ColorTarget target) => target switch
    {
        ColorTarget.Cmyk => settings.CmykProfile,
        ColorTarget.AdobeRgb => settings.AdobeRgbProfile,
        ColorTarget.Srgb => settings.SrgbProfile,
        _ => "",
    };

    /// <summary>The ICC data of the profile set for a target, or null for the converter's default.</summary>
    public static byte[]? ProfileData(string rootPath, ColorSettings settings, ColorTarget target)
    {
        var name = SettingFor(settings, target);
        if (string.IsNullOrWhiteSpace(name)) return null;
        var path = Path.Combine(ProfilesFolder(rootPath), Path.GetFileName(name));
        return File.Exists(path) ? File.ReadAllBytes(path) : throw new FileNotFoundException(
            $"The color profile \"{name}\" set in Settings → Colors is missing from {ProfilesFolder(rootPath)}. Choose it again in Settings.", path);
    }

    /// <summary>Stores a profile in the library (<c>.artgander/profiles/{name}.icc</c>) and returns its file name.</summary>
    public static string StoreProfile(string rootPath, IccProfileInfo profile)
    {
        var folder = ProfilesFolder(rootPath);
        Directory.CreateDirectory(folder);
        var baseName = NameRules.ToFolderName(profile.Name.Length > 0 ? profile.Name : "profile");
        var ext = ".icc";
        var name = baseName + ext;
        for (var i = 2; File.Exists(Path.Combine(folder, name)) && !File.ReadAllBytes(Path.Combine(folder, name)).AsSpan().SequenceEqual(profile.Data); i++)
            name = $"{baseName} ({i}){ext}";
        var path = Path.Combine(folder, name);
        if (!File.Exists(path)) File.WriteAllBytes(path, profile.Data);
        return name;
    }

    /// <summary>The profiles stored in the library.</summary>
    public static IReadOnlyList<IccProfileInfo> LibraryProfiles(string rootPath) => ReadFolder(ProfilesFolder(rootPath), "In the art folder");

    /// <summary>The color profiles installed on this computer (Windows color folder, macOS ColorSync folders).</summary>
    public static IReadOnlyList<IccProfileInfo> SystemProfiles()
    {
        var folders = OperatingSystem.IsWindows()
            ? [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "spool", "drivers", "color")]
            : new[]
            {
                "/System/Library/ColorSync/Profiles", "/Library/ColorSync/Profiles",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library/ColorSync/Profiles"),
            };
        return folders.SelectMany(f => ReadFolder(f, "On this computer")).ToList();
    }

    private static List<IccProfileInfo> ReadFolder(string folder, string source)
    {
        var result = new List<IccProfileInfo>();
        if (!Directory.Exists(folder)) return result;
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(folder).Where(f => Path.GetExtension(f).ToLowerInvariant() is ".icc" or ".icm").ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return result; }
        foreach (var file in files)
        {
            try
            {
                var data = File.ReadAllBytes(file);
                if (Icc.Read(data, Path.GetFileNameWithoutExtension(file)) is { } info) result.Add(info with { Source = source });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return result;
    }
}

/// <summary>Reads the little that's needed from an ICC profile: its color space and its description.</summary>
public static class Icc
{
    public static IccProfileInfo? Read(byte[] data, string fallbackName, string source = "")
    {
        if (data.Length < 132 || Encoding.ASCII.GetString(data, 36, 4) != "acsp") return null;
        var space = Encoding.ASCII.GetString(data, 16, 4).Trim();
        if (space is not ("RGB" or "CMYK")) return null;
        return new IccProfileInfo(Description(data) is { Length: > 0 } d ? d : fallbackName, space, data, source);
    }

    /// <summary>The 'desc' tag: ASCII in version 2 profiles, UTF-16 ('mluc') in version 4.</summary>
    private static string? Description(byte[] d)
    {
        var count = BigEndian(d, 128);
        for (var i = 0; i < count && 132 + i * 12 + 12 <= d.Length; i++)
        {
            var at = 132 + i * 12;
            if (Encoding.ASCII.GetString(d, at, 4) != "desc") continue;
            var offset = BigEndian(d, at + 4);
            var size = BigEndian(d, at + 8);
            if (offset < 0 || size < 12 || offset + size > d.Length) return null;
            var type = Encoding.ASCII.GetString(d, offset, 4);
            if (type == "desc")
            {
                var length = BigEndian(d, offset + 8);
                if (length <= 0 || offset + 12 + length > d.Length) return null;
                return Encoding.ASCII.GetString(d, offset + 12, length).TrimEnd('\0').Trim();
            }
            if (type == "mluc")
            {
                var records = BigEndian(d, offset + 8);
                if (records <= 0) return null;
                var len = BigEndian(d, offset + 20);
                var start = BigEndian(d, offset + 24);
                if (offset + start + len > d.Length || len <= 0) return null;
                return Encoding.BigEndianUnicode.GetString(d, offset + start, len).TrimEnd('\0').Trim();
            }
            return null;
        }
        return null;
    }

    private static int BigEndian(byte[] d, int at) => at + 4 > d.Length ? 0 : (d[at] << 24) | (d[at + 1] << 16) | (d[at + 2] << 8) | d[at + 3];
}

using ArtGander.Core.Naming;

namespace ArtGander.Core.Tests;

public class NamingTests
{
    [Theory]
    [InlineData("Goose On The Loose", "Goose On The Loose")]
    [InlineData("  Goose: On the Loose?  ", "Goose On the Loose")]
    [InlineData("Pond / Lake \\ Sea", "Pond Lake Sea")]
    [InlineData("Trailing dots...", "Trailing dots")]
    [InlineData("under_scores", "under scores")]
    [InlineData("...hidden", "hidden")]
    [InlineData("CON", "CON Art")]
    [InlineData("", "Untitled")]
    [InlineData("Café Crème", "Café Crème")]
    public void Folder_names_are_cross_platform_safe(string title, string expected) =>
        Assert.Equal(expected, NameRules.ToFolderName(title));

    [Fact]
    public void Decomposed_unicode_is_normalized()
    {
        var nfd = "Café"; // as a macOS SMB client may write it
        Assert.Equal("Café", NameRules.ToFolderName(nfd));
        Assert.True(NameRules.NamesEqual(nfd, "CAFÉ"));
    }

    [Theory]
    [InlineData("Transparent", "transparent")]
    [InlineData("Print A4 (v2)", "print-a4-v2")]
    [InlineData("--", "file")]
    public void Version_labels_are_normalized(string input, string expected) =>
        Assert.Equal(expected, NameRules.ToVersionLabel(input));

    [Fact]
    public void Builds_convention_file_name() =>
        Assert.Equal("Goose_On_The_Loose-transparent.png", FileNamer.Build("Goose On The Loose", "Transparent", ".PNG"));

    [Theory]
    [InlineData("Goose_On_The_Loose-print-a4.pdf", true, "print-a4")]
    [InlineData("goose_on_the_loose-master.tif", true, "master")]
    [InlineData("IMG_1234.jpg", false, "")]
    [InlineData("Goose_On_The_Loose-Bad Label.jpg", false, "")]
    public void Parses_version_from_file_name(string fileName, bool ok, string version)
    {
        Assert.Equal(ok, FileNamer.TryParseVersion("Goose On The Loose", fileName, out var v));
        Assert.Equal(version, v);
    }

    [Fact]
    public void Convention_requires_exact_case_and_lowercase_extension()
    {
        Assert.True(FileNamer.FollowsConvention("Goose", "Goose-master.tif"));
        Assert.False(FileNamer.FollowsConvention("Goose", "Goose-master.TIF"));
        Assert.False(FileNamer.FollowsConvention("Goose", "goose-master.tif"));
    }

    [Theory]
    [InlineData("Goose On The Loose - Transparent.png", "transparent")]
    [InlineData("Goose On The Loose.jpg", "original")]
    [InlineData("IMG_1234.jpg", "img-1234")]
    public void Suggests_versions_for_foreign_names(string fileName, string expected) =>
        Assert.Equal(expected, FileNamer.SuggestVersion("Goose On The Loose", fileName));

    [Fact]
    public void Makes_unique_versions()
    {
        var taken = new HashSet<string> { "Goose-print.png", "Goose-print-2.png" };
        Assert.Equal("print-3", FileNamer.MakeUnique("Goose", "print", ".png", taken.Contains));
    }
}

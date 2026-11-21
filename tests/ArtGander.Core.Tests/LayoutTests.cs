using ArtGander.Core.Library;

namespace ArtGander.Core.Tests;

public class LayoutTests
{
    [Fact]
    public void Reads_png_and_jpeg_sizes_from_the_header()
    {
        byte[] png = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R',
                      0, 0, 0x01, 0x2C, 0, 0, 0, 0xC8, 8, 6, 0, 0, 0];
        Assert.Equal((300, 200), ImageHeader.TryGetSize(png));

        // SOI, an APP0 segment, then SOF0 with height 480 and width 640.
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x04, 0x00, 0x00, 0xFF, 0xC0, 0x00, 0x11, 0x08, 0x01, 0xE0, 0x02, 0x80, 0x03, 0, 0, 0, 0, 0, 0];
        Assert.Equal((640, 480), ImageHeader.TryGetSize(jpeg));

        Assert.Null(ImageHeader.TryGetSize("not an image"u8));
    }

    [Fact]
    public void Justified_rows_fill_the_width_and_keep_shapes()
    {
        var rows = JustifiedLayout.Rows([1.0, 1.0, 1.0, 2.0], availableWidth: 610, targetHeight: 200, spacing: 10);

        Assert.Equal(2, rows.Count);
        var first = rows[0];
        Assert.Equal(3, first.Count);
        Assert.Equal(610, first.Sum(c => c.Width) + 2 * 10); // exactly fills the row
        Assert.All(first, c => Assert.Equal(first[0].Height, c.Height));
        Assert.True(first[0].Height < 200); // scaled down a little to fit

        var last = Assert.Single(rows[1]);
        Assert.Equal(400, last.Width); // the last row isn't stretched
        Assert.Equal(200, last.Height);
    }
}

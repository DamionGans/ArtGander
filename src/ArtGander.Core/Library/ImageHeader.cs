using System.Buffers.Binary;

namespace ArtGander.Core.Library;

/// <summary>
/// Reads an image's pixel size from its file header (PNG, JPEG, GIF, BMP, WebP) without decoding the image.
/// Used to lay out the library's raster view, where every tile keeps its image's own shape.
/// </summary>
public static class ImageHeader
{
    public static (int Width, int Height)? TryGetSize(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var head = new byte[Math.Min(fs.Length, 64 * 1024)];
            var read = fs.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            return TryGetSize(head.AsSpan(0, read));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static (int Width, int Height)? TryGetSize(ReadOnlySpan<byte> b)
    {
        // PNG: signature, then the IHDR chunk with width and height.
        if (b.Length >= 24 && b[0] == 0x89 && b[1] == 'P' && b[2] == 'N' && b[3] == 'G')
            return Valid(BinaryPrimitives.ReadInt32BigEndian(b[16..]), BinaryPrimitives.ReadInt32BigEndian(b[20..]));

        // GIF
        if (b.Length >= 10 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F')
            return Valid(BinaryPrimitives.ReadUInt16LittleEndian(b[6..]), BinaryPrimitives.ReadUInt16LittleEndian(b[8..]));

        // BMP (height may be negative for top-down bitmaps)
        if (b.Length >= 26 && b[0] == 'B' && b[1] == 'M')
            return Valid(BinaryPrimitives.ReadInt32LittleEndian(b[18..]), Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(b[22..])));

        // WebP
        if (b.Length >= 30 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P')
        {
            if (b[12] == 'V' && b[13] == 'P' && b[14] == '8' && b[15] == ' ')
                return Valid(BinaryPrimitives.ReadUInt16LittleEndian(b[26..]) & 0x3FFF, BinaryPrimitives.ReadUInt16LittleEndian(b[28..]) & 0x3FFF);
            if (b[12] == 'V' && b[13] == 'P' && b[14] == '8' && b[15] == 'L' && b.Length >= 25)
            {
                var bits = BinaryPrimitives.ReadUInt32LittleEndian(b[21..]);
                return Valid((int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1);
            }
            if (b[12] == 'V' && b[13] == 'P' && b[14] == '8' && b[15] == 'X')
                return Valid((b[24] | b[25] << 8 | b[26] << 16) + 1, (b[27] | b[28] << 8 | b[29] << 16) + 1);
            return null;
        }

        // JPEG: walk the markers to the first start-of-frame.
        if (b.Length >= 4 && b[0] == 0xFF && b[1] == 0xD8)
        {
            var i = 2;
            while (i + 9 < b.Length)
            {
                if (b[i] != 0xFF) { i++; continue; }
                var marker = b[i + 1];
                if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7)) { i += 2; continue; }
                if (marker == 0xFF) { i++; continue; }
                var length = BinaryPrimitives.ReadUInt16BigEndian(b[(i + 2)..]);
                var isFrame = marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC;
                if (isFrame)
                    return Valid(BinaryPrimitives.ReadUInt16BigEndian(b[(i + 7)..]), BinaryPrimitives.ReadUInt16BigEndian(b[(i + 5)..]));
                i += 2 + length;
            }
        }
        return null;
    }

    private static (int, int)? Valid(int w, int h) => w > 0 && h > 0 && w < 100_000 && h < 100_000 ? (w, h) : null;
}

/// <summary>
/// Packs items of known aspect ratios into rows that fill the available width exactly, like an image search
/// results page. Every row keeps roughly <c>targetHeight</c>; the last, incomplete row isn't stretched.
/// </summary>
public static class JustifiedLayout
{
    public sealed record Cell(int Index, double Width, double Height);

    public static List<List<Cell>> Rows(IReadOnlyList<double> aspects, double availableWidth, double targetHeight, double spacing)
    {
        var rows = new List<List<Cell>>();
        if (availableWidth <= 0 || targetHeight <= 0) return rows;
        var start = 0;
        while (start < aspects.Count)
        {
            double widths = 0;
            var end = start;
            // Add items until the row at target height is at least as wide as the space.
            while (end < aspects.Count)
            {
                widths += targetHeight * Clamp(aspects[end]);
                end++;
                if (widths + spacing * (end - start - 1) >= availableWidth) break;
            }
            var count = end - start;
            var full = widths + spacing * (count - 1) >= availableWidth;
            var scale = full ? (availableWidth - spacing * (count - 1)) / widths : 1.0;
            var height = Math.Floor(targetHeight * scale);
            var row = new List<Cell>(count);
            double used = 0;
            for (var i = start; i < end; i++)
            {
                var w = Math.Floor(height * Clamp(aspects[i]));
                if (full && i == end - 1) w = Math.Max(1, Math.Floor(availableWidth - spacing * (count - 1) - used)); // absorb rounding
                used += w;
                row.Add(new Cell(i, w, height));
            }
            rows.Add(row);
            start = end;
        }
        return rows;
    }

    /// <summary>Very wide or very tall images would make unusable tiles; keep them within reason.</summary>
    private static double Clamp(double aspect) => double.IsFinite(aspect) && aspect > 0 ? Math.Clamp(aspect, 0.4, 3.0) : 1.0;
}

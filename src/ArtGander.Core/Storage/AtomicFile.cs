using System.Security.Cryptography;
using System.Text;

namespace ArtGander.Core.Storage;

/// <summary>
/// Writes files in a crash-safe and network-share-safe way.
/// The content goes to a temporary file in the <b>same folder</b> first, which is then renamed over the target.
/// A rename within one folder is atomic on NTFS, APFS and SMB, so other computers never see a half-written file.
/// No file locks are held between operations.
/// </summary>
public static class AtomicFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static void WriteAllText(string path, string content)
    {
        var dir = Path.GetDirectoryName(path)!;
        var tmp = Path.Combine(dir, $".{Path.GetFileName(path)}.tmp-{Guid.NewGuid():N}");
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                var bytes = Utf8NoBom.GetBytes(content);
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
            if (Path.GetFileName(path).StartsWith('.')) MarkHidden(path);
        }
        finally
        {
            if (File.Exists(tmp)) TryDelete(tmp);
        }
    }

    /// <summary>Reads a file while letting others read and write it. Retries briefly, because network shares can report transient sharing violations.</summary>
    public static byte[] ReadAllBytes(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var ms = new MemoryStream();
                fs.CopyTo(ms);
                return ms.ToArray();
            }
            catch (IOException) when (attempt < 4 && File.Exists(path))
            {
                Thread.Sleep(50 * (attempt + 1));
            }
        }
    }

    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    public static string DecodeUtf8(byte[] bytes) =>
        bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? Utf8NoBom.GetString(bytes, 3, bytes.Length - 3) : Utf8NoBom.GetString(bytes);

    /// <summary>
    /// Hides a file or folder whose name starts with a dot. macOS and Linux already hide such names; Windows needs
    /// the Hidden attribute. Rewriting a file from a Mac can drop that attribute, so ArtGander calls this again on load.
    /// </summary>
    public static void MarkHidden(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var attributes = File.GetAttributes(path);
            if (!attributes.HasFlag(FileAttributes.Hidden)) File.SetAttributes(path, attributes | FileAttributes.Hidden);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* cosmetic only */ }
    }

    internal static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Checks whether a file is one of our own temporary files, which UIs and scanners should ignore.</summary>
    public static bool IsTempFile(string fileName) => fileName.StartsWith('.') && fileName.Contains(".tmp-", StringComparison.Ordinal);
}

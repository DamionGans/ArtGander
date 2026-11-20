using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace ArtGander.App.Services;

/// <summary>
/// Creates and caches small preview images. The cache lives in the app's local cache folder and never in the
/// art folder, so it doesn't clutter the library and isn't synced over the network.
/// </summary>
public sealed class ThumbnailService(IPlatformServices platform)
{
    private const int Size = 320;
    private static readonly HashSet<string> DirectlyLoadable = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" };

    private readonly string _cacheDir = Path.Combine(FileSystem.CacheDirectory, "thumbnails");
    private readonly ConcurrentDictionary<string, Task<string?>> _inflight = new();
    private readonly SemaphoreSlim _gate = new(4);

    /// <summary>Returns a path to a small image for <paramref name="sourcePath"/>, or null when no preview is possible.</summary>
    public Task<string?> GetAsync(string? sourcePath)
    {
        if (string.IsNullOrEmpty(sourcePath)) return Task.FromResult<string?>(null);
        string key;
        try
        {
            var info = new FileInfo(sourcePath);
            if (!info.Exists) return Task.FromResult<string?>(null);
            key = Key(sourcePath, info.LastWriteTimeUtc, info.Length);
        }
        catch (IOException) { return Task.FromResult<string?>(null); }

        return _inflight.GetOrAdd(key, k => Task.Run(() => CreateAsync(sourcePath, k)));
    }

    private async Task<string?> CreateAsync(string sourcePath, string key)
    {
        var target = Path.Combine(_cacheDir, key + ".img");
        if (File.Exists(target)) return target;

        await _gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(_cacheDir);
            if (await platform.CreateThumbnailAsync(sourcePath, target, Size)) return target;
        }
        catch (Exception) { /* fall through */ }
        finally { _gate.Release(); }

        // The OS thumbnailer couldn't make one (e.g. an offline network share). Use the image itself when it can be shown.
        return DirectlyLoadable.Contains(Path.GetExtension(sourcePath)) ? sourcePath : null;
    }

    private static string Key(string path, DateTime lastWrite, long length) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes($"{path}|{lastWrite.Ticks}|{length}|{Size}")));
}

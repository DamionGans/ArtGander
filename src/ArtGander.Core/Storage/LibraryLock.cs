using System.Text.Json;
using System.Text.Json.Nodes;
using ArtGander.Core.Schema;

namespace ArtGander.Core.Storage;

/// <summary>
/// A lock for library-wide work (syncing with Printify/Shopify, upgrading the library) so two computers sharing the
/// art folder never do it at the same time. It's a small file, <c>.artgander/sync.lock</c>, created exclusively.
/// A lock whose heartbeat is older than <see cref="StaleAfter"/> is assumed abandoned (e.g. a crash) and taken over.
/// </summary>
public sealed class LibraryLock : IDisposable
{
    public const string FileName = "sync.lock";
    public static TimeSpan StaleAfter { get; set; } = TimeSpan.FromMinutes(10);

    private readonly string _path;
    private readonly string _token;
    private bool _released;

    private LibraryLock(string path, string token, string purpose)
    {
        _path = path;
        _token = token;
        Purpose = purpose;
    }

    public string Purpose { get; }

    public static string PathFor(string rootPath) => Path.Combine(rootPath, CurrentSchema.LibraryFolderName, FileName);

    /// <summary>Takes the lock.</summary>
    /// <exception cref="LibraryBusyException">Another computer (or another ArtGander window) holds it.</exception>
    public static LibraryLock Acquire(string rootPath, string purpose, string? machineName = null, DateTimeOffset? now = null)
    {
        var path = PathFor(rootPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var token = Guid.NewGuid().ToString("N");
        var machine = machineName ?? Environment.MachineName;
        var time = now ?? DateTimeOffset.Now;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                {
                    fs.Write(System.Text.Encoding.UTF8.GetBytes(Content(token, machine, purpose, time, time).ToJsonString()));
                }
                AtomicFile.MarkHidden(path);
                return new LibraryLock(path, token, purpose);
            }
            catch (IOException) when (File.Exists(path))
            {
                var holder = ReadHolder(path);
                if (holder is not null && time - holder.Value.Heartbeat < StaleAfter)
                    throw new LibraryBusyException(holder.Value.Machine, holder.Value.Purpose, holder.Value.Started);
                // Stale or unreadable: remove it and try again.
                AtomicFile.TryDelete(path);
            }
        }
        throw new LibraryBusyException("another computer", purpose, time);
    }

    /// <summary>Who holds the lock right now, or null when nobody does.</summary>
    public static (string Machine, string Purpose, DateTimeOffset Started, DateTimeOffset Heartbeat)? ReadHolder(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var node = JsonNode.Parse(AtomicFile.DecodeUtf8(AtomicFile.ReadAllBytes(path)));
            return (
                node?["machine"]?.GetValue<string>() ?? "?",
                node?["purpose"]?.GetValue<string>() ?? "",
                node?["started"]?.GetValue<DateTimeOffset>() ?? DateTimeOffset.MinValue,
                node?["heartbeat"]?.GetValue<DateTimeOffset>() ?? DateTimeOffset.MinValue);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Tells other computers that this lock is still in use. Call it regularly during long work.</summary>
    public void Heartbeat()
    {
        if (_released || !IsStillMine()) return;
        var holder = ReadHolder(_path);
        AtomicFile.WriteAllText(_path, Content(_token, holder?.Machine ?? Environment.MachineName, Purpose,
            holder?.Started ?? DateTimeOffset.Now, DateTimeOffset.Now).ToJsonString());
    }

    /// <summary>False when the lock was taken over because it looked abandoned.</summary>
    public bool IsStillMine()
    {
        try
        {
            return File.Exists(_path) && JsonNode.Parse(AtomicFile.DecodeUtf8(AtomicFile.ReadAllBytes(_path)))?["token"]?.GetValue<string>() == _token;
        }
        catch (Exception ex) when (ex is IOException or JsonException) { return false; }
    }

    public void Dispose()
    {
        if (_released) return;
        _released = true;
        if (IsStillMine()) AtomicFile.TryDelete(_path);
    }

    private static JsonObject Content(string token, string machine, string purpose, DateTimeOffset started, DateTimeOffset heartbeat) => new()
    {
        ["token"] = token,
        ["machine"] = machine,
        ["purpose"] = purpose,
        ["started"] = started,
        ["heartbeat"] = heartbeat,
    };
}

public sealed class LibraryBusyException(string machine, string purpose, DateTimeOffset since)
    : Exception($"{machine} is busy with \"{purpose}\" in this art folder (since {since:t}). Please try again when it's done.")
{
    public string Machine { get; } = machine;
}

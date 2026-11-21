namespace ArtGander.Core.Library;

/// <summary>
/// Raises <see cref="Changed"/> when something in the art folder changes.
/// It combines <see cref="FileSystemWatcher"/>, which is fast on local disks, with periodic polling,
/// because change notifications on SMB/NAS shares are unreliable, especially for changes made by another computer.
/// </summary>
public sealed class LibraryWatcher : IDisposable
{
    private readonly string _root;
    private readonly FileSystemWatcher? _fsw;
    private readonly Timer _pollTimer;
    private readonly Timer _debounce;
    private string _fingerprint;
    private int _suspended;

    public LibraryWatcher(string rootPath, TimeSpan? pollInterval = null)
    {
        _root = rootPath;
        _fingerprint = SafeFingerprint();
        _debounce = new Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);
        var interval = pollInterval ?? TimeSpan.FromSeconds(20);
        _pollTimer = new Timer(_ => Poll(), null, interval, interval);

        try
        {
            _fsw = new FileSystemWatcher(rootPath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };
            _fsw.Changed += OnFsEvent;
            _fsw.Created += OnFsEvent;
            _fsw.Deleted += OnFsEvent;
            _fsw.Renamed += OnFsEvent;
            _fsw.Error += (_, _) => Trigger();
            _fsw.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            _fsw = null; // fall back to polling only
        }
    }

    /// <summary>Project folders whose piece subfolders are polled too (set after each scan).</summary>
    public IReadOnlyList<string> ProjectFolders { get; set; } = [];

    /// <summary>Raised on a thread-pool thread, debounced, when the library may have changed.</summary>
    public event EventHandler? Changed;

    /// <summary>Suppresses events while ArtGander itself writes files. Dispose the result to resume.</summary>
    public IDisposable Suspend()
    {
        Interlocked.Increment(ref _suspended);
        return new Resumer(this);
    }

    private void OnFsEvent(object sender, FileSystemEventArgs e)
    {
        if (Path.GetFileName(e.FullPath).Contains(".tmp-", StringComparison.Ordinal)) return;
        Trigger();
    }

    private void Trigger()
    {
        if (Volatile.Read(ref _suspended) > 0) return;
        _debounce.Change(TimeSpan.FromMilliseconds(750), Timeout.InfiniteTimeSpan);
    }

    private void Poll()
    {
        if (Volatile.Read(ref _suspended) > 0) return;
        var fp = SafeFingerprint();
        if (fp != _fingerprint) Trigger();
    }

    private void Fire()
    {
        _fingerprint = SafeFingerprint();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private string SafeFingerprint()
    {
        try { return LibraryScanner.Fingerprint(_root, ProjectFolders); }
        catch (Exception) { return ""; }
    }

    public void Dispose()
    {
        _fsw?.Dispose();
        _pollTimer.Dispose();
        _debounce.Dispose();
    }

    private sealed class Resumer(LibraryWatcher owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            owner._fingerprint = owner.SafeFingerprint();
            Interlocked.Decrement(ref owner._suspended);
        }
    }
}

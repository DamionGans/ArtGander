namespace ArtGander.App.Services;

/// <summary>Writes unhandled exceptions to <c>{AppData}/ArtGander/crash.log</c> so problems can be diagnosed after the fact.</summary>
public static class CrashLog
{
    public static string PathName => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArtGander", "crash.log");

    [ThreadStatic] private static bool _writing;

    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Write(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Write(e.Exception); e.SetObserved(); };
        // ARTGANDER_TRACE=1 also logs every exception as it's thrown (for diagnosing hard crashes).
        if (Environment.GetEnvironmentVariable("ARTGANDER_TRACE") == "1")
            AppDomain.CurrentDomain.FirstChanceException += (_, e) => Write(e.Exception, "first chance");
    }

    public static void Write(Exception? ex, string kind = "unhandled")
    {
        if (ex is null || _writing) return;
        _writing = true;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
            File.AppendAllText(PathName, $"[{DateTimeOffset.Now:O}] {kind}: {ex}\n\n");
        }
        catch (Exception) { /* never crash while logging a crash */ }
        finally { _writing = false; }
    }
}

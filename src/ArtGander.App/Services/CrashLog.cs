namespace ArtGander.App.Services;

/// <summary>Writes unhandled exceptions to <c>{AppData}/ArtGander/crash.log</c> so problems can be diagnosed after the fact.</summary>
public static class CrashLog
{
    public static string PathName => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArtGander", "crash.log");

    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Write(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Write(e.Exception); e.SetObserved(); };
    }

    public static void Write(Exception? ex)
    {
        if (ex is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
            File.AppendAllText(PathName, $"[{DateTimeOffset.Now:O}] {ex}\n\n");
        }
        catch (Exception) { /* never crash while logging a crash */ }
    }
}

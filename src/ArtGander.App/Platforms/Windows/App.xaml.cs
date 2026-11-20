using ArtGander.App.Services;

namespace ArtGander.App.WinUI;

/// <summary>The WinUI application host.</summary>
public partial class App : MauiWinUIApplication
{
    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => CrashLog.Write(e.Exception);
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}

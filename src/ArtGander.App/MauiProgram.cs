using ArtGander.App.Services;
using ArtGander.App.ViewModels;
using ArtGander.App.Views;
using ArtGander.Core.Library;
using ArtGander.Core.Services;
using ArtGander.Core.Storage;
using Microsoft.Extensions.Logging;

namespace ArtGander.App;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        CrashLog.Install();
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        // Core: platform-neutral services.
        builder.Services.AddSingleton<ArtPieceStore>();
        builder.Services.AddSingleton<LibraryScanner>();
        builder.Services.AddSingleton<PieceService>();

        // App services.
        builder.Services.AddSingleton<IPlatformServices, PlatformServices>();
        builder.Services.AddSingleton<ThumbnailService>();
        builder.Services.AddSingleton<AppState>();
        builder.Services.AddSingleton<AppShell>();

        // Pages and view models.
        builder.Services.AddSingleton<LibraryViewModel>();
        builder.Services.AddSingleton<LibraryPage>();
        builder.Services.AddTransient<PieceDetailViewModel>();
        builder.Services.AddTransient<PieceDetailPage>();
        builder.Services.AddTransient<OrderDetailViewModel>();
        builder.Services.AddTransient<OrderDetailPage>();
        builder.Services.AddSingleton<OrdersViewModel>();
        builder.Services.AddSingleton<OrdersPage>();
        builder.Services.AddSingleton<CollectionsViewModel>();
        builder.Services.AddSingleton<CollectionsPage>();
        builder.Services.AddSingleton<SettingsViewModel>();
        builder.Services.AddSingleton<SettingsPage>();

#if WINDOWS
        // WinUI reserves 120px for a CheckBox's own (unused) text, which pushes our labels far away.
        Microsoft.Maui.Handlers.CheckBoxHandler.Mapper.AppendToMapping("CompactWidth", (handler, _) =>
        {
            handler.PlatformView.MinWidth = 0;
            handler.PlatformView.Padding = new Microsoft.UI.Xaml.Thickness(0);
        });
#endif

#if DEBUG
        builder.Logging.AddDebug();
#endif
        return builder.Build();
    }
}

using System.Collections.ObjectModel;
using System.Windows.Input;
using ArtGander.App.Views;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ArtGander.App;

public partial class AppShell : Shell
{
    public const string PieceRoute = "piece";
    public const string OrderRoute = "order";
    public const string ProductRoute = "product";

    /// <summary>The menu on the left. Each entry goes to the top of its page, whatever is open.</summary>
    public ObservableCollection<MenuEntry> MenuEntries { get; } =
    [
        new("Library", "library"), new("Products", "products"), new("Orders", "orders"),
        new("Shops", "shops"), new("Collections", "collections"), new("Settings", "settings"),
    ];

    public ICommand NavigateCommand { get; }

    public AppShell()
    {
        NavigateCommand = new Command<string>(route => _ = NavigateToMenuAsync(route));
        InitializeComponent();
#if DEBUG
        if (Environment.GetEnvironmentVariable("ARTGANDER_SELFTEST") == "nav") Loaded += async (_, _) => await NavigationSelfTestAsync();
#endif
        Routing.RegisterRoute(PieceRoute, typeof(PieceDetailPage));
        Routing.RegisterRoute(OrderRoute, typeof(OrderDetailPage));
        Routing.RegisterRoute(ProductRoute, typeof(ProductDetailPage));
    }

    private ShellSection? _popAfterNavigation;

    /// <summary>
    /// A menu click: goes to the top of that page. On the page you're already on, the open editor is closed
    /// (Shell itself ignores a click on the current page). The unsaved-changes question comes first either way.
    /// </summary>
    public async Task NavigateToMenuAsync(string route)
    {
        try
        {
            var section = CurrentItem?.CurrentItem;
            if (section?.CurrentItem?.Route == route)
            {
                if (section.Navigation.NavigationStack.Count > 1) await section.Navigation.PopToRootAsync();
            }
            else
            {
                await GoToAsync("//" + route);
            }
        }
        catch (Exception ex) { Services.CrashLog.Write(ex, "menu"); }
    }

    private static bool IsMenuClick(ShellNavigatingEventArgs args) =>
        args.Source is ShellNavigationSource.ShellItemChanged or ShellNavigationSource.ShellSectionChanged or ShellNavigationSource.ShellContentChanged;

    /// <summary>
    /// Leaving an art piece, order or product: when it has unsaved changes, asks Save / Don't save / Cancel first.
    /// A click on a menu item (Library, Products…) always goes straight to that page; the page you were on is closed,
    /// so coming back to its menu shows the list, not an old editor.
    /// </summary>
    protected override async void OnNavigating(ShellNavigatingEventArgs args)
    {
        base.OnNavigating(args);
        var leavingSection = CurrentItem?.CurrentItem;
        var menuClick = IsMenuClick(args);
        (bool Dirty, string What, Func<Task<bool>> Save, Action Confirm)? page = CurrentPage switch
        {
            PieceDetailPage { ViewModel: { IsLeaveConfirmed: false } p } => (p.HasUnsavedChanges, "this art piece", p.TrySaveAsync, () => p.IsLeaveConfirmed = true),
            OrderDetailPage { ViewModel: { IsLeaveConfirmed: false } o } => (o.HasUnsavedChanges, "this order", o.TrySaveAsync, () => o.IsLeaveConfirmed = true),
            ProductDetailPage { ViewModel: { IsLeaveConfirmed: false } pr } => (pr.HasUnsavedChanges, "this product", pr.TrySaveAsync, () => pr.IsLeaveConfirmed = true),
            _ => null,
        };

        if (page is { Dirty: true } guard)
        {
            var deferral = args.GetDeferral();
            var choice = await DisplayActionSheetAsync($"Save changes to {guard.What}?", "Cancel", null, "Save", "Don't save");
            var leave = choice switch
            {
                "Save" => await guard.Save(),
                "Don't save" => true,
                _ => false,
            };
            if (leave)
            {
                guard.Confirm();
                if (menuClick) RememberToClose(leavingSection);
            }
            else
            {
                args.Cancel();
            }
            deferral.Complete();
            return;
        }

        if (menuClick) RememberToClose(leavingSection);
    }

    private void RememberToClose(ShellSection? section)
    {
        if (section?.Navigation.NavigationStack.Count > 1) _popAfterNavigation = section;
    }

    protected override async void OnNavigated(ShellNavigatedEventArgs args)
    {
        base.OnNavigated(args);
        var route = CurrentItem?.CurrentItem?.CurrentItem?.Route ?? "";
        foreach (var entry in MenuEntries) entry.IsCurrent = entry.Route == route;
        if (_popAfterNavigation is not { } section) return;
        _popAfterNavigation = null;
        try
        {
            // The menu page you came from goes back to its list, without animation.
            if (section != CurrentItem?.CurrentItem || args.Source is not ShellNavigationSource.PopToRoot)
                await section.Navigation.PopToRootAsync(false);
        }
        catch (Exception ex) { Services.CrashLog.Write(ex, "close page"); }
    }
}

#if DEBUG
public partial class AppShell
{
    /// <summary>Debug only: opens an order, uses the menu, and logs where it ends up (ARTGANDER_SELFTEST=nav).</summary>
    private async Task NavigationSelfTestAsync()
    {
        void Log(string step) => Services.CrashLog.Write(new Exception($"{step}: page={CurrentPage?.GetType().Name} stack={CurrentItem?.CurrentItem?.Navigation.NavigationStack.Count} route={CurrentState.Location} dirty={CurrentPage switch { OrderDetailPage o => o.ViewModel.HasUnsavedChanges, PieceDetailPage pc => pc.ViewModel.HasUnsavedChanges, ProductDetailPage pr => pr.ViewModel.HasUnsavedChanges, _ => (bool?)null }} diff={Diff()}"), "selftest");
        string Diff()
        {
            if (CurrentPage is not OrderDetailPage o) return "";
            var now = ArtGander.Core.Schema.ArtGanderJson.Serialize(o.ViewModel.Order).Split('\n');
            var saved = (o.ViewModel.SavedJsonForTest ?? "").Split('\n');
            return string.Join(" | ", now.Except(saved).Take(4));
        }
        await Task.Delay(5000);
        var app = IPlatformApplication.Current!.Services.GetRequiredService<Services.AppState>();
        var order = app.Orders.FirstOrDefault();
        if (order is null) { Log("no order"); return; }
        await GoToAsync("//orders");
        await ViewModels.OrdersViewModel.OpenOrderAsync(order.Id); await Task.Delay(1500); Log("1 order open");
        NavigateCommand.Execute("orders"); await Task.Delay(1500); Log("2 menu Orders (same page)");
        await ViewModels.OrdersViewModel.OpenOrderAsync(order.Id); await Task.Delay(1500); Log("3 order open again");
        NavigateCommand.Execute("library"); await Task.Delay(1500); Log("4 menu Library");
        NavigateCommand.Execute("orders"); await Task.Delay(1500); Log("5 menu Orders (back)");
        await ViewModels.OrdersViewModel.OpenOrderAsync(order.Id); await Task.Delay(1500); Log("6 order open");
        await GoToAsync(".."); await Task.Delay(1500); Log("7 Back button");
        if (app.AllFolders.FirstOrDefault(f => f.Document is not null) is { } piece)
        {
            NavigateCommand.Execute("library"); await Task.Delay(1000);
            await ViewModels.LibraryViewModel.OpenPieceAsync(piece.FolderPath); await Task.Delay(2000); Log("8 piece open");
            if (CurrentPage is PieceDetailPage pp && pp.ViewModel.Files.FirstOrDefault(f => f.CanProtect) is { } row)
            {
                var vm = pp.ViewModel;
                vm.Tab = "files"; await Task.Delay(500);
                vm.ShowProtectOptionsCommand.Execute(row); await Task.Delay(800);
                Log($"8a options open={row.Advanced is not null} preset={row.Advanced?.Preset} passes={row.Advanced?.Passes.Count(p => p.IsOn)}");
                vm.ShowProtectOptionsCommand.Execute(row); await Task.Delay(300);
                if (Environment.GetEnvironmentVariable("MISTCOAT_EXE") is { Length: > 0 })
                {
                    var name = row.Entry.FileName;
                    vm.QuickProtectCommand.Execute(row);
                    for (var i = 0; i < 240 && !(vm.Files.FirstOrDefault(f => f.Entry.FileName == name) is { IsProtecting: false, ProtectMessage.Length: > 0 }); i++)
                        await Task.Delay(500);
                    var done = vm.Files.FirstOrDefault(f => f.Entry.FileName == name);
                    Log($"8b quick protect: {done?.ProtectMessage} copy={done?.ProtectedCopyText} files={vm.Files.Count} photographs={string.Join("|", vm.Photographs.Select(p => p.FileName + ":" + p.ProtectButtonText))}");
                }
            }
            await GoToAsync(".."); await Task.Delay(1000);
        }
        if (app.Catalog.All.FirstOrDefault() is { } product)
        {
            NavigateCommand.Execute("products"); await Task.Delay(1000);
            await ViewModels.ProductsViewModel.OpenProductAsync(product.Id); await Task.Delay(2000); Log("9 product open");
            if (CurrentPage is ProductDetailPage pd && app.AllFolders.FirstOrDefault(f => f.Document is not null && f.PreviewFilePath is not null) is { } withImage)
            {
                var vm = pd.ViewModel;
                vm.Tab = "images"; await Task.Delay(500);
                var before = vm.Images.Count;
                await vm.AddImagePathsAsync([withImage.PreviewFilePath!]); await Task.Delay(1500);
                var row = vm.Images.LastOrDefault();
                if (row is not null)
                {
                    row.SelectedPiece = row.PieceChoices.FirstOrDefault(c => c.Id == withImage.Document!.Piece.Id);
                    await vm.TrySaveAsync(); await Task.Delay(1000);
                }
                Log($"9a picture added: {before}->{vm.Images.Count} file={vm.Images.LastOrDefault()?.Image.FileName} folder={vm.FolderText} dirty={vm.HasUnsavedChanges}");
                await GoToAsync(".."); await Task.Delay(1000);
                NavigateCommand.Execute("library"); await Task.Delay(1000);
                await ViewModels.LibraryViewModel.OpenPieceAsync(withImage.FolderPath, "products"); await Task.Delay(2000);
                if (CurrentPage is PieceDetailPage pc) Log($"9b piece shows product pictures: {string.Join("|", pc.ViewModel.ProductPictures.Select(p => p.ProductName + ":" + p.Caption))}");
                await GoToAsync(".."); await Task.Delay(1000);
                NavigateCommand.Execute("products"); await Task.Delay(1000);
                await ViewModels.ProductsViewModel.OpenProductAsync(product.Id); await Task.Delay(1500);
            }
            await GoToAsync(".."); await Task.Delay(1000); Log("10 back from product");
        }
    }
}
#endif

/// <summary>One entry in the menu.</summary>
public sealed partial class MenuEntry(string title, string route) : ObservableObject
{
    public string Title { get; } = title;
    public string Route { get; } = route;
    [ObservableProperty] public partial bool IsCurrent { get; set; }
}

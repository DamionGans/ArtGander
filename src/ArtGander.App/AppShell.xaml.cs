using ArtGander.App.Views;

namespace ArtGander.App;

public partial class AppShell : Shell
{
    public const string PieceRoute = "piece";
    public const string OrderRoute = "order";
    public const string ProductRoute = "product";

    public AppShell()
    {
        InitializeComponent();
        Routing.RegisterRoute(PieceRoute, typeof(PieceDetailPage));
        Routing.RegisterRoute(OrderRoute, typeof(OrderDetailPage));
        Routing.RegisterRoute(ProductRoute, typeof(ProductDetailPage));
    }

    private ShellSection? _popAfterNavigation;

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

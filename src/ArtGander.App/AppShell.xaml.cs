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

    /// <summary>Asks before leaving a piece that has unsaved changes.</summary>
    protected override async void OnNavigating(ShellNavigatingEventArgs args)
    {
        base.OnNavigating(args);
        (bool Dirty, string What, Func<Task<bool>> Save, Action Confirm)? page = CurrentPage switch
        {
            PieceDetailPage { ViewModel: { IsLeaveConfirmed: false } p } => (p.HasUnsavedChanges, "this art piece", p.TrySaveAsync, () => p.IsLeaveConfirmed = true),
            OrderDetailPage { ViewModel: { IsLeaveConfirmed: false } o } => (o.HasUnsavedChanges, "this order", o.TrySaveAsync, () => o.IsLeaveConfirmed = true),
            ProductDetailPage { ViewModel: { IsLeaveConfirmed: false } pr } => (pr.HasUnsavedChanges, "this product", pr.TrySaveAsync, () => pr.IsLeaveConfirmed = true),
            _ => null,
        };
        if (page is not { Dirty: true } guard) return;

        var deferral = args.GetDeferral();
        var choice = await DisplayActionSheetAsync($"Save changes to {guard.What}?", "Cancel", null, "Save", "Discard");
        switch (choice)
        {
            case "Save":
                if (await guard.Save()) { guard.Confirm(); deferral.Complete(); }
                else { args.Cancel(); deferral.Complete(); }
                break;
            case "Discard":
                guard.Confirm();
                deferral.Complete();
                break;
            default:
                args.Cancel();
                deferral.Complete();
                break;
        }
    }
}

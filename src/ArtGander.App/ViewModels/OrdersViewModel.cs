using System.Collections.ObjectModel;
using ArtGander.App.Services;
using ArtGander.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ArtGander.App.ViewModels;

/// <summary>Every sale across the library, grouped into orders. Sales are entered on each art piece's Sales tab.</summary>
public sealed partial class OrdersViewModel : ObservableObject
{
    public static IReadOnlyList<string> Periods { get; } = (string[])["All time", "This year", "Last year", "Last 30 days", "Last 90 days"];
    public const string AllChannels = "All channels";

    private readonly AppState _app;

    public OrdersViewModel(AppState app)
    {
        _app = app;
        _app.LibraryChanged += (_, _) => Rebuild();
        Rebuild();
    }

    public ObservableCollection<OrderSummary> Orders { get; } = [];
    public ObservableCollection<string> ChannelFilters { get; } = [AllChannels];

    [ObservableProperty] public partial string Period { get; set; } = "This year";
    [ObservableProperty] public partial string Channel { get; set; } = AllChannels;
    [ObservableProperty] public partial string SearchText { get; set; } = "";
    [ObservableProperty] public partial SalesTotals Totals { get; set; } = new(0, 0, 0, 0, 0, 0, 0, 0);

    public string Currency => _app.Currency;

    partial void OnPeriodChanged(string value) => Rebuild();
    partial void OnChannelChanged(string value) => Rebuild();
    partial void OnSearchTextChanged(string value) => Rebuild();

    public void Rebuild()
    {
        var rows = SalesReport.Rows(_app.Documents);

        var channels = rows.Select(r => r.Sale.Channel).Concat(_app.Settings.Channels)
            .Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList();
        var selected = Channel;
        ChannelFilters.Clear();
        ChannelFilters.Add(AllChannels);
        foreach (var c in channels) ChannelFilters.Add(c);
        if (!ChannelFilters.Contains(selected)) Channel = AllChannels;

        var today = DateOnly.FromDateTime(DateTime.Today);
        var (from, to) = Period switch
        {
            "This year" => (new DateOnly(today.Year, 1, 1), DateOnly.MaxValue),
            "Last year" => (new DateOnly(today.Year - 1, 1, 1), new DateOnly(today.Year - 1, 12, 31)),
            "Last 30 days" => (today.AddDays(-30), DateOnly.MaxValue),
            "Last 90 days" => (today.AddDays(-90), DateOnly.MaxValue),
            _ => (DateOnly.MinValue, DateOnly.MaxValue),
        };
        var q = SearchText.Trim();
        var filtered = rows.Where(r =>
            r.Sale.Date >= from && r.Sale.Date <= to
            && (Channel == AllChannels || string.Equals(r.Sale.Channel, Channel, StringComparison.OrdinalIgnoreCase))
            && (q.Length == 0
                || r.PieceTitle.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                || r.Sale.OrderRef.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                || r.Sale.Customer.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                || r.ProductName.Contains(q, StringComparison.CurrentCultureIgnoreCase)));

        var details = _app.Orders.Where(o => o.Date >= from && o.Date <= to
            && (Channel == AllChannels || string.Equals(o.Channel, Channel, StringComparison.OrdinalIgnoreCase)));
        var orders = SalesReport.Orders(filtered, details)
            .Where(o => q.Length == 0 || o.Lines.Count > 0 || o.OrderRef.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                        || o.Customer.Contains(q, StringComparison.CurrentCultureIgnoreCase) || o.TrackingText.Contains(q, StringComparison.CurrentCultureIgnoreCase))
            .ToList();
        Orders.Clear();
        foreach (var o in orders) Orders.Add(o);
        Totals = SalesReport.Totals(orders);
        OnPropertyChanged(nameof(Currency));
    }

    [RelayCommand]
    private Task OpenAsync(OrderSummary? order)
    {
        if (order is null) return Task.CompletedTask;
        // Loose sales without an order number can only be edited on their piece.
        if (order.OrderRef.Length == 0 && order.Lines.FirstOrDefault() is { } line)
            return LibraryViewModel.OpenPieceAsync(line.Piece.FolderPath, "sales");
        return OpenOrderAsync(order.OrderRef);
    }

    [RelayCommand]
    private Task NewOrderAsync() => _app.HasRoot ? OpenOrderAsync("") : Dialogs.AlertAsync("No art folder", "Choose your art folder first.");

    private static Task OpenOrderAsync(string orderRef) =>
        Shell.Current.GoToAsync(AppShell.OrderRoute, new ShellNavigationQueryParameters { ["ref"] = orderRef });
}

using System.Collections.ObjectModel;
using ArtGander.App.Services;
using ArtGander.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ArtGander.App.ViewModels;

/// <summary>Every order across the library, with filters and totals.</summary>
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
        var all = _app.OrderSummaries;

        var channels = all.Select(o => o.Channel).Concat(_app.Settings.Channels)
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
        bool Has(string text) => text.Contains(q, StringComparison.CurrentCultureIgnoreCase);
        var orders = all.Where(o =>
                o.Date >= from && o.Date <= to
                && (Channel == AllChannels || string.Equals(o.Channel, Channel, StringComparison.OrdinalIgnoreCase))
                && (q.Length == 0 || Has(o.OrderRef) || Has(o.Customer) || Has(o.TrackingText)
                    || o.Lines.Any(l => Has(l.ProductName) || Has(l.PieceTitle))))
            .ToList();
        Orders.Clear();
        foreach (var o in orders) Orders.Add(o);
        Totals = SalesReport.Totals(orders);
        OnPropertyChanged(nameof(Currency));
    }

    [RelayCommand]
    private void Open(OrderSummary? order) => Ui.Fire(() => order is null ? Task.CompletedTask : OpenOrderAsync(order.Details.Id));

    [RelayCommand]
    private Task NewOrderAsync() => _app.HasRoot ? OpenOrderAsync(null) : Dialogs.AlertAsync("No art folder", "Choose your art folder first.");

    /// <summary>Opens an order by id, or a new order when <paramref name="id"/> is null.</summary>
    public static Task OpenOrderAsync(Guid? id) =>
        Shell.Current.GoToAsync(AppShell.OrderRoute, new ShellNavigationQueryParameters { ["id"] = id?.ToString() ?? "" });
}

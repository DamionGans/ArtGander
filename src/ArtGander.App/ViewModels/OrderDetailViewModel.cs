using System.Collections.ObjectModel;
using ArtGander.App.Services;
using ArtGander.Core.Model;
using ArtGander.Core.Naming;
using ArtGander.Core.Schema;
using ArtGander.Core.Services;
using ArtGander.Core.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ArtGander.App.ViewModels;

/// <summary>One item of an order: a sale line stored in an art piece's artgander.json.</summary>
public sealed record OrderItemRow(string FolderPath, string PieceTitle, Guid SaleId, string ProductName, int Quantity, decimal UnitPrice, decimal Fees, decimal Cost)
{
    public decimal Total => UnitPrice * Quantity;
    public string Summary => $"{Quantity} × {ProductName}";
}

/// <summary>
/// Edits one order: payment, production bill, shipping and tracking live in <c>.artgander/orders/{order}.json</c>.
/// Items are sale lines in the art pieces' own files and are written there directly.
/// </summary>
public sealed partial class OrderDetailViewModel(AppState app, ArtPieceStore store) : ObservableObject, IQueryAttributable
{
    public static IReadOnlyList<OrderStatus> Statuses { get; } = Enum.GetValues<OrderStatus>();
    public static IReadOnlyList<string> CarrierOptions { get; } = Carriers.Known;

    private string? _savedFileName;
    private string _savedRef = "";
    private string _savedJson = "";

    [ObservableProperty] public partial Order Order { get; private set; } = new();
    [ObservableProperty] public partial bool IsNew { get; private set; }
    [ObservableProperty] public partial string Summary { get; set; } = "";

    public ObservableCollection<OrderItemRow> Items { get; } = [];
    public IReadOnlyList<string> Channels => app.Settings.Channels;
    public string Currency => app.Currency;

    // "Add item" form.
    public ObservableCollection<ArtPieceDocument> Pieces { get; } = [];
    public ObservableCollection<Product> PieceProducts { get; } = [];
    [ObservableProperty] public partial ArtPieceDocument? NewItemPiece { get; set; }
    [ObservableProperty] public partial Product? NewItemProduct { get; set; }
    [ObservableProperty] public partial string NewItemQuantity { get; set; } = "1";
    [ObservableProperty] public partial string NewItemPrice { get; set; } = "";

    public bool HasUnsavedChanges => ArtGanderJson.Serialize(Order) != _savedJson;
    public bool IsLeaveConfirmed { get; set; }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        var orderRef = query.TryGetValue("ref", out var r) ? r as string ?? "" : "";
        var existing = orderRef.Length == 0 ? null : app.Orders.FirstOrDefault(o => NameRules.NamesEqual(o.OrderRef.Trim(), orderRef.Trim()));
        IsNew = existing is null;
        Order = existing is not null
            ? ArtGanderJson.DeserializeOrder(ArtGanderJson.Serialize(existing))! // edit a copy until saved
            : NewOrderFromLines(orderRef);
        _savedFileName = existing is null ? null : OrderStore.FileNameFor(existing);
        _savedRef = Order.OrderRef;
        _savedJson = existing is null ? "" : ArtGanderJson.Serialize(Order);
        IsLeaveConfirmed = false;
        Order.PropertyChanged += (_, _) => UpdateSummary();

        Pieces.Clear();
        foreach (var d in app.Documents.Where(d => !d.IsReadOnly).OrderBy(d => d.DisplayTitle, NameRules.NaturalComparer)) Pieces.Add(d);
        RebuildItems();
    }

    private Order NewOrderFromLines(string orderRef)
    {
        // Start from what the sale lines already say about this order.
        var line = SalesReport.Rows(app.Documents).FirstOrDefault(r => orderRef.Length > 0 && NameRules.NamesEqual(r.Sale.OrderRef.Trim(), orderRef.Trim()));
        return new Order
        {
            OrderRef = orderRef.Length > 0 ? orderRef : NextOrderRef(),
            Date = line?.Sale.Date ?? DateOnly.FromDateTime(DateTime.Today),
            Channel = line?.Sale.Channel ?? Channels.FirstOrDefault() ?? "shopify",
            Customer = line?.Sale.Customer ?? "",
            Currency = Currency,
        };
    }

    private string NextOrderRef()
    {
        var used = SalesReport.Rows(app.Documents).Select(r => r.Sale.OrderRef).Concat(app.Orders.Select(o => o.OrderRef)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; ; i++)
        {
            var candidate = $"#{DateTime.Today:yyMMdd}-{i}";
            if (!used.Contains(candidate)) return candidate;
        }
    }

    private void RebuildItems()
    {
        Items.Clear();
        var refText = _savedRef.Trim();
        if (refText.Length > 0)
        {
            foreach (var row in SalesReport.Rows(app.Documents).Where(r => NameRules.NamesEqual(r.Sale.OrderRef.Trim(), refText)))
                Items.Add(new OrderItemRow(row.Piece.FolderPath, row.PieceTitle, row.Sale.Id, row.ProductName, row.Sale.Quantity, row.Sale.UnitPrice, row.Sale.Fees, row.Cost));
        }
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var itemsTotal = Items.Sum(i => i.Total);
        var paid = Order.AmountPaid ?? itemsTotal;
        var revenue = paid - (Order.AmountPaidTax ?? 0);
        var production = (Order.ProductionCost ?? Items.Sum(i => i.Cost)) + (Order.ProductionTax ?? 0);
        var shipping = (Order.ShippingCost ?? 0) + (Order.ShippingTax ?? 0);
        var fees = Items.Sum(i => i.Fees);
        var profit = revenue - production - shipping - fees;
        Summary = $"Items {itemsTotal:N2} · paid {paid:N2} · revenue excl. tax {revenue:N2} · production {production:N2} · shipping {shipping:N2} · fees {fees:N2} · profit {profit:N2} {Currency}";
    }

    partial void OnNewItemPieceChanged(ArtPieceDocument? value)
    {
        PieceProducts.Clear();
        if (value is null) return;
        foreach (var p in value.Piece.Products) PieceProducts.Add(p);
        NewItemProduct = PieceProducts.FirstOrDefault();
    }

    partial void OnNewItemProductChanged(Product? value)
    {
        if (value?.Price is { } price) NewItemPrice = price.ToString("0.##");
    }

    [RelayCommand]
    private Task<bool> SaveAsync() => TrySaveAsync();

    public async Task<bool> TrySaveAsync()
    {
        if (!app.HasRoot) return false;
        Order.OrderRef = Order.OrderRef.Trim();
        if (Order.OrderRef.Length == 0) { await Dialogs.AlertAsync("Order number missing", "Please enter an order number, e.g. your Shopify order number."); return false; }
        try
        {
            using (app.SuspendWatching())
            {
                OrderStore.Save(app.RootPath!, Order, _savedFileName);
                if (!NameRules.NamesEqual(_savedRef, Order.OrderRef) && _savedRef.Length > 0)
                    UpdateItemRefs(_savedRef, Order.OrderRef);
            }
            _savedFileName = OrderStore.FileNameFor(Order);
            _savedRef = Order.OrderRef;
            _savedJson = ArtGanderJson.Serialize(Order);
            IsNew = false;
            await app.RefreshAsync();
            RebuildItems();
            return true;
        }
        catch (Exception ex)
        {
            await Dialogs.ErrorAsync(ex);
            return false;
        }
    }

    /// <summary>Keeps the items' order numbers in sync when the order number changes.</summary>
    private void UpdateItemRefs(string oldRef, string newRef)
    {
        foreach (var folder in Items.Select(i => i.FolderPath).Distinct())
        {
            var doc = store.Load(folder);
            foreach (var s in doc.Piece.Sales.Where(s => NameRules.NamesEqual(s.OrderRef.Trim(), oldRef.Trim()))) s.OrderRef = newRef;
            store.Save(doc);
        }
    }

    [RelayCommand]
    private async Task AddItemAsync()
    {
        if (NewItemPiece is null) { await Dialogs.AlertAsync("Pick an art piece", "Choose the art piece this item belongs to."); return; }
        if (!int.TryParse(NewItemQuantity, out var qty) || qty <= 0) qty = 1;
        Converters.NullableDecimalConverter.TryParse(NewItemPrice ?? "", System.Globalization.CultureInfo.CurrentCulture, out var price);
        if (_savedRef.Length == 0 || !NameRules.NamesEqual(_savedRef, Order.OrderRef.Trim()))
        {
            // Items are linked by order number, so the order must be saved first.
            if (!await TrySaveAsync()) return;
        }
        try
        {
            using (app.SuspendWatching())
            {
                var doc = store.Load(NewItemPiece.FolderPath); // always add to the latest version on disk
                doc.Piece.Sales.Add(new Sale
                {
                    Date = Order.Date,
                    OrderRef = Order.OrderRef,
                    Channel = Order.Channel,
                    Customer = Order.Customer,
                    Currency = Order.Currency,
                    ProductId = NewItemProduct?.Id,
                    Quantity = qty,
                    UnitPrice = price,
                });
                store.Save(doc);
            }
            await app.RefreshAsync();
            RefreshPieces();
            RebuildItems();
        }
        catch (Exception ex) { await Dialogs.ErrorAsync(ex); }
    }

    [RelayCommand]
    private void RemoveItem(OrderItemRow? row) => Ui.Fire(() => RemoveItemAsync(row));

    private async Task RemoveItemAsync(OrderItemRow? row)
    {
        if (row is null) return;
        if (!await Dialogs.ConfirmAsync("Remove item?", $"{row.Summary} ({row.PieceTitle}) is removed from this order and from the piece's sales.", "Remove")) return;
        try
        {
            using (app.SuspendWatching())
            {
                var doc = store.Load(row.FolderPath);
                var sale = doc.Piece.Sales.FirstOrDefault(s => s.Id == row.SaleId);
                if (sale is not null) { doc.Piece.Sales.Remove(sale); store.Save(doc); }
            }
            await app.RefreshAsync();
            RefreshPieces();
            RebuildItems();
        }
        catch (Exception ex) { await Dialogs.ErrorAsync(ex); }
    }

    [RelayCommand]
    private void OpenPiece(OrderItemRow? row) => Ui.Fire(() => OpenPieceAsync(row));

    private Task OpenPieceAsync(OrderItemRow? row) =>
        row is null ? Task.CompletedTask : LibraryViewModel.OpenPieceAsync(row.FolderPath, "sales");

    [RelayCommand]
    private async Task TrackAsync()
    {
        var url = Carriers.TrackingUrl(Order.Carrier, Order.TrackingCode);
        if (url is null) { await Dialogs.AlertAsync("No tracking link", "Choose a known delivery provider and enter a tracking code."); return; }
        await Launcher.Default.OpenAsync(new Uri(url));
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (IsNew || !app.HasRoot) { await Shell.Current.GoToAsync(".."); return; }
        var msg = Items.Count > 0
            ? $"The order details (payment, shipping, tracking) are deleted. The {Items.Count} item(s) stay recorded as sales on their art pieces."
            : "The order details are deleted.";
        if (!await Dialogs.ConfirmAsync($"Delete order {Order.OrderRef}?", msg, "Delete")) return;
        using (app.SuspendWatching()) OrderStore.Delete(app.RootPath!, Order);
        IsLeaveConfirmed = true;
        await app.RefreshAsync();
        await Shell.Current.GoToAsync("..");
    }

    private void RefreshPieces()
    {
        var selected = NewItemPiece?.FolderPath;
        Pieces.Clear();
        foreach (var d in app.Documents.Where(d => !d.IsReadOnly).OrderBy(d => d.DisplayTitle, NameRules.NaturalComparer)) Pieces.Add(d);
        NewItemPiece = Pieces.FirstOrDefault(p => p.FolderPath == selected);
    }
}

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

/// <summary>One item of the order being edited.</summary>
public sealed partial class OrderLineRow : ObservableObject
{
    public OrderLineRow(OrderLine line, Product? product, string pieceTitle)
    {
        Line = line;
        Product = product;
        PieceTitle = pieceTitle;
        line.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(OrderLine.Quantity) or nameof(OrderLine.UnitPrice)) OnPropertyChanged(nameof(Total));
        };
    }

    public OrderLine Line { get; }
    public Product? Product { get; }
    public string PieceTitle { get; }
    public string Title => Product?.Name is { Length: > 0 } n ? n : Line.Title is { Length: > 0 } t ? t : PieceTitle;
    public string Subtitle => string.Join(" · ", new[]
    {
        Product?.FindVariant(Line.VariantKey)?.Title ?? "",
        PieceTitle != Title ? PieceTitle : "",
        Line.Fulfillment == Fulfillment.Printify ? "made and shipped by Printify" : Line.Fulfillment == Fulfillment.Self ? "ship yourself" : "digital",
    }.Where(s => s.Length > 0));
    public decimal Total => Line.UnitPrice * Line.Quantity;
    public bool HasProduct => Product is not null;
}

/// <summary>
/// Edits one order (<c>.artgander/orders/{order}.json</c>): its items, payment, production bill, shipping and tracking.
/// Everything is edited on a copy and written together on Save.
/// </summary>
public sealed partial class OrderDetailViewModel(AppState app) : ObservableObject, IQueryAttributable
{
    public static IReadOnlyList<OrderStatus> Statuses { get; } = Enum.GetValues<OrderStatus>();
    public static IReadOnlyList<string> CarrierOptions { get; } = Carriers.Known;
    public static IReadOnlyList<Fulfillment> Fulfillments { get; } = Enum.GetValues<Fulfillment>();

    private string _savedJson = "";

    [ObservableProperty] public partial Order Order { get; private set; } = new();
    [ObservableProperty] public partial bool IsNew { get; private set; }
    [ObservableProperty] public partial string Summary { get; set; } = "";
    [ObservableProperty] public partial string? Notice { get; set; }

    public ObservableCollection<OrderLineRow> Items { get; } = [];
    public IReadOnlyList<string> Channels => app.Settings.Channels;
    public string Currency => app.Currency;
    public string SourceText => Order.Sources switch
    {
        { Shopify: { } s, Printify: { } p } => $"Imported from Shopify ({s.Name}) and Printify ({p.Id})",
        { Shopify: { } s } => $"Imported from Shopify ({s.Name})",
        { Printify: { } p } => $"Imported from Printify ({p.Id})",
        _ => "",
    };

    // "Add item" form.
    public ObservableCollection<Product> Products { get; } = [];
    public ObservableCollection<ProductVariant> ProductVariants { get; } = [];
    public ObservableCollection<ArtPieceDocument> Pieces { get; } = [];
    [ObservableProperty] public partial Product? NewItemProduct { get; set; }
    [ObservableProperty] public partial ProductVariant? NewItemVariant { get; set; }
    [ObservableProperty] public partial ArtPieceDocument? NewItemPiece { get; set; }
    [ObservableProperty] public partial string NewItemQuantity { get; set; } = "1";
    [ObservableProperty] public partial string NewItemPrice { get; set; } = "";
    public bool HasVariants => ProductVariants.Count > 0;

    public bool HasUnsavedChanges => ArtGanderJson.Serialize(Order) != _savedJson;
    public bool IsLeaveConfirmed { get; set; }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        var id = query.TryGetValue("id", out var r) && Guid.TryParse(r as string, out var g) ? g : (Guid?)null;
        var existing = id is null ? null : app.Orders.FirstOrDefault(o => o.Id == id);
        Load(existing);
    }

    private void Load(Order? existing)
    {
        IsNew = existing is null;
        Order = existing is not null ? Copy(existing) : new Order { OrderRef = NextOrderRef(), Channel = Channels.FirstOrDefault() ?? "shopify", Currency = Currency };
        _savedJson = existing is null ? "" : ArtGanderJson.Serialize(Order);
        IsLeaveConfirmed = false;
        Order.PropertyChanged += (_, _) => UpdateSummary();
        OnPropertyChanged(nameof(SourceText));

        Products.Clear();
        foreach (var p in app.Catalog.All) Products.Add(p);
        Pieces.Clear();
        foreach (var d in app.Documents.OrderBy(d => d.DisplayTitle, NameRules.NaturalComparer)) Pieces.Add(d);
        RebuildItems();
    }

    /// <summary>Edits a copy, so nothing changes until Save. The copy keeps the file name and hash for the conflict check.</summary>
    private static Order Copy(Order o)
    {
        var copy = ArtGanderJson.DeserializeOrder(ArtGanderJson.Serialize(o))!;
        copy.StoredFileName = o.StoredFileName;
        copy.StoredHash = o.StoredHash;
        return copy;
    }

    private string NextOrderRef()
    {
        var used = app.Orders.Select(o => o.OrderRef).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; ; i++)
        {
            var candidate = $"#{DateTime.Today:yyMMdd}-{i}";
            if (!used.Contains(candidate)) return candidate;
        }
    }

    private void RebuildItems()
    {
        Items.Clear();
        var docs = app.Documents.ToDictionary(d => d.Piece.Id, d => d);
        foreach (var line in Order.Lines)
        {
            var product = app.Catalog.Find(line.ProductId);
            var pieceId = line.PieceId ?? (product?.Pieces.Count == 1 ? product.Pieces[0] : null);
            var title = pieceId is { } id && docs.TryGetValue(id, out var d) ? d.DisplayTitle : "";
            var row = new OrderLineRow(line, product, title);
            row.PropertyChanged += (_, _) => UpdateSummary();
            Items.Add(row);
        }
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var summary = SalesReport.Orders([Order], app.Catalog.All, app.Documents).Single();
        Summary = $"Items {summary.ItemsTotal:N2} · paid {summary.Paid:N2} · revenue excl. tax {summary.Revenue:N2} · production {summary.ProductionCost:N2} · shipping {summary.Shipping:N2} · fees {summary.Fees:N2} · profit {summary.Profit:N2} {Currency}";
    }

    partial void OnNewItemProductChanged(Product? value)
    {
        ProductVariants.Clear();
        foreach (var v in value?.Variants.Where(v => v.Enabled) ?? []) ProductVariants.Add(v);
        OnPropertyChanged(nameof(HasVariants));
        NewItemVariant = ProductVariants.FirstOrDefault();
        if (value is not null && value.Pieces.Count == 1) NewItemPiece = Pieces.FirstOrDefault(p => p.Piece.Id == value.Pieces[0]);
        UpdateNewItemPrice();
    }

    partial void OnNewItemVariantChanged(ProductVariant? value) => UpdateNewItemPrice();

    private void UpdateNewItemPrice()
    {
        var price = NewItemVariant is { } v ? NewItemProduct?.PriceOf(v) : NewItemProduct?.Price;
        if (price is { } p) NewItemPrice = p.ToString("0.##");
    }

    [RelayCommand]
    private void AddItem() => Ui.Fire(AddItemAsync);

    private async Task AddItemAsync()
    {
        if (NewItemProduct is null && NewItemPiece is null)
        {
            await Dialogs.AlertAsync("Pick a product", "Choose the product that was sold, or (e.g. for an original) just the art piece.");
            return;
        }
        if (!int.TryParse(NewItemQuantity, out var qty) || qty <= 0) qty = 1;
        Converters.NullableDecimalConverter.TryParse(NewItemPrice ?? "", System.Globalization.CultureInfo.CurrentCulture, out var price);
        Order.Lines.Add(new OrderLine
        {
            ProductId = NewItemProduct?.Id,
            VariantKey = NewItemVariant?.Key,
            PieceId = NewItemPiece?.Piece.Id,
            Title = NewItemProduct?.Name ?? NewItemPiece?.Piece.Title ?? "",
            Sku = NewItemVariant?.Sku ?? "",
            Quantity = qty,
            UnitPrice = price,
            Fulfillment = NewItemProduct?.Fulfillment ?? Fulfillment.Self,
        });
        NewItemProduct = null;
        NewItemPiece = null;
        NewItemQuantity = "1";
        NewItemPrice = "";
        RebuildItems();
    }

    [RelayCommand]
    private void RemoveItem(OrderLineRow? row)
    {
        if (row is null) return;
        Order.Lines.Remove(row.Line);
        RebuildItems();
    }

    [RelayCommand]
    private void OpenProduct(OrderLineRow? row) => Ui.Fire(() => row?.Product is { } p ? ProductsViewModel.OpenProductAsync(p.Id) : Task.CompletedTask);

    [RelayCommand]
    private Task<bool> SaveAsync() => TrySaveAsync();

    public Task<bool> TrySaveAsync() => SaveAsync(force: false);

    private async Task<bool> SaveAsync(bool force)
    {
        if (!app.HasRoot) return false;
        Order.OrderRef = Order.OrderRef.Trim();
        if (Order.OrderRef.Length == 0) { await Dialogs.AlertAsync("Order number missing", "Please enter an order number, e.g. your Shopify order number."); return false; }
        try
        {
            using (app.SuspendWatching()) Records.Orders.Save(app.RootPath!, Order, force);
            _savedJson = ArtGanderJson.Serialize(Order);
            IsNew = false;
            Notice = null;
            await app.RefreshAsync();
            return true;
        }
        catch (ConcurrentEditException)
        {
            const string reload = "Reload (discard my changes)";
            const string overwrite = "Overwrite with my version";
            var choice = await Dialogs.ChooseAsync("This order was changed on another computer (or by an import) since you opened it.", reload, overwrite);
            if (choice == overwrite) return await SaveAsync(force: true);
            if (choice == reload) { await app.RefreshAsync(); Load(app.Orders.FirstOrDefault(o => o.Id == Order.Id)); return true; }
            return false;
        }
        catch (Exception ex)
        {
            await Dialogs.ErrorAsync(ex);
            return false;
        }
    }

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
        var msg = Order.IsImported
            ? "The order is deleted from ArtGander. It isn't deleted in Shopify or Printify, and a new import brings it back."
            : $"The order and its {Order.Lines.Count} item(s) are deleted.";
        if (!await Dialogs.ConfirmAsync($"Delete order {Order.OrderRef}?", msg, "Delete")) return;
        using (app.SuspendWatching()) Records.Orders.Delete(app.RootPath!, Order);
        IsLeaveConfirmed = true;
        await app.RefreshAsync();
        await Shell.Current.GoToAsync("..");
    }
}

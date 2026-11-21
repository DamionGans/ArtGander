using System.Collections.ObjectModel;
using ArtGander.App.Services;
using ArtGander.Core.Integrations;
using ArtGander.Core.Naming;
using ArtGander.Core.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ArtGander.App.ViewModels;

/// <summary>A product found in the shops, in the import preview.</summary>
public sealed partial class ProductImportRow : ObservableObject
{
    public ProductImportRow(ProductImportCandidate candidate, IReadOnlyList<ArtPieceDocument> pieces)
    {
        Candidate = candidate;
        Pieces = pieces;
        Piece = pieces.FirstOrDefault(p => candidate.SuggestedPieces.Contains(p.Piece.Id));
        Include = !candidate.IsUpToDate;
    }

    public ProductImportCandidate Candidate { get; }
    public IReadOnlyList<ArtPieceDocument> Pieces { get; }
    public string Title => Candidate.Title;
    public string Details => string.Join(" · ", new[]
    {
        Candidate.Where,
        $"{Candidate.Main.EnabledVariants.Count()} variant(s)",
        Candidate.IsUpToDate ? "already in ArtGander" : Candidate.IsNew ? "new" : "connects to \"" + Candidate.Existing!.Name + "\"",
    });
    [ObservableProperty] public partial bool Include { get; set; }
    [ObservableProperty] public partial ArtPieceDocument? Piece { get; set; }
}

/// <summary>An order in the import preview, with a choice per conflicting field.</summary>
public sealed partial class OrderImportRow(OrderImportItem item) : ObservableObject
{
    public OrderImportItem Item { get; } = item;
    public string Title => Item.Title + (Item.IsNew ? " · new" : "");
    public string Details => string.Join(" · ", Item.Changes.Where(c => !c.IsConflict).Select(c => $"{c.Label}: {c.Remote}")
        .Concat(Item.NewLines.Select(l => $"+ {l.Quantity} × {l.Title}")));
    public ObservableCollection<ConflictChoice> Conflicts { get; } = [.. item.Changes.Where(c => c.IsConflict).Select(c => new ConflictChoice(c))];
    public bool HasConflicts => Conflicts.Count > 0;
    [ObservableProperty] public partial bool Include { get; set; } = true;
}

/// <summary>A field both you and the shop changed: keep yours (default) or take the shop's.</summary>
public sealed partial class ConflictChoice(FieldChange change) : ObservableObject
{
    public FieldChange Change { get; } = change;
    public string Text => $"{Change.Label}: yours \"{Change.Local}\", shop \"{Change.Remote}\"";
    [ObservableProperty] public partial bool TakeShop { get; set; }
}

/// <summary>The Shops page: import products and orders from Printify and Shopify (and, later, push changes).</summary>
public sealed partial class ShopsViewModel(AppState app, ImportService import) : ObservableObject
{
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "";
    [ObservableProperty] public partial string ConnectionText { get; set; } = "";

    public ObservableCollection<ProductImportRow> ProductRows { get; } = [];
    public ObservableCollection<OrderImportRow> OrderRows { get; } = [];
    public bool HasProductRows => ProductRows.Count > 0;
    public bool HasOrderRows => OrderRows.Count > 0;

    public void Refresh()
    {
        var s = app.Settings.Integrations;
        var state = app.HasRoot ? SyncState.Load(app.RootPath!) : new SyncState();
        ConnectionText = string.Join(Environment.NewLine, new[]
        {
            s.PrintifyShopId is not null ? $"Printify: {s.PrintifyShopTitle}" : "Printify: not connected (Settings → Shops)",
            s.ShopifyDomain.Length > 0 ? $"Shopify: {s.ShopifyDomain}" : "Shopify: not connected (Settings → Shops)",
            state.LastOrderImport is { } o ? $"Orders last imported {o:g}" : "Orders not imported yet",
        });
    }

    // ---- Products -------------------------------------------------------------------------------------

    [RelayCommand]
    private void FetchProducts() => Ui.Fire(FetchProductsAsync);

    private async Task FetchProductsAsync()
    {
        if (!app.HasRoot || IsBusy) return;
        IsBusy = true;
        Status = "Reading products from the shops…";
        try
        {
            var candidates = await import.FetchProductsAsync(app.Settings, app.Documents.ToList(), CancellationToken.None);
            var pieces = app.Documents.OrderBy(d => d.DisplayTitle, NameRules.NaturalComparer).ToList();
            ProductRows.Clear();
            foreach (var c in candidates) ProductRows.Add(new ProductImportRow(c, pieces));
            var fresh = candidates.Count(c => !c.IsUpToDate);
            Status = $"Found {candidates.Count} product(s); {fresh} to import or connect. Check the art piece of each, then press Import selected.";
        }
        catch (Exception ex) { Status = "Couldn't read the products: " + ex.Message; }
        finally { IsBusy = false; OnPropertyChanged(nameof(HasProductRows)); }
    }

    [RelayCommand]
    private void ImportProducts() => Ui.Fire(ImportProductsAsync);

    private async Task ImportProductsAsync()
    {
        if (!app.HasRoot || IsBusy) return;
        var chosen = ProductRows.Where(r => r.Include).Select(r => (r.Candidate, (IReadOnlyList<Guid>)(r.Piece is { } p ? [p.Piece.Id] : []))).ToList();
        if (chosen.Count == 0) { Status = "Nothing selected."; return; }
        IsBusy = true;
        try
        {
            ApplyResultText(await import.ImportProductsAsync(app.RootPath!, app.Settings, chosen, CancellationToken.None), "product");
            ProductRows.Clear();
            await app.RefreshAsync();
        }
        catch (Exception ex) { Status = "Import failed: " + ex.Message; }
        finally { IsBusy = false; OnPropertyChanged(nameof(HasProductRows)); }
    }

    [RelayCommand]
    private void SelectAllProducts()
    {
        var all = ProductRows.All(r => r.Include);
        foreach (var r in ProductRows) r.Include = !all;
    }

    // ---- Orders ---------------------------------------------------------------------------------------

    [RelayCommand]
    private void FetchOrders(string? scope) => Ui.Fire(() => FetchOrdersAsync(scope == "all"));

    private async Task FetchOrdersAsync(bool everything)
    {
        if (!app.HasRoot || IsBusy) return;
        IsBusy = true;
        Status = everything ? "Reading all orders from the shops…" : "Reading new and changed orders…";
        try
        {
            await app.RefreshAsync(); // compare against the newest orders on disk
            var items = await import.FetchOrdersAsync(app.RootPath!, app.Settings, app.Orders, everything, CancellationToken.None);
            OrderRows.Clear();
            foreach (var i in items.OrderBy(i => i.Order.Date)) OrderRows.Add(new OrderImportRow(i));
            var conflicts = items.Count(i => i.HasConflicts);
            Status = items.Count == 0 ? "Everything is up to date." :
                $"{items.Count(i => i.IsNew)} new order(s), {items.Count(i => !i.IsNew)} with updates" +
                (conflicts > 0 ? $", {conflicts} where you and the shop both changed something (yours is kept unless you tick the shop's)." : ".");
        }
        catch (Exception ex) { Status = "Couldn't read the orders: " + ex.Message; }
        finally { IsBusy = false; OnPropertyChanged(nameof(HasOrderRows)); }
    }

    [RelayCommand]
    private void ImportOrders() => Ui.Fire(ImportOrdersAsync);

    private async Task ImportOrdersAsync()
    {
        if (!app.HasRoot || IsBusy) return;
        var chosen = OrderRows.Where(r => r.Include)
            .Select(r => (r.Item, (IReadOnlySet<string>)r.Conflicts.Where(c => c.TakeShop).Select(c => c.Change.Field).ToHashSet()))
            .ToList();
        if (chosen.Count == 0) { Status = "Nothing selected."; return; }
        IsBusy = true;
        try
        {
            // Only remember "imported until now" when every fetched order was imported.
            var all = chosen.Count == OrderRows.Count;
            var result = await Task.Run(() => import.ApplyOrders(app.RootPath!, chosen, markImported: all));
            ApplyResultText(result, "order");
            OrderRows.Clear();
            await app.RefreshAsync();
            Refresh();
        }
        catch (Exception ex) { Status = "Import failed: " + ex.Message; }
        finally { IsBusy = false; OnPropertyChanged(nameof(HasOrderRows)); }
    }

    private void ApplyResultText(ImportService.ApplyResult result, string what) =>
        Status = $"Imported {result.Saved} {what}(s)." + (result.Skipped.Count > 0 ? " Skipped: " + string.Join("; ", result.Skipped) : "");
}

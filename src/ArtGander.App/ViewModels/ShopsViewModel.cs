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
        Candidate.MatchReason,
    }.Where(x => x.Length > 0));
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

/// <summary>One field in the sync preview, with your choice.</summary>
public sealed partial class SyncChangeRow : ObservableObject
{
    public static IReadOnlyList<string> Options { get; } = ["Send ArtGander's", "Take the shop's", "Skip for now"];

    public SyncChangeRow(Core.Model.Product product, SyncFieldChange change)
    {
        Product = product;
        Change = change;
        Choice = change.DefaultResolution switch { SyncResolution.Push => Options[0], SyncResolution.TakeRemote => Options[1], _ => Options[2] };
    }

    public Core.Model.Product Product { get; }
    public SyncFieldChange Change { get; }
    public string Where => Change.Channel == "printify" ? "Printify" : "Shopify";
    public string KindText => Change.Kind switch
    {
        SyncChangeKind.Push => "changed in ArtGander",
        SyncChangeKind.OutsideEdit => $"changed in {Where}",
        SyncChangeKind.Conflict => "changed on both sides",
        _ => $"updated by {Where} (sales)",
    };
    public bool IsConflict => Change.Kind == SyncChangeKind.Conflict;
    public string Values => $"ArtGander: {Short(Change.Local)}   ·   {Where}: {Short(Change.Remote)}";
    [ObservableProperty] public partial string Choice { get; set; }

    public SyncResolution Resolution => Choice == Options[0] ? SyncResolution.Push : Choice == Options[1] ? SyncResolution.TakeRemote : SyncResolution.Skip;

    private static string Short(string? s) => string.IsNullOrEmpty(s) ? "(empty)" : s.Length > 80 ? s[..80].ReplaceLineEndings(" ") + "…" : s.ReplaceLineEndings(" ");
}

/// <summary>One product in the sync preview.</summary>
public sealed class SyncPlanRow(ProductSyncPlan plan)
{
    public ProductSyncPlan Plan { get; } = plan;
    public string Title => Plan.Product.Name;
    public string? Problem => Plan.Problem;
    public bool HasProblem => Plan.Problem is not null;
    public List<SyncChangeRow> Changes { get; } = [.. plan.Changes.Select(c => new SyncChangeRow(plan.Product, c))];
}

/// <summary>The Shops page: import products and orders from Printify and Shopify, and sync product changes.</summary>
public sealed partial class ShopsViewModel(AppState app, ImportService import) : ObservableObject
{
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "";
    [ObservableProperty] public partial string ConnectionText { get; set; } = "";

    public ObservableCollection<ProductImportRow> ProductRows { get; } = [];
    public ObservableCollection<SyncPlanRow> SyncRows { get; } = [];
    public bool HasSyncRows => SyncRows.Any(r => !r.HasProblem);
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

    // ---- Sync -----------------------------------------------------------------------------------------

    [RelayCommand]
    private void CheckSync() => Ui.Fire(CheckSyncAsync);

    private async Task CheckSyncAsync()
    {
        if (!app.HasRoot || IsBusy) return;
        IsBusy = true;
        Status = "Comparing your products with the shops…";
        try
        {
            await app.RefreshAsync();
            var plans = await import.CheckProductsAsync(app.Settings, CancellationToken.None);
            SyncRows.Clear();
            foreach (var p in plans) SyncRows.Add(new SyncPlanRow(p));
            var fields = plans.Sum(p => p.Changes.Count);
            var conflicts = plans.Sum(p => p.Changes.Count(c => c.Kind == SyncChangeKind.Conflict));
            Status = plans.Count == 0 ? "All linked products match the shops." :
                $"{plans.Count} product(s), {fields} difference(s)" + (conflicts > 0 ? $", {conflicts} changed on both sides (skipped unless you choose)." : ".")
                + " Nothing is sent until you press Sync now.";
        }
        catch (Exception ex) { Status = "Couldn't compare: " + ex.Message; }
        finally { IsBusy = false; OnPropertyChanged(nameof(HasSyncRows)); }
    }

    [RelayCommand]
    private void ApplySync() => Ui.Fire(ApplySyncAsync);

    private async Task ApplySyncAsync()
    {
        if (!app.HasRoot || IsBusy) return;
        var rows = SyncRows.Where(r => !r.HasProblem).ToList();
        var changes = rows.SelectMany(r => r.Changes).ToList();
        var send = changes.Count(c => c.Resolution == SyncResolution.Push);
        var take = changes.Count(c => c.Resolution == SyncResolution.TakeRemote);
        if (send + take == 0) { Status = "Everything is set to skip."; return; }
        if (send > 0 && !await Dialogs.ConfirmAsync("Send changes to the shops?",
                $"{send} change(s) will be sent to Printify/Shopify and {take} taken over into ArtGander. Products changed in the meantime are skipped.", "Sync now"))
            return;
        IsBusy = true;
        Status = "Syncing…";
        try
        {
            var choices = changes.ToDictionary(c => Core.Integrations.SyncRunner.ChoiceKey(c.Product, c.Change), c => c.Resolution);
            var outcomes = await import.ApplySyncAsync(app.RootPath!, app.Settings, rows.Select(r => r.Plan).ToList(), choices, CancellationToken.None);
            var failed = outcomes.Where(o => !o.Done).ToList();
            Status = $"Synced {outcomes.Count(o => o.Done)} product(s)." + (failed.Count > 0 ? " Not done: " + string.Join("; ", failed.Select(f => $"{f.Product.Name}: {f.Message}")) : "");
            SyncRows.Clear();
            await app.RefreshAsync();
        }
        catch (Exception ex) { Status = "Sync failed: " + ex.Message; }
        finally { IsBusy = false; OnPropertyChanged(nameof(HasSyncRows)); }
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

    /// <summary>Finds everything in both shops and imports all of it in one go (combining what belongs together).</summary>
    [RelayCommand]
    private void ImportAllProducts() => Ui.Fire(ImportAllProductsAsync);

    private async Task ImportAllProductsAsync()
    {
        if (!app.HasRoot || IsBusy) return;
        if (ProductRows.Count == 0) await FetchProductsAsync();
        var todo = ProductRows.Where(r => !r.Candidate.IsUpToDate).ToList();
        if (todo.Count == 0) { Status = "All products from the shops are already in ArtGander."; return; }
        var combined = todo.Count(r => r.Candidate.Printify is not null && r.Candidate.Shopify is not null);
        var joined = todo.Count(r => !r.Candidate.IsNew);
        var withoutPiece = todo.Count(r => r.Piece is null);
        if (!await Dialogs.ConfirmAsync("Import all products?",
                $"{todo.Count} product(s) are imported: {combined} combined from Printify + Shopify, {joined} joined to products you already have in ArtGander."
                + (withoutPiece > 0 ? $" {withoutPiece} have no art piece yet; you can link them later on the product page." : "")
                + " Nothing in the shops changes.", "Import all"))
            return;
        foreach (var r in ProductRows) r.Include = !r.Candidate.IsUpToDate;
        await ImportProductsAsync();
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

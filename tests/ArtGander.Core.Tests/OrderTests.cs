using ArtGander.Core.Model;
using ArtGander.Core.Services;
using ArtGander.Core.Storage;

namespace ArtGander.Core.Tests;

public class OrderTests
{
    [Fact]
    public void Order_details_drive_the_money_calculation()
    {
        using var lib = new TempLibrary();
        var service = new PieceService(new ArtPieceStore());
        var a = service.CreatePiece(lib.Root, "A");
        var product = new Product { Name = "Mug", ProductionCost = 6, Pieces = [a.Piece.Id] };

        var details = new Order
        {
            Lines = { new OrderLine { ProductId = product.Id, Quantity = 1, UnitPrice = 22, Fees = 1 } },
            OrderRef = "#2001",
            AmountPaid = 30, AmountPaidTax = 5,      // customer paid 30 incl. 5 tax (and shipping)
            ProductionCost = 8, ProductionTax = 1.6m, // actual Printify bill
            ShippingCost = 4, ShippingTax = 0.8m,
            Carrier = "USPS", TrackingCode = "9400 1",
        };

        var order = SalesReport.Orders([details], [product], [a]).Single();

        Assert.Equal(20.2m, order.Revenue);        // 30 paid - 5 tax - 4.8 shipping the customer paid for
        Assert.Equal(9.6m, order.ProductionCost);
        Assert.Equal(4.8m, order.Shipping);        // kept for bookkeeping…
        Assert.Equal(10.6m, order.CostAndFees);    // …but not the artist's cost: 9.6 production + 1 fees
        Assert.Equal(9.6m, order.Profit);          // 20.2 - 10.6
        Assert.Equal("USPS 9400 1", order.TrackingText);
    }

    [Fact]
    public void Shipping_is_never_the_artists_cost()
    {
        // No amount paid entered: revenue is the item prices, and the shipping (paid by the customer) isn't subtracted.
        var product = new Product { ProductionCost = 6 };
        var order = new Order { ShippingCost = 4, ShippingTax = 1, Lines = { new OrderLine { ProductId = product.Id, Quantity = 1, UnitPrice = 20 } } };
        var summary = SalesReport.Orders([order], [product], []).Single();
        Assert.Equal(20, summary.Revenue);
        Assert.Equal(5, summary.Shipping);
        Assert.Equal(14, summary.Profit);
        Assert.Equal(5, SalesReport.Totals([summary]).Shipping);
    }

    [Fact]
    public void Without_details_falls_back_to_product_costs()
    {
        using var lib = new TempLibrary();
        var a = new PieceService(new ArtPieceStore()).CreatePiece(lib.Root, "A");
        var product = new Product { ProductionCost = 6, Pieces = [a.Piece.Id] };
        var details = new Order { OrderRef = "#1", Lines = { new OrderLine { ProductId = product.Id, Quantity = 2, UnitPrice = 20 } } };

        var order = SalesReport.Orders([details], [product], [a]).Single();
        Assert.Equal(40, order.Revenue);
        Assert.Equal(12, order.ProductionCost);
        Assert.Equal(28, order.Profit);
    }

    [Fact]
    public void Orders_without_items_still_show()
    {
        var orders = SalesReport.Orders([new Order { OrderRef = "#9", AmountPaid = 10 }], [], []);
        Assert.Equal(10, Assert.Single(orders).Revenue);
    }

    [Fact]
    public void Order_files_live_inside_the_art_folder_and_follow_renames()
    {
        using var lib = new TempLibrary();
        var order = new Order { OrderRef = "#1001", Carrier = "PostNL", TrackingCode = "3S123" };
        Records.Orders.Save(lib.Root, order);
        Assert.True(File.Exists(Path.Combine(lib.Root, ".artgander", "orders", "1001.json")));

        order.OrderRef = "#1002";
        Records.Orders.Save(lib.Root, order);

        var loaded = Assert.Single(Records.Orders.LoadAll(lib.Root));
        Assert.Equal("#1002", loaded.OrderRef);
        Assert.Equal("PostNL", loaded.Carrier);
        Assert.Throws<IOException>(() => Records.Orders.Save(lib.Root, new Order { OrderRef = "#1002" }));
    }

    [Fact]
    public void Tracking_urls_for_known_carriers()
    {
        Assert.Equal("https://tools.usps.com/go/TrackConfirmAction?tLabels=9400%201", Carriers.TrackingUrl("usps", "9400 1"));
        Assert.Null(Carriers.TrackingUrl("Pigeon", "1"));
    }
}

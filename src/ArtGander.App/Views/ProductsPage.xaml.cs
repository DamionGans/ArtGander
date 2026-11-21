using ArtGander.App.ViewModels;

namespace ArtGander.App.Views;

public partial class ProductsPage : ContentPage
{
    private readonly ProductsViewModel _vm;

    public ProductsPage(ProductsViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _vm = viewModel;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _vm.Rebuild();
    }
}

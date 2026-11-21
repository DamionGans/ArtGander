using ArtGander.App.ViewModels;

namespace ArtGander.App.Views;

public partial class ProductDetailPage : ContentPage
{
    public ProductDetailPage(ProductDetailViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        BindingContext = viewModel;
    }

    public ProductDetailViewModel ViewModel { get; }
}

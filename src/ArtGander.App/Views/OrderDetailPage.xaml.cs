using ArtGander.App.ViewModels;

namespace ArtGander.App.Views;

public partial class OrderDetailPage : ContentPage
{
    public OrderDetailPage(OrderDetailViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = ViewModel = viewModel;
    }

    public OrderDetailViewModel ViewModel { get; }
}

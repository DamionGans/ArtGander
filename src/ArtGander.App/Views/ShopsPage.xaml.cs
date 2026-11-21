using ArtGander.App.ViewModels;

namespace ArtGander.App.Views;

public partial class ShopsPage : ContentPage
{
    private readonly ShopsViewModel _vm;

    public ShopsPage(ShopsViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _vm = viewModel;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _vm.Refresh();
    }
}

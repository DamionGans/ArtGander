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

    /// <summary>The Back button on the page (the bar at the top is hidden). Unsaved changes are asked about first.</summary>
    private void OnBackClicked(object? sender, EventArgs e) => Services.Ui.Fire(() => Shell.Current.GoToAsync(".."));
}

using ArtGander.App.ViewModels;

namespace ArtGander.App.Views;

public partial class CollectionsPage : ContentPage
{
    private readonly CollectionsViewModel _vm;

    public CollectionsPage(CollectionsViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _vm = viewModel;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _vm.Rebuild();
    }

    private void OnDescriptionUnfocused(object? sender, FocusEventArgs e)
    {
        if (sender is BindableObject { BindingContext: CollectionRow row }) _vm.SaveDescriptionCommand.Execute(row);
    }
}

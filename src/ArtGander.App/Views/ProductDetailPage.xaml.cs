using ArtGander.App.Services;
using ArtGander.App.ViewModels;

namespace ArtGander.App.Views;

public partial class ProductDetailPage : ContentPage
{
    private readonly IPlatformServices _platform;

    public ProductDetailPage(ProductDetailViewModel viewModel, IPlatformServices platform)
    {
        InitializeComponent();
        _platform = platform;
        ViewModel = viewModel;
        BindingContext = viewModel;
    }

    public ProductDetailViewModel ViewModel { get; }

    /// <summary>The Back button on the page (the bar at the top is hidden). Unsaved changes are asked about first.</summary>
    private void OnBackClicked(object? sender, EventArgs e) => Ui.Fire(() => Shell.Current.GoToAsync(".."));

    /// <summary>Pictures dropped anywhere on the page become the product's pictures.</summary>
    private async void OnDrop(object? sender, DropEventArgs e)
    {
        try
        {
            var files = await _platform.GetDroppedFilesAsync(e);
            if (files.Count > 0) await ViewModel.AddImagePathsAsync(files);
        }
        catch (Exception ex) { await Dialogs.ErrorAsync(ex); }
    }
}
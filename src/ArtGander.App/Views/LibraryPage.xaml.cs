using ArtGander.App.Services;
using ArtGander.App.ViewModels;

namespace ArtGander.App.Views;

public partial class LibraryPage : ContentPage
{
    private readonly AppState _app;
    private bool _initialized;

    private readonly LibraryViewModel _vm;

    public LibraryPage(LibraryViewModel viewModel, AppState app)
    {
        InitializeComponent();
        BindingContext = _vm = viewModel;
        _app = app;
        SizeChanged += (_, _) => UpdateAvailableWidth();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_initialized) return;
        _initialized = true;
        await _app.InitializeAsync();
    }

    /// <summary>Tells the raster view how much width its rows can fill.</summary>
    private void UpdateAvailableWidth()
    {
        if (Width <= 0) return;
        const double padding = 48, scrollbar = 18;
        _vm.AvailableWidth = Math.Floor(Width - padding - scrollbar);
    }
}

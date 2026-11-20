using ArtGander.App.Services;
using ArtGander.App.ViewModels;

namespace ArtGander.App.Views;

public partial class LibraryPage : ContentPage
{
    private readonly AppState _app;
    private bool _initialized;

    public LibraryPage(LibraryViewModel viewModel, AppState app)
    {
        InitializeComponent();
        BindingContext = viewModel;
        _app = app;
        SizeChanged += (_, _) => UpdateSpan();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_initialized) return;
        _initialized = true;
        await _app.InitializeAsync();
    }

    /// <summary>Keeps grid cards around 230 px wide whatever the window size.</summary>
    private void UpdateSpan()
    {
        if (Grid.ItemsLayout is GridItemsLayout layout && Width > 0)
            layout.Span = Math.Max(1, (int)((Width - 48) / 230));
    }
}

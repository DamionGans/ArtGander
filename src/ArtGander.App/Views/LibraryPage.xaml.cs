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

    private const double TileWidth = 220;

    /// <summary>
    /// Keeps the tiles around <see cref="TileWidth"/> wide whatever the window size. A new layout object is assigned
    /// instead of changing Span, because a runtime Span change isn't reliably applied by every platform.
    /// </summary>
    private void UpdateSpan()
    {
        if (Width <= 0) return;
        var span = Math.Max(2, (int)((Width - 48) / TileWidth));
        if (Grid.ItemsLayout is GridItemsLayout { Span: var current } && current == span) return;
        Grid.ItemsLayout = new GridItemsLayout(span, ItemsLayoutOrientation.Vertical)
        {
            HorizontalItemSpacing = 14,
            VerticalItemSpacing = 14,
        };
    }
}

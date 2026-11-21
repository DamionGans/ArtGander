using ArtGander.App.Services;
using ArtGander.App.ViewModels;

namespace ArtGander.App.Views;

public partial class PieceDetailPage : ContentPage
{
    private readonly IPlatformServices _platform;

    public PieceDetailPage(PieceDetailViewModel viewModel, IPlatformServices platform)
    {
        InitializeComponent();
        _platform = platform;
        ViewModel = viewModel;
        BindingContext = viewModel;
        viewModel.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(PieceDetailViewModel.Tab)) UpdateTabs(); };
        UpdateTabs();
    }

    public PieceDetailViewModel ViewModel { get; }

    // Exposed for bindings inside item templates (Source={x:Reference Root}).
    public IReadOnlyList<string> Channels => ViewModel.Channels;
    public System.Windows.Input.ICommand AddCollectionCommand => ViewModel.AddCollectionCommand;
    public System.Windows.Input.ICommand RenameVersionCommand => ViewModel.RenameVersionCommand;
    public System.Windows.Input.ICommand ApplyConventionCommand => ViewModel.ApplyConventionCommand;
    public System.Windows.Input.ICommand OpenFileCommand => ViewModel.OpenFileCommand;
    public System.Windows.Input.ICommand RevealFileCommand => ViewModel.RevealFileCommand;
    public System.Windows.Input.ICommand UntrackFileCommand => ViewModel.UntrackFileCommand;
    public System.Windows.Input.ICommand TrackFileCommand => ViewModel.TrackFileCommand;
    public System.Windows.Input.ICommand TrackFileAsIsCommand => ViewModel.TrackFileAsIsCommand;
    public System.Windows.Input.ICommand OpenUrlCommand => ViewModel.OpenUrlCommand;

    protected override void OnAppearing()
    {
        base.OnAppearing();
        ViewModel.OnAppearing();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        ViewModel.OnDisappearing();
    }

    private void UpdateTabs()
    {
        var selected = (Style)Application.Current!.Resources["TabButtonSelected"];
        var normal = (Style)Application.Current.Resources["TabButton"];
        foreach (var (button, tab) in new[] { (TabOverview, "overview"), (TabPieces, "pieces"), (TabFiles, "files"), (TabProducts, "products"), (TabSales, "sales"), (TabCopyright, "copyright") })
            button.Style = ViewModel.Tab == tab ? selected : normal;
    }

    private async void OnDrop(object? sender, DropEventArgs e)
    {
        var files = await _platform.GetDroppedFilesAsync(e);
        if (files.Count == 0) return;
        ViewModel.Tab = "files";
        await ViewModel.ImportPathsAsync(files);
    }

    /// <summary>The Back button on the page (the bar at the top is hidden). Unsaved changes are asked about first.</summary>
    private void OnBackClicked(object? sender, EventArgs e) => Services.Ui.Fire(() => Shell.Current.GoToAsync(".."));
}

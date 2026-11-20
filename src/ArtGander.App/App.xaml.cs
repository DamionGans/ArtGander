namespace ArtGander.App;

public partial class App : Application
{
    private readonly IServiceProvider _services;

    public App(IServiceProvider services)
    {
        InitializeComponent(); // loads the resources before any page is created
        _services = services;
    }

    protected override Window CreateWindow(IActivationState? activationState) => new(_services.GetRequiredService<AppShell>())
    {
        Title = "ArtGander",
        MinimumWidth = 900,
        MinimumHeight = 600,
        Width = 1280,
        Height = 820,
    };
}

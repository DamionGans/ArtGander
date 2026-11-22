using System.Windows.Input;

namespace ArtGander.App.Views;

/// <summary>
/// The Mistcoat controls for one image (its binding context is a <see cref="ViewModels.ProtectableRow"/>).
/// The page passes in its commands; each is called with the row.
/// </summary>
public partial class ProtectControls : ContentView
{
    public ProtectControls() => InitializeComponent();

    public static readonly BindableProperty QuickProtectCommandProperty = BindableProperty.Create(nameof(QuickProtectCommand), typeof(ICommand), typeof(ProtectControls));
    public static readonly BindableProperty ShowOptionsCommandProperty = BindableProperty.Create(nameof(ShowOptionsCommand), typeof(ICommand), typeof(ProtectControls));
    public static readonly BindableProperty ProtectWithOptionsCommandProperty = BindableProperty.Create(nameof(ProtectWithOptionsCommand), typeof(ICommand), typeof(ProtectControls));
    public static readonly BindableProperty CancelCommandProperty = BindableProperty.Create(nameof(CancelCommand), typeof(ICommand), typeof(ProtectControls));
    public static readonly BindableProperty OpenInMistcoatCommandProperty = BindableProperty.Create(nameof(OpenInMistcoatCommand), typeof(ICommand), typeof(ProtectControls));
    public static readonly BindableProperty OpenCopyCommandProperty = BindableProperty.Create(nameof(OpenCopyCommand), typeof(ICommand), typeof(ProtectControls));
    public static readonly BindableProperty ShowCopyCommandProperty = BindableProperty.Create(nameof(ShowCopyCommand), typeof(ICommand), typeof(ProtectControls));

    public ICommand? QuickProtectCommand { get => (ICommand?)GetValue(QuickProtectCommandProperty); set => SetValue(QuickProtectCommandProperty, value); }
    public ICommand? ShowOptionsCommand { get => (ICommand?)GetValue(ShowOptionsCommandProperty); set => SetValue(ShowOptionsCommandProperty, value); }
    public ICommand? ProtectWithOptionsCommand { get => (ICommand?)GetValue(ProtectWithOptionsCommandProperty); set => SetValue(ProtectWithOptionsCommandProperty, value); }
    public ICommand? CancelCommand { get => (ICommand?)GetValue(CancelCommandProperty); set => SetValue(CancelCommandProperty, value); }
    public ICommand? OpenInMistcoatCommand { get => (ICommand?)GetValue(OpenInMistcoatCommandProperty); set => SetValue(OpenInMistcoatCommandProperty, value); }
    public ICommand? OpenCopyCommand { get => (ICommand?)GetValue(OpenCopyCommandProperty); set => SetValue(OpenCopyCommandProperty, value); }
    public ICommand? ShowCopyCommand { get => (ICommand?)GetValue(ShowCopyCommandProperty); set => SetValue(ShowCopyCommandProperty, value); }
}

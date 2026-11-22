using CommunityToolkit.Mvvm.ComponentModel;

namespace ArtGander.App.ViewModels;

/// <summary>A row showing an image file that can be protected against AI training with Mistcoat (a piece's file, a product picture).</summary>
public abstract partial class ProtectableRow : ObservableObject
{
    public abstract string FullPath { get; }
    public abstract bool IsMissing { get; }

    /// <summary>An image Mistcoat can read.</summary>
    public bool CanProtect => !IsMissing && Core.Integrations.MistcoatPasses.CanProtect(FullPath);

    [ObservableProperty] public partial bool IsProtecting { get; set; }
    [ObservableProperty] public partial string ProtectStage { get; set; } = "";
    [ObservableProperty] public partial double ProtectFraction { get; set; }
    /// <summary>The outcome of the last protection: what it looks like and how durable it is, or the error.</summary>
    [ObservableProperty] public partial string ProtectMessage { get; set; } = "";
    /// <summary>The advanced options, while they are open.</summary>
    [ObservableProperty] public partial ProtectOptionsViewModel? Advanced { get; set; }

    public bool HasAdvanced => Advanced is not null;
    public bool CanStartProtect => CanProtect && !IsProtecting;
    public bool HasProtectMessage => ProtectMessage.Length > 0 && !IsProtecting;
    public CancellationTokenSource? ProtectCancel { get; set; }

    /// <summary>The protected copy in the Protected folder next to the file, or null.</summary>
    [ObservableProperty] public partial string? ProtectedCopyPath { get; set; }
    public bool HasProtectedCopy => ProtectedCopyPath is not null;
    public string ProtectedCopyText => ProtectedCopyPath is { } p ? $"Protected copy: {Core.Services.ProtectedCopies.FolderName}/{Path.GetFileName(p)}" : "";
    public string ProtectButtonText => HasProtectedCopy ? "Protect again" : "Protect";
    partial void OnProtectedCopyPathChanged(string? value)
    {
        OnPropertyChanged(nameof(HasProtectedCopy));
        OnPropertyChanged(nameof(ProtectedCopyText));
        OnPropertyChanged(nameof(ProtectButtonText));
    }

    /// <summary>Looks for the protected copy on disk (also one saved from Mistcoat's own window).</summary>
    public void RefreshProtectedCopy() => ProtectedCopyPath = CanProtect ? Core.Services.ProtectedCopies.Find(FullPath) : null;

    partial void OnAdvancedChanged(ProtectOptionsViewModel? value) => OnPropertyChanged(nameof(HasAdvanced));
    partial void OnIsProtectingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStartProtect));
        OnPropertyChanged(nameof(HasProtectMessage));
    }
    partial void OnProtectMessageChanged(string value) => OnPropertyChanged(nameof(HasProtectMessage));

    /// <summary>Keeps protection progress and open options when rows are rebuilt.</summary>
    public void CarryOverFrom(ProtectableRow? from)
    {
        if (from is null) return;
        IsProtecting = from.IsProtecting;
        ProtectStage = from.ProtectStage;
        ProtectFraction = from.ProtectFraction;
        ProtectMessage = from.ProtectMessage;
        ProtectCancel = from.ProtectCancel;
        Advanced = from.Advanced;
    }
}

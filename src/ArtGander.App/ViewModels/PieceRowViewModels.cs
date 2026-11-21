using System.Collections.ObjectModel;
using ArtGander.App.Services;
using ArtGander.Core.Model;
using ArtGander.Core.Naming;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ArtGander.App.ViewModels;

/// <summary>A tracked file in the Files tab.</summary>
public sealed partial class FileRowViewModel(FileEntry entry, string fullPath, bool missing, bool misnamed) : ObservableObject
{
    public FileEntry Entry { get; } = entry;
    public string FullPath { get; } = fullPath;
    public bool IsMissing { get; } = missing;
    public bool IsMisnamed { get; } = misnamed;
    public bool IsOk => !IsMissing && !IsMisnamed;

    /// <summary>The version label being edited. Applying it renames the file.</summary>
    [ObservableProperty] public partial string EditVersion { get; set; } = entry.Version;
    [ObservableProperty] public partial ImageSource? Thumbnail { get; set; }

    public bool VersionChanged => FileNamer.Label(EditVersion) != Entry.Version;
    partial void OnEditVersionChanged(string value) => OnPropertyChanged(nameof(VersionChanged));

    // ---- Protection with Mistcoat ----

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

    partial void OnAdvancedChanged(ProtectOptionsViewModel? value) => OnPropertyChanged(nameof(HasAdvanced));
    partial void OnIsProtectingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStartProtect));
        OnPropertyChanged(nameof(HasProtectMessage));
    }
    partial void OnProtectMessageChanged(string value) => OnPropertyChanged(nameof(HasProtectMessage));

    public string SizeText
    {
        get
        {
            try
            {
                var len = new FileInfo(FullPath).Length;
                return len switch
                {
                    < 1024 => $"{len} B",
                    < 1024 * 1024 => $"{len / 1024.0:0} KB",
                    < 1024L * 1024 * 1024 => $"{len / 1024.0 / 1024.0:0.0} MB",
                    _ => $"{len / 1024.0 / 1024.0 / 1024.0:0.00} GB",
                };
            }
            catch (IOException) { return ""; }
        }
    }

    public async Task LoadThumbnailAsync(ThumbnailService thumbs)
    {
        if (IsMissing) return;
        var path = await thumbs.GetAsync(FullPath);
        if (path is not null) MainThread.BeginInvokeOnMainThread(() => Thumbnail = ImageSource.FromFile(path));
    }
}

/// <summary>A file on disk that isn't tracked yet.</summary>
public sealed partial class UntrackedFileViewModel(string fileName, string suggestedVersion, string kind) : ObservableObject
{
    public string FileName { get; } = fileName;
    [ObservableProperty] public partial string Version { get; set; } = suggestedVersion;
    [ObservableProperty] public partial string Kind { get; set; } = kind;
}

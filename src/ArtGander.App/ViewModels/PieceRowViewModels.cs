using System.Collections.ObjectModel;
using ArtGander.App.Services;
using ArtGander.Core.Model;
using ArtGander.Core.Naming;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ArtGander.App.ViewModels;

/// <summary>A tracked file in the Files tab.</summary>
public sealed partial class FileRowViewModel(FileEntry entry, string fullPath, bool missing, bool misnamed) : ProtectableRow
{
    public FileEntry Entry { get; } = entry;
    public override string FullPath { get; } = fullPath;
    public override bool IsMissing { get; } = missing;
    public bool IsMisnamed { get; } = misnamed;
    public bool IsOk => !IsMissing && !IsMisnamed;

    /// <summary>The version label being edited. Applying it renames the file.</summary>
    [ObservableProperty] public partial string EditVersion { get; set; } = entry.Version;
    [ObservableProperty] public partial ImageSource? Thumbnail { get; set; }

    public bool VersionChanged => FileNamer.Label(EditVersion) != Entry.Version;
    partial void OnEditVersionChanged(string value) => OnPropertyChanged(nameof(VersionChanged));

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

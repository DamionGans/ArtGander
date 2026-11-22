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

    /// <summary>For a missing file: the special folder it was moved into (Photograph or Protected), if found there.</summary>
    public string? MovedTo { get; init; }
    public string MissingText => MovedTo is { } where
        ? $"Moved into the {where} folder, where ArtGander shows it now. Remove it from this list."
        : "⚠ Missing on disk: moved, renamed or deleted outside ArtGander";

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

/// <summary>A picture in a special folder (the original photograph in Photograph/), shown with its protected copy.</summary>
public sealed partial class SpecialFileRow : ProtectableRow
{
    public SpecialFileRow(string fullPath)
    {
        FullPath = fullPath;
        RefreshProtectedCopy();
    }

    public override string FullPath { get; }
    public override bool IsMissing => !File.Exists(FullPath);
    public string FileName => Path.GetFileName(FullPath);
    [ObservableProperty] public partial ImageSource? Thumbnail { get; set; }

    public async Task LoadThumbnailAsync(ThumbnailService thumbs)
    {
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

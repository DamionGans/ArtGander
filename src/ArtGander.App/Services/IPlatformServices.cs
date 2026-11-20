namespace ArtGander.App.Services;

/// <summary>Things each operating system does its own way. The implementations live in <c>Platforms/{OS}/PlatformServices.cs</c>.</summary>
public interface IPlatformServices
{
    /// <summary>Shows the native folder picker. Returns null when cancelled.</summary>
    Task<string?> PickFolderAsync();

    /// <summary>Shows a file or folder in Explorer or Finder.</summary>
    void RevealInFileManager(string path);

    /// <summary>Creates a small thumbnail with the OS thumbnailer (the Windows Shell or macOS QuickLook) and writes it to <paramref name="targetPath"/>.</summary>
    Task<bool> CreateThumbnailAsync(string sourcePath, string targetPath, int size);

    /// <summary>Extracts local file paths from a drag-and-drop event, when the platform supports it.</summary>
    Task<IReadOnlyList<string>> GetDroppedFilesAsync(DropEventArgs e);
}

using System.Diagnostics;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace ArtGander.App.Services;

public sealed class PlatformServices : IPlatformServices
{
    public async Task<string?> PickFolderAsync()
    {
        // The Windows App SDK picker works for unpackaged and elevated apps, and with network (UNC) paths.
        var window = Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView as Microsoft.UI.Xaml.Window;
        if (window is null) return null;
        var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(window.AppWindow.Id)
        {
            SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.PicturesLibrary,
        };
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    public async Task<IReadOnlyList<string>> PickFilesAsync(string startFolder)
    {
        var window = Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView as Microsoft.UI.Xaml.Window;
        if (window is null) return [];
        var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(window.AppWindow.Id)
        {
            // Always open in the art piece's folder (not wherever the last pick happened).
            SuggestedFolder = startFolder,
            CommitButtonText = "Add to art piece",
        };
        picker.FileTypeFilter.Add("*");
        var files = await picker.PickMultipleFilesAsync();
        return files?.Select(f => f.Path).Where(p => !string.IsNullOrEmpty(p)).ToList() ?? [];
    }

    public void RevealInFileManager(string path)
    {
        var args = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{path}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
    }

    public async Task<bool> CreateThumbnailAsync(string sourcePath, string targetPath, int size)
    {
        // The Windows thumbnailer ignores color profiles, so CMYK and wide-gamut (e.g. Adobe RGB) pictures would show
        // wrong colors: those are converted to screen colors through their own profile.
        var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (ext is ".tif" or ".tiff" or ".jpg" or ".jpeg" or ".png" or ".webp" or ".psd" or ".psb")
        {
            var managed = await Task.Run(() =>
                ArtGander.Imaging.MagickColorConverter.NeedsColorManagedPreview(sourcePath)
                && ArtGander.Imaging.MagickColorConverter.TryCreatePreview(sourcePath, targetPath, size));
            if (managed) return true;
        }
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(sourcePath);
            using var thumb = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, (uint)size, ThumbnailOptions.ResizeThumbnail);
            if (thumb is null || thumb.Type != ThumbnailType.Image) return false;
            await using var input = thumb.AsStreamForRead();
            await using var output = File.Create(targetPath);
            await input.CopyToAsync(output);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<string>> GetDroppedFilesAsync(DropEventArgs e)
    {
        var view = e.PlatformArgs?.DragEventArgs.DataView;
        if (view is null || !view.Contains(StandardDataFormats.StorageItems)) return [];
        var items = await view.GetStorageItemsAsync();
        return items.OfType<StorageFile>().Select(f => f.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
    }
}

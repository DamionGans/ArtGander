using Foundation;
using QuickLookThumbnailing;
using UIKit;
using UniformTypeIdentifiers;

namespace ArtGander.App.Services;

public sealed class PlatformServices : IPlatformServices
{
    public Task<string?> PickFolderAsync()
    {
        var tcs = new TaskCompletionSource<string?>();
        var picker = new UIDocumentPickerViewController([UTTypes.Folder], asCopy: false)
        {
            AllowsMultipleSelection = false,
        };
        picker.DidPickDocumentAtUrls += (_, e) =>
        {
            var url = e.Urls.FirstOrDefault();
            url?.StartAccessingSecurityScopedResource();
            tcs.TrySetResult(url?.Path);
        };
        picker.WasCancelled += (_, _) => tcs.TrySetResult(null);

        var root = Platform.GetCurrentUIViewController();
        if (root is null) return Task.FromResult<string?>(null);
        root.PresentViewController(picker, true, null);
        return tcs.Task;
    }

    public Task<IReadOnlyList<string>> PickFilesAsync(string startFolder)
    {
        var tcs = new TaskCompletionSource<IReadOnlyList<string>>();
        var picker = new UIDocumentPickerViewController([UTTypes.Item], asCopy: false)
        {
            AllowsMultipleSelection = true,
            DirectoryUrl = new NSUrl(startFolder, true),
        };
        picker.DidPickDocumentAtUrls += (_, e) =>
        {
            foreach (var url in e.Urls) url.StartAccessingSecurityScopedResource();
            tcs.TrySetResult(e.Urls.Select(u => u.Path).OfType<string>().ToList());
        };
        picker.WasCancelled += (_, _) => tcs.TrySetResult([]);

        var root = Platform.GetCurrentUIViewController();
        if (root is null) return Task.FromResult<IReadOnlyList<string>>([]);
        root.PresentViewController(picker, true, null);
        return tcs.Task;
    }

    public void RevealInFileManager(string path)
    {
        // Mac Catalyst has no direct NSWorkspace API, so open the folder itself (Finder opens it).
        var folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path)!;
        UIApplication.SharedApplication.OpenUrl(new NSUrl(folder, true), new UIApplicationOpenUrlOptions(), null);
    }

    public Task<bool> CreateThumbnailAsync(string sourcePath, string targetPath, int size)
    {
        var tcs = new TaskCompletionSource<bool>();
        try
        {
            var request = new QLThumbnailGenerationRequest(
                new NSUrl(sourcePath, false),
                new CoreGraphics.CGSize(size, size),
                UIScreen.MainScreen.Scale,
                QLThumbnailGenerationRequestRepresentationTypes.Thumbnail);

            QLThumbnailGenerator.SharedGenerator.GenerateBestRepresentation(request, (rep, error) =>
            {
                try
                {
                    var data = rep?.UIImage?.AsPNG();
                    if (data is null || error is not null) { tcs.TrySetResult(false); return; }
                    tcs.TrySetResult(data.Save(targetPath, atomically: true));
                }
                catch (Exception) { tcs.TrySetResult(false); }
            });
        }
        catch (Exception)
        {
            tcs.TrySetResult(false);
        }
        return tcs.Task;
    }

    public Task<IReadOnlyList<string>> GetDroppedFilesAsync(DropEventArgs e)
    {
        // Drag-and-drop file extraction on Mac Catalyst goes through UIDropSession item providers. Until that is
        // implemented, the "Add files…" button covers this.
        return Task.FromResult<IReadOnlyList<string>>([]);
    }
}

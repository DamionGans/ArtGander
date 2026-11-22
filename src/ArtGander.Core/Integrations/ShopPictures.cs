using System.Security.Cryptography;
using ArtGander.Core.Model;
using ArtGander.Core.Services;
using ArtGander.Core.Storage;

namespace ArtGander.Core.Integrations;

/// <summary>Downloads a picture from a shop into a file.</summary>
public delegate Task PictureDownloader(string url, string targetPath, CancellationToken ct);

/// <summary>
/// A product's pictures in the shops.
/// <list type="bullet">
/// <item>Printify products show Printify's mockups by default: ArtGander only downloads them, to have them at hand.</item>
/// <item>With "our own pictures" (always for products you make yourselves), ArtGander owns the Shopify pictures: the
/// product's pictures (except downloaded Printify mockups) are uploaded in their order with their alt text. A picture
/// with a protected copy (Mistcoat, in the Protected folder) is uploaded as that copy.</item>
/// </list>
/// Pictures are compared like the other fields, as a list of "mediaId|alt": at the last sync (base), in ArtGander and in
/// Shopify. A picture that is new or changed in ArtGander counts as "new". Pictures added in Shopify since the last sync
/// are never removed by a push; pictures removed in ArtGander that were in the shop at the last sync are taken off.
/// </summary>
public static class ShopPictures
{
    /// <summary>Only downloads from the shops' own picture servers (https).</summary>
    public static bool IsAllowedSource(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
        && (uri.Host.EndsWith(".shopify.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("cdn.shopify.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".shopifycdn.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".printify.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>Downloads with an <see cref="HttpClient"/>, only from the shops' picture servers.</summary>
    public static PictureDownloader HttpDownloader(HttpClient http) => async (url, target, ct) =>
    {
        if (!IsAllowedSource(url)) throw new InvalidOperationException($"Not downloading a picture from an unknown address: {url}");
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var file = File.Create(target);
        await source.CopyToAsync(file, ct);
    };

    // ---- Signatures ---------------------------------------------------------------------------------

    private static string Clean(string? alt) => (alt ?? "").Replace('|', '/').Replace('\n', ' ').Replace('\r', ' ').Trim();
    public static string IdOf(string entry) => entry[..entry.IndexOf('|')];
    public static string AltOf(string entry) => entry[(entry.IndexOf('|') + 1)..];

    /// <summary>The file that goes to the shop: its protected copy (Protected folder) when there is one, otherwise the picture itself.</summary>
    public static string UploadPath(Product product, ProductImage image)
    {
        var original = ProductImageService.PathOf(product, image);
        return ProtectedCopies.Find(original) ?? original;
    }

    public static List<string> RemoteSignature(RemoteProduct remote) => remote.Images.Select(i => $"{i.Id}|{Clean(i.Alt)}").ToList();

    /// <summary>The pictures that go to the shop in "our own pictures" mode: all except downloaded Printify mockups, when the file is there.</summary>
    public static IReadOnlyList<ProductImage> ShopImages(Product product)
    {
        if (ProductStore.FolderOf(product) is not { } folder) return [];
        return product.Images.Where(i => i.Source != PictureSource.Printify && File.Exists(Path.Combine(folder, i.FileName))).ToList();
    }

    public static List<string> LocalSignature(Product product) =>
        ShopImages(product).Select(i => i.Shopify is { MediaId.Length: > 0 } link && !FileChanged(product, i)
            ? $"{link.MediaId}|{Clean(i.Alt)}"
            : $"new:{i.FileName}|{Clean(i.Alt)}").ToList();

    private static string Join(IEnumerable<string>? sig) => sig is null ? "" : string.Join("\n", sig);

    /// <summary>Whether the picture's file changed since it was uploaded (or downloaded).</summary>
    public static bool FileChanged(Product product, ProductImage image)
    {
        if (image.Shopify is not { Hash.Length: > 0 } link) return true;
        var path = UploadPath(product, image);
        var info = new FileInfo(path);
        if (!info.Exists) return true;
        if (info.Length == link.Size && link.Modified is { } m && m == new DateTimeOffset(info.LastWriteTimeUtc)) return false;
        return FileHash(path) != link.Hash;
    }

    public static string FileHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream))[..32];
    }

    private static ShopifyMediaLink LinkFor(string mediaId, string path, string alt, bool uploaded)
    {
        var info = new FileInfo(path);
        return new ShopifyMediaLink
        {
            MediaId = mediaId,
            Hash = FileHash(path),
            Size = info.Length,
            Modified = new DateTimeOffset(info.LastWriteTimeUtc),
            UploadedByArtGander = uploaded,
            Alt = alt,
        };
    }

    // ---- The sync preview ---------------------------------------------------------------------------

    /// <summary>The "Pictures" change for the sync preview, or null when ArtGander and Shopify show the same.</summary>
    public static SyncFieldChange? Compare(Product product, RemoteProduct shopify, List<string>? baseSig, bool review)
    {
        var local = LocalSignature(product);
        var remote = RemoteSignature(shopify);
        if (Join(local) == Join(remote)) return null;
        var start = baseSig ?? remote;
        var localChanged = Join(local) != Join(start);
        var remoteChanged = Join(remote) != Join(start);
        SyncChangeKind? kind = review ? SyncChangeKind.Conflict : (localChanged, remoteChanged) switch
        {
            (true, false) => SyncChangeKind.Push,
            (false, true) => SyncChangeKind.OutsideEdit,
            (true, true) => SyncChangeKind.Conflict,
            _ => null,
        };
        if (kind is null) return null;
        return new SyncFieldChange("shopify", "pictures", null, "Pictures",
            baseSig is null ? null : $"{baseSig.Count} picture(s) at the last sync",
            DescribeLocal(product, remote, start),
            DescribeRemote(product, shopify, start),
            kind.Value);
    }

    private static string DescribeLocal(Product product, List<string> remote, List<string> start)
    {
        var images = ShopImages(product);
        if (images.Count == 0) return "No pictures";
        var remoteIds = remote.Select(IdOf).ToHashSet();
        var parts = images.Select(i => i.Shopify is { MediaId.Length: > 0 } l && remoteIds.Contains(l.MediaId)
            ? FileChanged(product, i) ? $"{i.Label} (changed)" : i.Label
            : $"{i.Label} (new)");
        var mine = images.Select(i => i.Shopify?.MediaId).OfType<string>().ToHashSet();
        var removed = start.Select(IdOf).Count(id => remoteIds.Contains(id) && !mine.Contains(id));
        return $"{images.Count} picture(s): {string.Join(", ", parts)}" + (removed > 0 ? $"; {removed} taken off the shop" : "");
    }

    private static string DescribeRemote(Product product, RemoteProduct shopify, List<string> start)
    {
        if (shopify.Images.Count == 0) return "No pictures";
        var known = start.Select(IdOf).ToHashSet();
        var parts = shopify.Images.Select(m =>
            product.Images.FirstOrDefault(i => i.Shopify?.MediaId == m.Id) is { } local ? local.Label
            : !known.Contains(m.Id) ? (m.Alt.Length > 0 ? $"\"{m.Alt}\" (added in Shopify)" : "a picture added in Shopify")
            : m.Alt.Length > 0 ? $"\"{m.Alt}\"" : "a shop picture");
        return $"{shopify.Images.Count} picture(s): {string.Join(", ", parts)}";
    }

    // ---- Writing ------------------------------------------------------------------------------------

    /// <summary>
    /// Makes the Shopify product show ArtGander's pictures: uploads new and changed ones, updates alt texts, takes off
    /// pictures that were in the shop at the last sync but aren't in ArtGander anymore, and puts everything in order.
    /// Pictures added in Shopify since the last sync stay (at the end). Returns the new base.
    /// </summary>
    public static async Task<List<string>> PushAsync(Product product, string shopifyProductId, RemoteProduct current, List<string>? baseSig,
        IShopifyApi shopify, CancellationToken ct)
    {
        var remoteById = current.Images.ToDictionary(i => i.Id);
        var known = (baseSig ?? RemoteSignature(current)).Select(IdOf).ToHashSet();
        var images = ShopImages(product);
        var upload = new List<ProductImage>();
        var alts = new List<(string, string)>();
        var remove = new List<string>();
        foreach (var image in images)
        {
            if (image.Shopify is { MediaId.Length: > 0 } link && remoteById.TryGetValue(link.MediaId, out var media))
            {
                if (!FileChanged(product, image))
                {
                    if (Clean(media.Alt) != Clean(image.Alt)) alts.Add((link.MediaId, image.Alt));
                    link.Alt = image.Alt;
                    continue;
                }
                remove.Add(link.MediaId); // the picture changed: replace it
            }
            upload.Add(image);
        }
        var mine = images.Select(i => i.Shopify?.MediaId).OfType<string>().ToHashSet();
        foreach (var media in current.Images)
            if (!mine.Contains(media.Id) && known.Contains(media.Id) && !remove.Contains(media.Id))
                remove.Add(media.Id); // taken out in ArtGander since the last sync

        if (upload.Count > 0)
        {
            var ids = await shopify.AddMediaAsync(shopifyProductId, upload.Select(i => (UploadPath(product, i), i.Alt)).ToList(), ct);
            if (ids.Count != upload.Count) throw new ShopifyException("Shopify didn't confirm every uploaded picture.");
            for (var i = 0; i < upload.Count; i++)
            {
                upload[i].Shopify = LinkFor(ids[i], UploadPath(product, upload[i]), upload[i].Alt, uploaded: true);
            }
        }
        await shopify.UpdateMediaAltAsync(alts, ct);
        await shopify.RemoveMediaAsync(shopifyProductId, remove, ct);

        var removed = remove.ToHashSet();
        var foreign = current.Images.Where(m => !mine.Contains(m.Id) && !removed.Contains(m.Id)).ToList();
        var order = images.Select(i => i.Shopify!.MediaId).Concat(foreign.Select(m => m.Id)).ToList();
        await shopify.ReorderMediaAsync(shopifyProductId, order, ct);
        return images.Select(i => $"{i.Shopify!.MediaId}|{Clean(i.Alt)}")
            .Concat(foreign.Select(m => $"{m.Id}|{Clean(m.Alt)}")).ToList();
    }

    /// <summary>
    /// Takes the Shopify pictures over into ArtGander: downloads new ones into the product's folder, takes over alt texts
    /// and the order, and stops using pictures that were removed in Shopify (their files stay). Returns the new base.
    /// </summary>
    public static async Task<List<string>> TakeOverAsync(Product product, RemoteProduct shopify, PictureDownloader download, CancellationToken ct)
    {
        var ordered = new List<ProductImage>();
        for (var n = 0; n < shopify.Images.Count; n++)
        {
            var media = shopify.Images[n];
            var local = product.Images.FirstOrDefault(i => i.Shopify?.MediaId == media.Id);
            if (local is null)
            {
                local = await DownloadAsync(product, media.Url, LabelFromAlt(media.Alt, $"shop-{n + 1}"), ProductImageKind.Photo, PictureSource.Shopify, download, ct);
                local.Shopify = LinkFor(media.Id, ProductImageService.PathOf(product, local), media.Alt, uploaded: false);
            }
            local.Alt = media.Alt;
            local.Shopify!.Alt = media.Alt;
            ordered.Add(local);
        }
        var inShop = shopify.Images.Select(m => m.Id).ToHashSet();
        foreach (var gone in product.Images.Where(i => i.Source != PictureSource.Printify && i.Shopify is { MediaId.Length: > 0 } l && !inShop.Contains(l.MediaId)).ToList())
            ProductImageService.Untrack(product, gone);
        Reorder(product, ordered);
        return RemoteSignature(shopify);
    }

    /// <summary>
    /// Brings the shop's pictures into ArtGander (on import, or with "Get pictures from the shop"): Printify's mockups
    /// for products that show them, otherwise the Shopify pictures not in ArtGander yet. Returns how many were added.
    /// </summary>
    public static async Task<int> DownloadFromShopAsync(Product product, RemoteProduct? printify, RemoteProduct? shopify, PictureDownloader download, CancellationToken ct)
    {
        var added = 0;
        if (product.EffectivePictures == PictureMode.PrintifyMockups && printify is not null)
        {
            var mockups = printify.Images.Where(i => i.Selected).ToList();
            var wanted = mockups.Select(m => m.Url).ToHashSet();
            // Mockups Printify no longer shows: these are downloaded copies, so they are removed.
            foreach (var old in product.Images.Where(i => i.Source == PictureSource.Printify && !wanted.Contains(i.PrintifySrc ?? "")).ToList())
            {
                ProductImageService.Untrack(product, old);
                try { File.Delete(ProductImageService.PathOf(product, old)); } catch (IOException) { }
            }
            var ordered = new List<ProductImage>();
            foreach (var mockup in mockups)
            {
                var local = product.Images.FirstOrDefault(i => i.Source == PictureSource.Printify && i.PrintifySrc == mockup.Url);
                if (local is null)
                {
                    local = await DownloadAsync(product, mockup.Url, "mockup-" + CameraLabel(mockup.Url, ordered.Count + 1), ProductImageKind.Mockup, PictureSource.Printify, download, ct);
                    local.PrintifySrc = mockup.Url;
                    added++;
                }
                ordered.Add(local);
            }
            Reorder(product, ordered);
        }
        else if (shopify is not null)
        {
            for (var n = 0; n < shopify.Images.Count; n++)
            {
                var media = shopify.Images[n];
                if (product.Images.Any(i => i.Shopify?.MediaId == media.Id)) continue;
                var local = await DownloadAsync(product, media.Url, LabelFromAlt(media.Alt, $"shop-{n + 1}"), ProductImageKind.Photo, PictureSource.Shopify, download, ct);
                local.Alt = media.Alt;
                local.Shopify = LinkFor(media.Id, ProductImageService.PathOf(product, local), media.Alt, uploaded: false);
                added++;
            }
        }
        return added;
    }

    private static async Task<ProductImage> DownloadAsync(Product product, string url, string label, string kind, PictureSource source,
        PictureDownloader download, CancellationToken ct)
    {
        var ext = Path.GetExtension(new Uri(url).AbsolutePath).ToLowerInvariant();
        if (!ProductImageService.IsImage("x" + ext)) ext = ".jpg";
        var temp = Path.Combine(Path.GetTempPath(), "ArtGander", "downloads", Guid.NewGuid().ToString("N") + ext);
        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
        try
        {
            await download(url, temp, ct);
            var image = ProductImageService.Import(product, temp, label, kind, move: true);
            image.Source = source;
            return image;
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
        }
    }

    /// <summary>Puts <paramref name="first"/> at the front, in that order; the other pictures keep their order after them.</summary>
    private static void Reorder(Product product, List<ProductImage> first)
    {
        for (var i = 0; i < first.Count; i++)
        {
            var at = product.Images.IndexOf(first[i]);
            if (at >= 0 && at != i) product.Images.Move(at, i);
        }
    }

    private static string LabelFromAlt(string alt, string fallback)
    {
        var label = Naming.NameRules.ToVersionLabel(alt);
        return label.Length is > 0 and <= 40 ? label : fallback;
    }

    /// <summary>Printify mockup addresses end in "?camera_label=front": that makes the label.</summary>
    private static string CameraLabel(string url, int n)
    {
        var query = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Query : "";
        foreach (var part in query.TrimStart('?').Split('&'))
            if (part.StartsWith("camera_label=", StringComparison.OrdinalIgnoreCase) && part.Length > 13)
                return Naming.NameRules.ToVersionLabel(Uri.UnescapeDataString(part[13..]));
        return n.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}

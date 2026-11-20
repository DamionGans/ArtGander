using System.Text.Json;
using ArtGander.Core.Model;
using ArtGander.Core.Schema;

namespace ArtGander.Core.Storage;

/// <summary>Reads and writes <c>{ArtRoot}/.artgander/library.json</c>.</summary>
public static class LibrarySettingsStore
{
    public static string PathFor(string rootPath) =>
        Path.Combine(rootPath, CurrentSchema.LibraryFolderName, CurrentSchema.LibraryFileName);

    public static LibrarySettings Load(string rootPath)
    {
        var path = PathFor(rootPath);
        if (!File.Exists(path)) return new LibrarySettings();
        try
        {
            return ArtGanderJson.DeserializeLibrary(AtomicFile.DecodeUtf8(AtomicFile.ReadAllBytes(path))) ?? new LibrarySettings();
        }
        catch (JsonException)
        {
            return new LibrarySettings();
        }
    }

    public static void Save(string rootPath, LibrarySettings settings)
    {
        var path = PathFor(rootPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.MarkHidden(Path.GetDirectoryName(path)!);
        AtomicFile.WriteAllText(path, ArtGanderJson.Serialize(settings));
    }
}

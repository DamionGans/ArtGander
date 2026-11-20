namespace ArtGander.Core.Tests;

/// <summary>A throwaway art folder for one test.</summary>
public sealed class TempLibrary : IDisposable
{
    public TempLibrary()
    {
        Root = Path.Combine(Path.GetTempPath(), "artgander-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Folder(string name)
    {
        var path = Path.Combine(Root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    public string WriteFile(string relativePath, string content = "x")
    {
        var path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }
}

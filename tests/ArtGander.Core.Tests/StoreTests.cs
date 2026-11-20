using System.Text.Json.Nodes;
using ArtGander.Core.Model;
using ArtGander.Core.Schema;
using ArtGander.Core.Schema.Migrations;
using ArtGander.Core.Storage;

namespace ArtGander.Core.Tests;

public class StoreTests
{
    [Fact]
    public void Round_trips_and_keeps_unknown_properties()
    {
        using var lib = new TempLibrary();
        var folder = lib.Folder("Goose");
        File.WriteAllText(Path.Combine(folder, ".artgander.json"), """
            {
              "schemaVersion": 1,
              "title": "Goose",
              "futureField": { "a": 1 },
              "copyright": { "owner": "Me", "status": "registered", "futureNested": true },
              "products": [ { "name": "Mug", "type": "printOnDemand", "files": ["Goose-transparent.png"], "price": 18.5 } ]
            }
            """);

        var store = new ArtPieceStore();
        var doc = store.Load(folder);
        Assert.Equal(CopyrightStatus.Registered, doc.Piece.Copyright.Status);
        Assert.Equal(ProductType.PrintOnDemand, doc.Piece.Products[0].Type);
        Assert.False(doc.HasUnsavedChanges);

        doc.Piece.Description = "Honk";
        Assert.True(doc.HasUnsavedChanges);
        store.Save(doc);

        var json = File.ReadAllText(Path.Combine(folder, ".artgander.json"));
        Assert.Contains("\"futureField\"", json);
        Assert.Contains("\"futureNested\"", json);
        Assert.Contains("\"printOnDemand\"", json);
        Assert.DoesNotContain("\r\n", json);
        Assert.Equal("Honk", store.Load(folder).Piece.Description);
    }

    [Fact]
    public void Writes_non_ascii_readably()
    {
        using var lib = new TempLibrary();
        var store = new ArtPieceStore();
        var doc = store.Create(lib.Folder("Café"), new ArtPiece { Title = "Café Crème" });
        Assert.Contains("Café Crème", File.ReadAllText(doc.FilePath));
    }

    [Fact]
    public void Detects_edits_from_another_computer()
    {
        using var lib = new TempLibrary();
        var store = new ArtPieceStore();
        var mine = store.Create(lib.Folder("Goose"), new ArtPiece { Title = "Goose" });
        var theirs = store.Load(mine.FolderPath);

        theirs.Piece.Notes = "edited on the Mac";
        store.Save(theirs);

        mine.Piece.Notes = "edited on Windows";
        Assert.True(store.ChangedOnDisk(mine));
        Assert.Throws<ConcurrentEditException>(() => store.Save(mine));
        store.Save(mine, force: true);
        Assert.Equal("edited on Windows", store.Load(mine.FolderPath).Piece.Notes);
    }

    [Fact]
    public void Upgrades_old_files_and_keeps_backup()
    {
        using var lib = new TempLibrary();
        var folder = lib.Folder("Goose");
        File.WriteAllText(Path.Combine(folder, ".artgander.json"), """{ "schemaVersion": 1, "title": "Goose", "size": "30x40" }""");

        var store = new ArtPieceStore(new SchemaMigrator([new FakeV1ToV2()], targetVersion: 2));
        var doc = store.Load(folder);

        Assert.Equal(1, doc.UpgradedFromVersion);
        Assert.Equal("30x40", doc.Piece.Artwork.Dimensions);
        var saved = JsonNode.Parse(File.ReadAllText(doc.FilePath))!;
        Assert.Equal(2, saved["schemaVersion"]!.GetValue<int>());
        Assert.Null(saved["size"]);
        Assert.True(File.Exists(Path.Combine(folder, ".artgander.json.v1.bak")));

        // A second load finds nothing to upgrade.
        Assert.Null(store.Load(folder).UpgradedFromVersion);
    }

    [Fact]
    public void Files_from_newer_versions_open_read_only()
    {
        using var lib = new TempLibrary();
        var folder = lib.Folder("Goose");
        var original = """{ "schemaVersion": 99, "title": "Goose", "artwork": { "originalStatus": "teleported" } }""";
        File.WriteAllText(Path.Combine(folder, ".artgander.json"), original);

        var store = new ArtPieceStore();
        var doc = store.Load(folder);
        Assert.True(doc.IsReadOnly);
        Assert.Equal("Goose", doc.Piece.Title);
        Assert.Throws<InvalidOperationException>(() => store.Save(doc));
        Assert.Equal(original, File.ReadAllText(doc.FilePath));
    }

    [Fact]
    public void Invalid_json_reports_a_clear_error()
    {
        using var lib = new TempLibrary();
        var folder = lib.Folder("Broken");
        File.WriteAllText(Path.Combine(folder, ".artgander.json"), "{ \"title\": ");
        var ex = Assert.Throws<ArtPieceFileException>(() => new ArtPieceStore().Load(folder));
        Assert.Contains("invalid JSON", ex.Message);
    }

    [Fact]
    public void Details_file_is_hidden()
    {
        using var lib = new TempLibrary();
        var store = new ArtPieceStore();
        var doc = store.Create(lib.Folder("Goose"), new ArtPiece { Title = "Goose" });
        doc.Piece.Notes = "saved again";
        store.Save(doc);

        Assert.Equal(".artgander.json", Path.GetFileName(doc.FilePath));
        if (OperatingSystem.IsWindows())
            Assert.True(File.GetAttributes(doc.FilePath).HasFlag(FileAttributes.Hidden));
    }

    [Fact]
    public void Visible_details_file_from_early_builds_is_renamed_and_hidden()
    {
        using var lib = new TempLibrary();
        var folder = lib.Folder("Goose");
        File.WriteAllText(Path.Combine(folder, "artgander.json"), """{ "schemaVersion": 1, "title": "Goose" }""");
        Assert.True(ArtPieceStore.HasPieceFile(folder));

        var doc = new ArtPieceStore().Load(folder);

        Assert.Equal("Goose", doc.Piece.Title);
        Assert.False(File.Exists(Path.Combine(folder, "artgander.json")));
        Assert.True(File.Exists(Path.Combine(folder, ".artgander.json")));
        if (OperatingSystem.IsWindows())
            Assert.True(File.GetAttributes(doc.FilePath).HasFlag(FileAttributes.Hidden));
    }

    [Fact]
    public void Library_folder_is_hidden()
    {
        using var lib = new TempLibrary();
        LibrarySettingsStore.Save(lib.Root, new LibrarySettings());
        if (OperatingSystem.IsWindows())
            Assert.True(File.GetAttributes(Path.Combine(lib.Root, ".artgander")).HasFlag(FileAttributes.Hidden));
    }

    [Fact]
    public void Missing_migration_is_a_programming_error() =>
        Assert.Throws<InvalidOperationException>(() => new SchemaMigrator([], targetVersion: 3));

    private sealed class FakeV1ToV2 : IMigration
    {
        public int FromVersion => 1;

        public void Apply(JsonObject root)
        {
            var size = root["size"]?.GetValue<string>();
            root.Remove("size");
            root["artwork"] = new JsonObject { ["dimensions"] = size };
        }
    }
}

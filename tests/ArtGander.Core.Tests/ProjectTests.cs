using System.Text.Json.Nodes;
using ArtGander.Core.Library;
using ArtGander.Core.Model;
using ArtGander.Core.Services;
using ArtGander.Core.Storage;

namespace ArtGander.Core.Tests;

public class ProjectTests
{
    private readonly ArtPieceStore _store = new();
    private PieceService Service => new(_store);
    private LibraryScanner Scanner => new(_store);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Version_1_files_become_finished_pieces()
    {
        using var lib = new TempLibrary();
        var folder = lib.Folder("Goose");
        lib.WriteFile("Goose/.artgander.json", """{ "schemaVersion": 1, "title": "Goose" }""");

        var doc = _store.Load(folder);

        Assert.Equal(1, doc.UpgradedFromVersion);
        Assert.Equal(ItemType.Piece, doc.Piece.Type);
        Assert.Equal(WorkStatus.Finished, doc.Piece.Status);
        var json = JsonNode.Parse(File.ReadAllText(doc.FilePath))!;
        Assert.Equal(ArtGander.Core.Schema.CurrentSchema.Version, json["schemaVersion"]!.GetValue<int>());
        Assert.Equal("piece", json["type"]!.GetValue<string>());
        Assert.Null(json["isProject"]); // computed properties are not written
        Assert.True(File.Exists(Path.Combine(folder, ".artgander.json.v1.bak")));
    }

    [Fact]
    public void Classifier_suggests_projects_for_folders_with_art_subfolders()
    {
        using var lib = new TempLibrary();
        lib.WriteFile("Book/book text.docx");
        lib.WriteFile("Book/Page 1/page1.png");
        lib.WriteFile("Book/Page 2/page2.tif");
        lib.WriteFile("Single/painting.jpg");
        lib.WriteFile("Single/exports/small.jpg"); // one subfolder with art is not enough

        Assert.Equal(ItemType.Project, FolderClassifier.Suggest(Path.Combine(lib.Root, "Book")));
        Assert.Equal(ItemType.Piece, FolderClassifier.Suggest(Path.Combine(lib.Root, "Single")));

        var scanned = Scanner.Scan(lib.Root, Ct);
        Assert.Equal(ItemType.Project, scanned.Single(p => p.FolderName == "Book").SuggestedType);
        Assert.Equal(ItemType.Piece, scanned.Single(p => p.FolderName == "Single").SuggestedType);
    }

    [Fact]
    public void Adopting_a_project_tracks_its_pieces_in_natural_order()
    {
        using var lib = new TempLibrary();
        lib.WriteFile("Book/Book-text.docx");
        lib.WriteFile("Book/Page 10/a.png");
        lib.WriteFile("Book/Page 2/a.png");
        lib.WriteFile("Book/Page 1/a.png");

        var project = Service.AdoptFolder(Path.Combine(lib.Root, "Book"));

        Assert.True(project.Piece.IsProject);
        Assert.Single(project.Piece.Files); // the book text is a project file
        var book = Assert.Single(Scanner.Scan(lib.Root, Ct));
        Assert.Equal(["Page 1", "Page 2", "Page 10"], book.Children.Select(c => c.Title));
        Assert.All(book.Children, c => Assert.Equal(ItemType.Piece, c.Type));
        Assert.All(book.Children, c => Assert.Equal("Book › " + c.Title, c.Document!.DisplayTitle));
    }

    [Fact]
    public void Pieces_follow_the_project_order()
    {
        using var lib = new TempLibrary();
        var project = Service.CreateProject(lib.Root, "Goose Book");
        var a = Service.CreatePieceInProject(project, "Alpha");
        var b = Service.CreatePieceInProject(project, "Beta", planned: true);
        Assert.Equal(WorkStatus.Planned, b.Piece.Status);

        Service.ReorderPieces(project, [b.Piece.Id, a.Piece.Id]);

        var scanned = Assert.Single(Scanner.Scan(lib.Root, Ct));
        Assert.Equal(["Beta", "Alpha"], scanned.Children.Select(c => c.Title));
    }

    [Fact]
    public void Moving_pieces_between_library_and_projects_updates_order()
    {
        using var lib = new TempLibrary();
        var piece = Service.CreatePiece(lib.Root, "Goose Pond");
        var project = Service.CreateProject(lib.Root, "Goose Book");

        Service.MovePiece(piece, project.FolderPath);

        Assert.Equal(Path.Combine(project.FolderPath, "Goose Pond"), piece.FolderPath);
        Assert.Contains(piece.Piece.Id, _store.Load(project.FolderPath).Piece.PieceOrder);

        Service.MovePiece(piece, lib.Root);

        Assert.True(Directory.Exists(Path.Combine(lib.Root, "Goose Pond")));
        Assert.DoesNotContain(piece.Piece.Id, _store.Load(project.FolderPath).Piece.PieceOrder);
        Assert.Throws<InvalidOperationException>(() => Service.MovePiece(_store.Load(project.FolderPath), piece.FolderPath));
    }

    [Fact]
    public void Group_selected_pieces_into_a_new_project()
    {
        using var lib = new TempLibrary();
        var a = Service.CreatePiece(lib.Root, "A");
        var b = Service.CreatePiece(lib.Root, "B");

        var project = Service.GroupIntoProject(lib.Root, "Set", [a.FolderPath, b.FolderPath]);

        Assert.True(project.Piece.IsProject);
        Assert.Equal([a.Piece.Id, b.Piece.Id], project.Piece.PieceOrder);
        Assert.True(Directory.Exists(Path.Combine(lib.Root, "Set", "A")));
        Assert.False(Directory.Exists(Path.Combine(lib.Root, "A")));
        Assert.Throws<IOException>(() => Service.GroupIntoProject(lib.Root, "Set", [])); // the name is taken now
    }

    [Fact]
    public void Group_rollback_restores_moved_pieces()
    {
        using var lib = new TempLibrary();
        var a = Service.CreatePiece(lib.Root, "A");
        var project = Service.CreateProject(lib.Root, "Other");
        var b = Service.CreatePieceInProject(project, "A"); // same folder name as the stand-alone "A"
        var c = Service.CreatePiece(lib.Root, "C");

        // Moving C and then B (from "Other") works, but A clashes with B's folder name: everything goes back.
        Assert.Throws<IOException>(() => Service.GroupIntoProject(lib.Root, "New", [c.FolderPath, b.FolderPath, a.FolderPath]));

        Assert.True(Directory.Exists(Path.Combine(lib.Root, "C")));
        Assert.True(Directory.Exists(Path.Combine(lib.Root, "Other", "A")));
        Assert.True(Directory.Exists(Path.Combine(lib.Root, "A")));
        Assert.False(Directory.Exists(Path.Combine(lib.Root, "New")));
        Assert.Contains(b.Piece.Id, _store.Load(project.FolderPath).Piece.PieceOrder);
    }

    [Fact]
    public void Convert_piece_with_art_subfolders_to_project()
    {
        using var lib = new TempLibrary();
        lib.WriteFile("Book/cover.png");
        var doc = Service.AdoptFolder(Path.Combine(lib.Root, "Book"), type: ItemType.Piece);
        lib.WriteFile("Book/Page 1/a.png");
        lib.WriteFile("Book/Page 2/a.png");

        var before = Assert.Single(Scanner.Scan(lib.Root, Ct));
        Assert.True(before.LooksLikeProject);

        Service.ConvertToProject(_store.Load(doc.FolderPath));

        var after = Assert.Single(Scanner.Scan(lib.Root, Ct));
        Assert.True(after.IsProject);
        Assert.Equal(2, after.Children.Count);
        Assert.EndsWith("cover.png", after.PreviewFilePath);
    }

    [Fact]
    public void Bundle_products_add_up_their_pieces_costs()
    {
        using var lib = new TempLibrary();
        var project = Service.CreateProject(lib.Root, "Goose Book");
        var a = Service.CreatePieceInProject(project, "Page 1");
        var b = Service.CreatePieceInProject(project, "Page 2");
        var printA = new Product { Name = "A4 print", ProductionCost = 4, Pieces = [a.Piece.Id] };
        var printB = new Product { Name = "A4 print", ProductionCost = 5, Pieces = [b.Piece.Id] };
        var set = new Product
        {
            Name = "Set of prints",
            Pieces = [project.Piece.Id],
            Includes = { new BundleItem { PieceId = a.Piece.Id, ProductId = printA.Id }, new BundleItem { PieceId = b.Piece.Id, ProductId = printB.Id, Quantity = 2 } },
        };
        var order1 = new Order { OrderRef = "#1" };
        order1.Lines.Add(new OrderLine { ProductId = set.Id, UnitPrice = 40 });
        order1.Lines.Add(new OrderLine { ProductId = printA.Id, UnitPrice = 15 });
        a.ProjectTitle = "Goose Book";

        var order = Assert.Single(SalesReport.Orders([order1], [printA, printB, set], [project, a, b]));
        var rows = order.Lines;

        var setRow = rows.Single(r => r.Product == set);
        Assert.Equal(14, setRow.Cost); // 4 + 2 x 5
        Assert.Equal("Goose Book › Page 1", rows.Single(r => r.Product == printA).PieceTitle);
        Assert.Equal(55, order.Revenue);
        Assert.Equal(2, SalesReport.ForPiece(rows, a.Piece.Id).Count); // directly, and as part of the set
    }
}

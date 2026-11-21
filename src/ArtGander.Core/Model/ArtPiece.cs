using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using ArtGander.Core.Naming;
using ArtGander.Core.Schema;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ArtGander.Core.Model;

/// <summary>
/// The root object of a <c>.artgander.json</c> file. It describes either an <b>art piece</b> or a <b>project</b>
/// (see <see cref="Type"/>). A project is a folder that holds project-only files plus one subfolder per art piece.
/// </summary>
public sealed partial class ArtPiece : ModelBase
{
    /// <summary>The file's structure version. Upgraded automatically when the file is loaded.</summary>
    public int SchemaVersion { get; set; } = CurrentSchema.Version;

    /// <summary>A stable identity that survives folder renames.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Whether this folder is a single art piece or a project (a group of pieces, e.g. a book).</summary>
    [ObservableProperty] public partial ItemType Type { get; set; } = ItemType.Piece;

    /// <summary>Where the work stands. "planned" pieces are placeholders that have no files yet.</summary>
    [ObservableProperty] public partial WorkStatus Status { get; set; } = WorkStatus.InProgress;

    [ObservableProperty] public partial string Title { get; set; } = "";
    [ObservableProperty] public partial string Description { get; set; } = "";
    public ObservableCollection<string> Tags { get; set; } = [];
    public ObservableCollection<string> Collections { get; set; } = [];

    [ObservableProperty] public partial DateOnly? CreatedOn { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
    public string? UpdatedWith { get; set; }

    public Artwork Artwork { get; set; } = new();
    public Copyright Copyright { get; set; } = new();
    public ObservableCollection<FileEntry> Files { get; set; } = [];

    /// <summary>
    /// Products and sales from format version 2, when they were stored inside the piece. ArtGander moves them to
    /// product and order files (<see cref="Services.LibraryMigrator"/>) and then clears these. Never written when empty.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<LegacyProduct>? LegacyProducts { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<LegacySale>? LegacySales { get; set; }

    [ObservableProperty] public partial string Notes { get; set; } = "";

    /// <summary>Projects only: the order of the pieces (e.g. book pages), by piece id. Pieces not listed come after, by title.</summary>
    public ObservableCollection<Guid> PieceOrder { get; set; } = [];

    [JsonIgnore] public bool IsProject => Type == ItemType.Project;

    public FileEntry? FindFile(string fileName) =>
        Files.FirstOrDefault(f => NameRules.NamesEqual(f.FileName, fileName));
}

/// <summary>The physical (or original digital) work itself.</summary>
public sealed partial class Artwork : ModelBase
{
    [ObservableProperty] public partial string Medium { get; set; } = "";
    [ObservableProperty] public partial string Dimensions { get; set; } = "";
    [ObservableProperty] public partial bool IsDigitalOnly { get; set; }
    [ObservableProperty] public partial OriginalStatus OriginalStatus { get; set; } = OriginalStatus.Available;
    [ObservableProperty] public partial decimal? OriginalPrice { get; set; }
    [ObservableProperty] public partial string SoldTo { get; set; } = "";
    [ObservableProperty] public partial DateOnly? SoldOn { get; set; }
}

public sealed partial class Copyright : ModelBase
{
    [ObservableProperty] public partial string Owner { get; set; } = "";
    [ObservableProperty] public partial int? Year { get; set; }
    [ObservableProperty] public partial CopyrightStatus Status { get; set; } = CopyrightStatus.Unregistered;
    [ObservableProperty] public partial string RegistrationNumber { get; set; } = "";
    [ObservableProperty] public partial DateOnly? RegisteredOn { get; set; }
    [ObservableProperty] public partial string Office { get; set; } = "";
    [ObservableProperty] public partial string Notes { get; set; } = "";
}

/// <summary>A file in the piece folder. <see cref="Version"/> is the label in the file name (Name-<b>version</b>.ext).</summary>
public sealed partial class FileEntry : ModelBase
{
    [ObservableProperty] public partial string FileName { get; set; } = "";
    [ObservableProperty] public partial string Version { get; set; } = "";
    /// <summary>What kind of version this is, e.g. "transparent". One of the library's file kinds (Settings).</summary>
    [ObservableProperty] public partial string Kind { get; set; } = FileKind.Other;
    [ObservableProperty] public partial string Notes { get; set; } = "";
    public DateTimeOffset? AddedAt { get; set; }
}

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ArtGander.Core.Model;

/// <summary>
/// Library-wide settings stored in <c>{ArtRoot}/.artgander/library.json</c>.
/// This file only holds shared defaults. Art piece data always lives in the piece's own folder.
/// </summary>
public sealed partial class LibrarySettings : ModelBase
{
    public int SchemaVersion { get; set; } = 1;
    [ObservableProperty] public partial string DefaultCurrency { get; set; } = "EUR";
    [ObservableProperty] public partial string DefaultCopyrightOwner { get; set; } = "";
    public ObservableCollection<CollectionInfo> Collections { get; set; } = [];
    public ObservableCollection<string> Channels { get; set; } = ["shopify", "printify", "inPerson", "other"];

    /// <summary>The kinds a file version can have, e.g. photo, master, transparent. Editable in Settings.</summary>
    public ObservableCollection<string> FileKinds { get; set; } = [.. FileKind.Defaults];
}

public sealed partial class CollectionInfo : ModelBase
{
    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string Description { get; set; } = "";
}

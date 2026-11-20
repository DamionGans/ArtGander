namespace ArtGander.Core.Schema;

/// <summary>
/// The structure version of <c>artgander.json</c> files.
/// <para>
/// To change the structure:
/// 1. Bump <see cref="Version"/>.
/// 2. Add a migration class in <c>Schema/Migrations</c> with <c>FromVersion = old version</c>.
/// 3. Register it in <see cref="Migrations.MigrationCatalog.All"/>.
/// 4. Document the change in <c>docs/file-format.md</c>.
/// Files are then upgraded automatically the next time ArtGander opens them. A backup of the old file is kept.
/// </para>
/// </summary>
public static class CurrentSchema
{
    public const int Version = 1;

    /// <summary>The name of the details file inside every art piece folder.</summary>
    public const string PieceFileName = "artgander.json";

    /// <summary>The hidden library-level folder (shared settings only).</summary>
    public const string LibraryFolderName = ".artgander";

    public const string LibraryFileName = "library.json";
}

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
    public const int Version = 3;

    /// <summary>
    /// The name of the details file inside every art piece folder. The leading dot hides it on macOS/Linux,
    /// and ArtGander sets the Hidden attribute on Windows, so the art folders show only the art.
    /// </summary>
    public const string PieceFileName = ".artgander.json";

    /// <summary>The visible name used by the first builds. Found files are renamed to <see cref="PieceFileName"/> and hidden.</summary>
    public const string LegacyPieceFileName = "artgander.json";

    /// <summary>The hidden library-level folder (shared settings and order details).</summary>
    public const string LibraryFolderName = ".artgander";

    public const string LibraryFileName = "library.json";
}

using System.Text.Json.Nodes;
using ArtGander.Core.Schema.Migrations;

namespace ArtGander.Core.Schema;

/// <summary>Runs the migration chain on a raw <c>artgander.json</c> tree.</summary>
public sealed class SchemaMigrator
{
    private readonly Dictionary<int, IMigration> _byVersion;

    public SchemaMigrator(IEnumerable<IMigration> migrations, int targetVersion)
    {
        _byVersion = migrations.ToDictionary(m => m.FromVersion);
        TargetVersion = targetVersion;
        for (var v = 1; v < targetVersion; v++)
        {
            if (!_byVersion.ContainsKey(v))
                throw new InvalidOperationException($"No migration registered from schema version {v} to {v + 1}.");
        }
    }

    public static SchemaMigrator Default { get; } = new(MigrationCatalog.All, CurrentSchema.Version);

    public int TargetVersion { get; }

    /// <summary>Reads the schema version. Files without one are treated as version 1, the first public format.</summary>
    public static int ReadVersion(JsonObject root) =>
        root["schemaVersion"] is JsonValue v && v.TryGetValue(out int version) ? version : 1;

    /// <summary>Upgrades <paramref name="root"/> in place to <see cref="TargetVersion"/> and returns the version it started at.</summary>
    /// <exception cref="NewerSchemaException">The file comes from a newer ArtGander.</exception>
    public int Migrate(JsonObject root)
    {
        var original = ReadVersion(root);
        if (original > TargetVersion)
            throw new NewerSchemaException(original, TargetVersion);

        for (var v = original; v < TargetVersion; v++)
        {
            _byVersion[v].Apply(root);
            root["schemaVersion"] = v + 1;
        }
        return original;
    }
}

public sealed class NewerSchemaException(int fileVersion, int supportedVersion)
    : Exception($"This file uses format version {fileVersion}, but this ArtGander only understands up to version {supportedVersion}. Please update ArtGander.")
{
    public int FileVersion { get; } = fileVersion;
    public int SupportedVersion { get; } = supportedVersion;
}

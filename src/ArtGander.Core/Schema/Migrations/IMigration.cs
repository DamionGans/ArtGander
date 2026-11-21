using System.Text.Json.Nodes;

namespace ArtGander.Core.Schema.Migrations;

/// <summary>
/// Upgrades the raw JSON of an <c>artgander.json</c> file from <see cref="FromVersion"/> to <c>FromVersion + 1</c>.
/// Migrations work on the raw JSON tree, so they don't depend on the current C# model.
/// </summary>
public interface IMigration
{
    int FromVersion { get; }

    void Apply(JsonObject root);
}

/// <summary>Every built-in migration, in order. Add new ones here.</summary>
public static class MigrationCatalog
{
    public static IReadOnlyList<IMigration> All { get; } =
    [
        new V1ToV2_ItemType(),
        new V2ToV3_SeparateProducts(),
    ];
}

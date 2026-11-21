using System.Text.Json.Nodes;

namespace ArtGander.Core.Schema.Migrations;

/// <summary>
/// Version 3 stores products and orders as their own files instead of inside the art piece. This step only renames
/// <c>products</c>/<c>sales</c> to <c>legacyProducts</c>/<c>legacySales</c>, so nothing is lost; the library migrator then
/// moves them to product and order files and removes them from the piece.
/// </summary>
public sealed class V2ToV3_SeparateProducts : IMigration
{
    public int FromVersion => 2;

    public void Apply(JsonObject root)
    {
        Rename(root, "products", "legacyProducts");
        Rename(root, "sales", "legacySales");
    }

    private static void Rename(JsonObject root, string from, string to)
    {
        if (!root.TryGetPropertyValue(from, out var node)) return;
        root.Remove(from);
        if (node is JsonArray { Count: > 0 }) root[to] = node;
    }
}

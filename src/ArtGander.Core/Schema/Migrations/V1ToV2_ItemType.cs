using System.Text.Json.Nodes;

namespace ArtGander.Core.Schema.Migrations;

/// <summary>
/// Version 2 introduces projects. Every existing details file describes a single art piece that is already made,
/// so it gets <c>"type": "piece"</c> and <c>"status": "finished"</c>.
/// </summary>
public sealed class V1ToV2_ItemType : IMigration
{
    public int FromVersion => 1;

    public void Apply(JsonObject root)
    {
        root["type"] ??= "piece";
        root["status"] ??= "finished";
    }
}

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ArtGander.Core.Model;

namespace ArtGander.Core.Schema;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    IndentSize = 2,
    NewLine = "\n",
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ArtPiece))]
[JsonSerializable(typeof(LibrarySettings))]
[JsonSerializable(typeof(Order))]
internal sealed partial class ArtGanderJsonContext : JsonSerializerContext;

/// <summary>
/// Reflection-free (trim/AOT safe) JSON settings shared by every ArtGander file.
/// Output is human-readable: 2-space indent, LF line endings, and unescaped non-ASCII characters (é, ü, ß…).
/// </summary>
public static class ArtGanderJson
{
    private static readonly ArtGanderJsonContext Context = new(new JsonSerializerOptions(ArtGanderJsonContext.Default.Options)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    public static JsonSerializerOptions Options => Context.Options;

    public static string Serialize(ArtPiece piece) => JsonSerializer.Serialize(piece, Context.ArtPiece) + "\n";
    public static string Serialize(LibrarySettings settings) => JsonSerializer.Serialize(settings, Context.LibrarySettings) + "\n";
    public static string Serialize(Order order) => JsonSerializer.Serialize(order, Context.Order) + "\n";

    public static ArtPiece? DeserializePiece(System.Text.Json.Nodes.JsonNode node) => node.Deserialize(Context.ArtPiece);
    public static ArtPiece? DeserializePiece(string json) => JsonSerializer.Deserialize(json, Context.ArtPiece);
    public static LibrarySettings? DeserializeLibrary(string json) => JsonSerializer.Deserialize(json, Context.LibrarySettings);
    public static Order? DeserializeOrder(string json) => JsonSerializer.Deserialize(json, Context.Order);

    public static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}

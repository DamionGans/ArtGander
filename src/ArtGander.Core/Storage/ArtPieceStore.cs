using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ArtGander.Core.Model;
using ArtGander.Core.Schema;

namespace ArtGander.Core.Storage;

/// <summary>Loads, upgrades and saves <c>artgander.json</c> files.</summary>
public sealed class ArtPieceStore(SchemaMigrator migrator)
{
    public ArtPieceStore() : this(SchemaMigrator.Default) { }

    /// <summary>The machine name written into <c>updatedBy</c>, which helps when the library is shared over the network.</summary>
    public string MachineName { get; init; } = Environment.MachineName;

    public static string AppVersion { get; } =
        typeof(ArtPieceStore).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "dev";

    public static bool HasPieceFile(string folderPath) =>
        File.Exists(Path.Combine(folderPath, CurrentSchema.PieceFileName))
        || File.Exists(Path.Combine(folderPath, CurrentSchema.LegacyPieceFileName));

    /// <summary>Renames a visible details file from early builds to the hidden name.</summary>
    private static void PromoteLegacyFile(string folderPath)
    {
        var legacy = Path.Combine(folderPath, CurrentSchema.LegacyPieceFileName);
        var current = Path.Combine(folderPath, CurrentSchema.PieceFileName);
        if (!File.Exists(legacy) || File.Exists(current)) return;
        try { File.Move(legacy, current); }
        catch (IOException) { /* another computer may be doing the same right now */ }
    }

    /// <summary>
    /// Loads a piece. Files with an older schema are upgraded in place (a <c>.v{N}.bak</c> backup is kept).
    /// Files from a newer ArtGander load read-only.
    /// </summary>
    /// <exception cref="ArtPieceFileException">The file is missing or isn't valid JSON.</exception>
    public ArtPieceDocument Load(string folderPath)
    {
        PromoteLegacyFile(folderPath);
        var path = Path.Combine(folderPath, CurrentSchema.PieceFileName);
        byte[] bytes;
        try { bytes = AtomicFile.ReadAllBytes(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ArtPieceFileException(path, $"Could not read {CurrentSchema.PieceFileName}: {ex.Message}", ex);
        }

        AtomicFile.MarkHidden(path);
        var hash = AtomicFile.Hash(bytes);
        var text = AtomicFile.DecodeUtf8(bytes);

        JsonObject root;
        try
        {
            root = JsonNode.Parse(text, documentOptions: ArtGanderJson.DocumentOptions) as JsonObject
                   ?? throw new ArtPieceFileException(path, "The file is not a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new ArtPieceFileException(path, $"{CurrentSchema.PieceFileName} contains invalid JSON (line {ex.LineNumber + 1}): {ex.Message}", ex);
        }

        int originalVersion;
        try
        {
            originalVersion = migrator.Migrate(root);
        }
        catch (NewerSchemaException ex)
        {
            var newer = DeserializeLenient(root, path);
            return new ArtPieceDocument(folderPath, newer, hash, ArtGanderJson.Serialize(newer))
            {
                IsReadOnly = true,
                ReadOnlyReason = ex.Message,
            };
        }

        var piece = Deserialize(root, path);
        Normalize(piece, folderPath);
        var doc = new ArtPieceDocument(folderPath, piece, hash, ArtGanderJson.Serialize(piece))
        {
            UpgradedFromVersion = originalVersion < migrator.TargetVersion ? originalVersion : null,
        };

        if (doc.UpgradedFromVersion is { } from)
        {
            // Keep the pre-upgrade file next to the new one, then write the upgraded version.
            var backup = Path.Combine(folderPath, $"{CurrentSchema.PieceFileName}.v{from}.bak");
            if (!File.Exists(backup)) AtomicFile.WriteAllText(backup, text);
            Save(doc, force: true);
        }
        return doc;
    }

    /// <summary>Creates a new piece file in <paramref name="folderPath"/>. Fails if one already exists.</summary>
    public ArtPieceDocument Create(string folderPath, ArtPiece piece)
    {
        if (HasPieceFile(folderPath))
            throw new IOException($"{CurrentSchema.PieceFileName} already exists in {folderPath}.");
        Directory.CreateDirectory(folderPath);
        piece.SchemaVersion = migrator.TargetVersion;
        var doc = new ArtPieceDocument(folderPath, piece, loadedHash: null, savedJson: "");
        Save(doc, force: true);
        return doc;
    }

    /// <summary>
    /// Saves the piece atomically. Unless <paramref name="force"/> is set, this throws <see cref="ConcurrentEditException"/>
    /// when another computer changed the file after we loaded it.
    /// </summary>
    public void Save(ArtPieceDocument doc, bool force = false)
    {
        if (doc.IsReadOnly)
            throw new InvalidOperationException(doc.ReadOnlyReason ?? "This art piece is read-only.");

        if (!force && doc.LoadedHash is not null)
        {
            var current = CurrentHash(doc.FilePath);
            if (current != doc.LoadedHash)
                throw new ConcurrentEditException(doc.FilePath);
        }

        doc.Piece.SchemaVersion = migrator.TargetVersion;
        doc.Piece.UpdatedAt = DateTimeOffset.Now;
        doc.Piece.UpdatedBy = MachineName;
        doc.Piece.UpdatedWith = $"ArtGander {AppVersion}";

        var json = ArtGanderJson.Serialize(doc.Piece);
        AtomicFile.WriteAllText(doc.FilePath, json);
        doc.LoadedHash = AtomicFile.Hash(System.Text.Encoding.UTF8.GetBytes(json));
        doc.SavedJson = json;
    }

    /// <summary>Checks whether the file on disk changed since this document was loaded or saved (e.g. by the other computer).</summary>
    public bool ChangedOnDisk(ArtPieceDocument doc) => doc.LoadedHash is not null && CurrentHash(doc.FilePath) != doc.LoadedHash;

    private static string? CurrentHash(string path)
    {
        try { return File.Exists(path) ? AtomicFile.Hash(AtomicFile.ReadAllBytes(path)) : null; }
        catch (IOException) { return null; }
    }

    private static ArtPiece Deserialize(JsonObject root, string path)
    {
        try
        {
            return ArtGanderJson.DeserializePiece(root) ?? throw new ArtPieceFileException(path, "The file is empty.");
        }
        catch (JsonException ex)
        {
            throw new ArtPieceFileException(path, $"{CurrentSchema.PieceFileName} has an unexpected structure: {ex.Message}", ex);
        }
    }

    /// <summary>A best-effort read of a file from a newer version. Shows what we can and never throws over unknown values.</summary>
    private static ArtPiece DeserializeLenient(JsonObject root, string path)
    {
        try { return Deserialize(root, path); }
        catch (ArtPieceFileException)
        {
            return new ArtPiece
            {
                Title = root["title"]?.GetValueKind() == JsonValueKind.String ? root["title"]!.GetValue<string>() : Path.GetFileName(Path.GetDirectoryName(path)) ?? "",
                SchemaVersion = SchemaMigrator.ReadVersion(root),
            };
        }
    }

    /// <summary>Repairs harmless inconsistencies, such as a missing title or duplicate file entries.</summary>
    private static void Normalize(ArtPiece piece, string folderPath)
    {
        if (string.IsNullOrWhiteSpace(piece.Title)) piece.Title = Path.GetFileName(folderPath);
        var seen = new HashSet<string>(Naming.NameRules.Comparer);
        for (var i = piece.Files.Count - 1; i >= 0; i--)
        {
            if (string.IsNullOrWhiteSpace(piece.Files[i].FileName) || !seen.Add(piece.Files[i].FileName))
                piece.Files.RemoveAt(i);
        }
    }
}

public class ArtPieceFileException(string path, string message, Exception? inner = null) : Exception(message, inner)
{
    public string FilePath { get; } = path;
}

public sealed class ConcurrentEditException(string path)
    : ArtPieceFileException(path, "This art piece was changed on another computer after you opened it.");

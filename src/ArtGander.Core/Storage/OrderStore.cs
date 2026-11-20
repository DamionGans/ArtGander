using System.Text.Json;
using ArtGander.Core.Model;
using ArtGander.Core.Naming;
using ArtGander.Core.Schema;

namespace ArtGander.Core.Storage;

/// <summary>Reads and writes order details: one file per order in <c>{ArtRoot}/.artgander/orders/</c>.</summary>
public static class OrderStore
{
    public const string FolderName = "orders";

    public static string FolderFor(string rootPath) => Path.Combine(rootPath, CurrentSchema.LibraryFolderName, FolderName);

    /// <summary>The file name for an order, e.g. "#1001" maps to "1001.json".</summary>
    public static string FileNameFor(Order order)
    {
        var name = NameRules.ToVersionLabel(order.OrderRef);
        return (string.IsNullOrWhiteSpace(order.OrderRef) ? "order-" + order.Id.ToString("N")[..8] : name) + ".json";
    }

    public static IReadOnlyList<Order> LoadAll(string rootPath)
    {
        var folder = FolderFor(rootPath);
        if (!Directory.Exists(folder)) return [];
        var orders = new List<Order>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
        {
            if (AtomicFile.IsTempFile(Path.GetFileName(file))) continue;
            try
            {
                if (ArtGanderJson.DeserializeOrder(AtomicFile.DecodeUtf8(AtomicFile.ReadAllBytes(file))) is { } order)
                    orders.Add(order);
            }
            catch (Exception ex) when (ex is JsonException or IOException) { /* skip unreadable files */ }
        }
        return orders;
    }

    /// <summary>Saves an order. If its reference changed, the file is renamed (<paramref name="previousFileName"/>).</summary>
    public static void Save(string rootPath, Order order, string? previousFileName = null)
    {
        var folder = FolderFor(rootPath);
        Directory.CreateDirectory(folder);
        order.UpdatedAt = DateTimeOffset.Now;
        order.UpdatedBy = Environment.MachineName;
        var fileName = FileNameFor(order);
        var path = Path.Combine(folder, fileName);
        if (File.Exists(path) && (previousFileName is null || !NameRules.NamesEqual(previousFileName, fileName)))
        {
            // Another order already uses this file: only allowed if it is the same order.
            var existing = ArtGanderJson.DeserializeOrder(AtomicFile.DecodeUtf8(AtomicFile.ReadAllBytes(path)));
            if (existing is not null && existing.Id != order.Id)
                throw new IOException($"Another order with reference \"{order.OrderRef}\" already exists.");
        }
        AtomicFile.WriteAllText(path, ArtGanderJson.Serialize(order));
        if (previousFileName is not null && !NameRules.NamesEqual(previousFileName, fileName))
            AtomicFile.TryDelete(Path.Combine(folder, previousFileName));
    }

    public static void Delete(string rootPath, Order order) =>
        AtomicFile.TryDelete(Path.Combine(FolderFor(rootPath), FileNameFor(order)));
}

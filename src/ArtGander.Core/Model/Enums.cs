using System.Text.Json;
using System.Text.Json.Serialization;

namespace ArtGander.Core.Model;

/// <summary>Writes enums as camelCase strings (e.g. "printOnDemand"). Safe for trimming and AOT.</summary>
public sealed class CamelCaseEnumConverter<TEnum>() : JsonStringEnumConverter<TEnum>(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
    where TEnum : struct, Enum;

/// <summary>
/// File version kinds are plain names, so each library can define its own list (Settings → File kinds,
/// stored in <see cref="LibrarySettings.FileKinds"/>). These are the built-in defaults.
/// </summary>
public static class FileKind
{
    public const string Photo = "photo";
    public const string Scan = "scan";
    public const string Master = "master";
    public const string Edit = "edit";
    public const string Transparent = "transparent";
    public const string Print = "print";
    public const string Mockup = "mockup";
    public const string Other = "other";

    public static IReadOnlyList<string> Defaults { get; } = [Photo, Scan, Master, Edit, Transparent, Print, Mockup, Other];
}

[JsonConverter(typeof(CamelCaseEnumConverter<ItemType>))]
public enum ItemType { Piece, Project }

[JsonConverter(typeof(CamelCaseEnumConverter<WorkStatus>))]
public enum WorkStatus { Idea, Planned, InProgress, Finished, Published }

[JsonConverter(typeof(CamelCaseEnumConverter<OriginalStatus>))]
public enum OriginalStatus { Available, Reserved, Sold, NotForSale, Gifted }

[JsonConverter(typeof(CamelCaseEnumConverter<CopyrightStatus>))]
public enum CopyrightStatus { Unregistered, Pending, Registered }

[JsonConverter(typeof(CamelCaseEnumConverter<ProductType>))]
public enum ProductType { Original, SelfPrint, PrintOnDemand, Digital, Other, Handmade }

[JsonConverter(typeof(CamelCaseEnumConverter<ProductStatus>))]
public enum ProductStatus { Draft, Active, Archived }

/// <summary>Who ships an order line: Printify, or you yourselves.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<Fulfillment>))]
public enum Fulfillment { Self, Printify, Digital }

[JsonConverter(typeof(CamelCaseEnumConverter<ListingStatus>))]
public enum ListingStatus { Draft, Active, Retired }

using System.Text.Json;
using System.Text.Json.Serialization;

namespace ArtGander.Core.Model;

/// <summary>Writes enums as camelCase strings (e.g. "printOnDemand"). Safe for trimming and AOT.</summary>
public sealed class CamelCaseEnumConverter<TEnum>() : JsonStringEnumConverter<TEnum>(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
    where TEnum : struct, Enum;

[JsonConverter(typeof(CamelCaseEnumConverter<FileKind>))]
public enum FileKind { Photo, Scan, Master, Edit, Transparent, Print, Mockup, Other }

[JsonConverter(typeof(CamelCaseEnumConverter<OriginalStatus>))]
public enum OriginalStatus { Available, Reserved, Sold, NotForSale, Gifted }

[JsonConverter(typeof(CamelCaseEnumConverter<CopyrightStatus>))]
public enum CopyrightStatus { Unregistered, Pending, Registered }

[JsonConverter(typeof(CamelCaseEnumConverter<ProductType>))]
public enum ProductType { Original, SelfPrint, PrintOnDemand, Digital, Other }

[JsonConverter(typeof(CamelCaseEnumConverter<ListingStatus>))]
public enum ListingStatus { Draft, Active, Retired }

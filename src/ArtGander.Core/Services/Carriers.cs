namespace ArtGander.Core.Services;

/// <summary>Known delivery providers and their tracking page URLs.</summary>
public static class Carriers
{
    private static readonly Dictionary<string, string> TrackingUrls = new(StringComparer.OrdinalIgnoreCase)
    {
        ["USPS"] = "https://tools.usps.com/go/TrackConfirmAction?tLabels={0}",
        ["UPS"] = "https://www.ups.com/track?tracknum={0}",
        ["FedEx"] = "https://www.fedex.com/fedextrack/?trknbr={0}",
        ["DHL"] = "https://www.dhl.com/global-en/home/tracking/tracking-parcel.html?tracking-id={0}",
        ["PostNL"] = "https://jouw.postnl.nl/track-and-trace/{0}",
        ["bpost"] = "https://track.bpost.cloud/btr/web/#/search?itemCode={0}",
        ["DPD"] = "https://tracking.dpd.de/status/en_US/parcel/{0}",
        ["GLS"] = "https://gls-group.com/track/{0}",
        ["Royal Mail"] = "https://www.royalmail.com/track-your-item#/tracking-results/{0}",
        ["Canada Post"] = "https://www.canadapost-postescanada.ca/track-reperage/en#/search?searchFor={0}",
        ["Australia Post"] = "https://auspost.com.au/mypost/track/details/{0}",
        ["Deutsche Post"] = "https://www.deutschepost.de/de/s/sendungsverfolgung.html?piececode={0}",
    };

    public static IReadOnlyList<string> Known { get; } = [.. TrackingUrls.Keys, "Other"];

    /// <summary>The tracking page for a shipment, or null when the carrier is unknown or there is no code.</summary>
    public static string? TrackingUrl(string? carrier, string? code)
    {
        if (string.IsNullOrWhiteSpace(carrier) || string.IsNullOrWhiteSpace(code)) return null;
        return TrackingUrls.TryGetValue(carrier.Trim(), out var template)
            ? string.Format(System.Globalization.CultureInfo.InvariantCulture, template, Uri.EscapeDataString(code.Trim()))
            : null;
    }
}

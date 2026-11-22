using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ArtGander.Core.Model;

/// <summary>
/// Library-wide settings stored in <c>{ArtRoot}/.artgander/library.json</c>.
/// This file only holds shared defaults. Art piece data always lives in the piece's own folder.
/// </summary>
public sealed partial class LibrarySettings : ModelBase
{
    public int SchemaVersion { get; set; } = 1;
    [ObservableProperty] public partial string DefaultCurrency { get; set; } = "EUR";
    [ObservableProperty] public partial string DefaultCopyrightOwner { get; set; } = "";
    public ObservableCollection<CollectionInfo> Collections { get; set; } = [];
    public ObservableCollection<string> Channels { get; set; } = ["shopify", "printify", "inPerson", "other"];

    /// <summary>The kinds a file version can have, e.g. photo, master, transparent. Editable in Settings.</summary>
    public ObservableCollection<string> FileKinds { get; set; } = [.. FileKind.Defaults];

    /// <summary>
    /// The folder in the art folder that holds the products, one folder per product, grouped per art piece
    /// (default "Products"). It is never shown as an art piece.
    /// </summary>
    public string ProductsFolder { get; set; } = "Products";

    /// <summary>
    /// Which Printify shop and Shopify store this library is connected to. Only ids live here (shared by all computers);
    /// the secret keys are stored per computer in the operating system's keychain.
    /// </summary>
    public IntegrationSettings Integrations { get; set; } = new();

    /// <summary>Defaults for protecting images with Mistcoat (shared by all computers; where Mistcoat is installed is per computer).</summary>
    public ProtectionSettings Protection { get; set; } = new();

    /// <summary>The ICC profiles for CMYK, Adobe RGB and sRGB versions (stored in <c>.artgander/profiles</c>).</summary>
    public ColorSettings Colors { get; set; } = new();
}

public sealed partial class ColorSettings : ModelBase
{
    /// <summary>File names in <c>.artgander/profiles</c>; empty means the default of the computer's color engine.</summary>
    [ObservableProperty] public partial string CmykProfile { get; set; } = "";
    [ObservableProperty] public partial string AdobeRgbProfile { get; set; } = "";
    [ObservableProperty] public partial string SrgbProfile { get; set; } = "";
    /// <summary>"perceptual" or "relative" (relative colorimetric).</summary>
    [ObservableProperty] public partial string Intent { get; set; } = "relative";
    [ObservableProperty] public partial bool BlackPointCompensation { get; set; } = true;
}

public sealed partial class ProtectionSettings : ModelBase
{
    /// <summary>The Mistcoat preset quick protect uses: light, balanced, strong or maximum.</summary>
    [ObservableProperty] public partial string Preset { get; set; } = "balanced";
    /// <summary>The artist name embedded in protected files. Empty: the piece's copyright owner.</summary>
    [ObservableProperty] public partial string Credit { get; set; } = "";
    /// <summary>Embed the machine-readable "do not train" declaration.</summary>
    [ObservableProperty] public partial bool Metadata { get; set; } = true;
}

public sealed partial class IntegrationSettings : ModelBase
{
    [ObservableProperty] public partial long? PrintifyShopId { get; set; }
    [ObservableProperty] public partial string PrintifyShopTitle { get; set; } = "";
    /// <summary>e.g. "my-goose-shop.myshopify.com".</summary>
    [ObservableProperty] public partial string ShopifyDomain { get; set; } = "";
    /// <summary>The Client ID of the ArtGander app in the Shopify Dev Dashboard (not secret; the client secret is).</summary>
    [ObservableProperty] public partial string ShopifyClientId { get; set; } = "";
    [ObservableProperty] public partial string ShopifyApiVersion { get; set; } = "";
    /// <summary>Default prices of new Printify products: production cost × this factor, when no price is set.</summary>
    [ObservableProperty] public partial decimal? DefaultMarkup { get; set; }
}

public sealed partial class CollectionInfo : ModelBase
{
    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string Description { get; set; } = "";
}

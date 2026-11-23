using System.Collections.ObjectModel;
using ArtGander.Core.Integrations;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ArtGander.App.ViewModels;

/// <summary>The advanced Mistcoat options for one file: everything <c>mistcoat protect</c> can be told.</summary>
public sealed partial class ProtectOptionsViewModel : ObservableObject
{
    public const string SameFormat = "Same as the file";
    public static IReadOnlyList<string> Presets => MistcoatPasses.Presets;
    public static IReadOnlyList<string> Formats { get; } = [SameFormat, "PNG", "JPEG", "WebP", "TIFF"];
    public static IReadOnlyList<string> WatermarkLayouts => MistcoatPasses.WatermarkLayouts;
    public static IReadOnlyList<string> WatermarkResistances => MistcoatPasses.WatermarkResistances;

    private readonly string _sourceExtension;

    public ProtectOptionsViewModel(string sourceExtension, MistcoatOptions defaults, string version)
    {
        _sourceExtension = sourceExtension.ToLowerInvariant();
        foreach (var (name, label, visibility) in MistcoatPasses.All)
            Passes.Add(new PassToggle(name, label, visibility));
        Metadata = defaults.Metadata;
        Credit = defaults.Credit;
        Copyright = defaults.Copyright;
        Version = version;
        Watermark = !string.IsNullOrWhiteSpace(defaults.Watermark);
        WatermarkText = defaults.Watermark ?? "";
        WatermarkLayout = defaults.WatermarkLayout is { } l && WatermarkLayouts.Contains(l) ? l : "tiled";
        WatermarkResistance = defaults.WatermarkResistance is { } r && WatermarkResistances.Contains(r) ? r : "strong";
        WatermarkOpacity = defaults.WatermarkOpacity ?? 50;
        Preset = defaults.Preset; // applies strength and passes
        OnPresetChanged(Preset);
    }

    public ObservableCollection<PassToggle> Passes { get; } = [];

    [ObservableProperty] public partial string Preset { get; set; } = MistcoatPasses.DefaultPreset;
    [ObservableProperty] public partial string PresetBlurb { get; set; } = "";
    [ObservableProperty] public partial double Strength { get; set; } = 60;
    [ObservableProperty] public partial bool Harden { get; set; } = true;
    [ObservableProperty] public partial bool Metadata { get; set; } = true;
    [ObservableProperty] public partial string Credit { get; set; } = "";
    [ObservableProperty] public partial string Copyright { get; set; } = "";
    [ObservableProperty] public partial string Format { get; set; } = SameFormat;
    [ObservableProperty] public partial double JpegQuality { get; set; } = 95;
    [ObservableProperty] public partial bool Measure { get; set; } = true;
    [ObservableProperty] public partial bool UseGpu { get; set; } = true;
    /// <summary>A visible watermark drawn into the picture before it's protected.</summary>
    [ObservableProperty] public partial bool Watermark { get; set; }
    [ObservableProperty] public partial string WatermarkText { get; set; } = "";
    [ObservableProperty] public partial string WatermarkLayout { get; set; } = "tiled";
    [ObservableProperty] public partial string WatermarkResistance { get; set; } = "strong";
    [ObservableProperty] public partial double WatermarkOpacity { get; set; } = 50;
    public string WatermarkOpacityText => $"Watermark opacity: {WatermarkOpacity:0}%";
    partial void OnWatermarkOpacityChanged(double value) => OnPropertyChanged(nameof(WatermarkOpacityText));
    public string WatermarkResistanceBlurb => WatermarkResistance switch
    {
        "plain" => "An even pattern: easiest to read, easiest to remove.",
        "maximum" => "Extra rows woven into the texture: hardest to remove, most visible.",
        _ => "Every mark differs a little in place, angle, size and opacity, with a soft edge: hard for AI tools to remove.",
    };
    partial void OnWatermarkResistanceChanged(string value) => OnPropertyChanged(nameof(WatermarkResistanceBlurb));

    /// <summary>The version label the protected copy is saved under.</summary>
    [ObservableProperty] public partial string Version { get; set; } = "";

    public string StrengthText => $"{Strength:0}";
    public bool IsJpeg => OutputExtension is ".jpg" or ".jpeg" or ".jfif";

    partial void OnPresetChanged(string value)
    {
        var (strength, passes, blurb) = MistcoatPasses.Preset(value);
        Strength = strength;
        PresetBlurb = blurb;
        foreach (var p in Passes) p.IsOn = passes.Contains(p.Name);
    }

    partial void OnStrengthChanged(double value) => OnPropertyChanged(nameof(StrengthText));
    partial void OnFormatChanged(string value) => OnPropertyChanged(nameof(IsJpeg));

    /// <summary>The output file's extension, which tells Mistcoat the format.</summary>
    public string OutputExtension => Format switch
    {
        "PNG" => ".png",
        "JPEG" => ".jpg",
        "WebP" => ".webp",
        "TIFF" => ".tif",
        _ => _sourceExtension,
    };

    public MistcoatOptions ToOptions() => new()
    {
        Preset = Preset,
        Strength = Math.Round(Strength),
        Passes = Passes.ToDictionary(p => p.Name, p => p.IsOn),
        Harden = Harden,
        Metadata = Metadata,
        Credit = Metadata ? Credit : "",
        Copyright = Metadata ? Copyright : "",
        JpegQuality = IsJpeg ? (int)Math.Round(JpegQuality) : null,
        Quick = !Measure,
        UseGpu = UseGpu ? null : false,
        Watermark = Watermark ? ArtGander.App.Services.ProtectionService.WatermarkTextOrDefault(WatermarkText) : null,
        WatermarkLayout = WatermarkLayout,
        WatermarkResistance = WatermarkResistance,
        WatermarkOpacity = Math.Round(WatermarkOpacity),
    };
}

public sealed partial class PassToggle(string name, string label, string visibility) : ObservableObject
{
    public string Name { get; } = name;
    public string Label { get; } = label;
    public string Visibility { get; } = visibility;
    public string Text => $"{Label} ({Visibility})";
    [ObservableProperty] public partial bool IsOn { get; set; }
}

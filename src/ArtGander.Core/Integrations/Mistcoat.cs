using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ArtGander.Core.Integrations;

/// <summary>
/// Options for one Mistcoat run. They map onto <c>mistcoat protect</c>'s command-line options
/// (see Mistcoat's docs/CLI.md). Null means "Mistcoat's own default".
/// </summary>
public sealed record MistcoatOptions
{
    /// <summary>light, balanced, strong or maximum.</summary>
    public string Preset { get; init; } = MistcoatPasses.DefaultPreset;
    /// <summary>0–100; overrides the preset's strength.</summary>
    public double? Strength { get; init; }
    /// <summary>Passes switched on or off on top of the preset. Missing passes follow the preset.</summary>
    public IReadOnlyDictionary<string, bool> Passes { get; init; } = new Dictionary<string, bool>();
    /// <summary>Re-compression hardening (slower, more durable). Default on.</summary>
    public bool Harden { get; init; } = true;
    /// <summary>Embed the "do not train" declaration. Default on.</summary>
    public bool Metadata { get; init; } = true;
    public string Credit { get; init; } = "";
    public string Copyright { get; init; } = "";
    /// <summary>70–100, for JPEG output.</summary>
    public int? JpegQuality { get; init; }
    /// <summary>Skip the quality measurement (faster; no visibility/durability figures).</summary>
    public bool Quick { get; init; }
    public bool? UseGpu { get; init; }

    /// <summary>A visible watermark drawn into the picture before it's protected (so it's protected too), or null for none.</summary>
    public string? Watermark { get; init; }
    /// <summary>tiled (repeated across the picture), centre, or corner.</summary>
    public string? WatermarkLayout { get; init; }
    /// <summary>How hard it is to remove: plain, strong or maximum.</summary>
    public string? WatermarkResistance { get; init; }
    /// <summary>5–100 percent.</summary>
    public double? WatermarkOpacity { get; init; }
}

/// <summary>Mistcoat's passes and presets, with the names its command line uses.</summary>
public static class MistcoatPasses
{
    public const string DefaultPreset = "balanced";
    public static IReadOnlyList<string> Presets { get; } = ["light", "balanced", "strong", "maximum"];
    public static IReadOnlyList<string> WatermarkLayouts { get; } = ["tiled", "centre", "corner"];
    public static IReadOnlyList<string> WatermarkResistances { get; } = ["plain", "strong", "maximum"];
    public const string DefaultWatermark = "SAMPLE";

    public static IReadOnlyList<(string Name, string Label, string Visibility)> All { get; } =
    [
        ("texture_warp", "Stroke disruption", "subtle"),
        ("decoy", "Ghost contours", "visible"),
        ("dct_poison", "Compression-resistant pattern", "invisible"),
        ("chroma", "Colour-channel perturbation", "invisible"),
        ("hf_noise", "Texture noise", "invisible"),
    ];

    private static readonly string[] Signal = ["texture_warp", "dct_poison", "chroma", "hf_noise"];

    /// <summary>What each preset does, as Mistcoat defines it (strength 0–100, passes on).</summary>
    public static (double Strength, IReadOnlyList<string> Passes, string Blurb) Preset(string key) => key switch
    {
        "light" => (32, ["dct_poison", "chroma", "hf_noise"], "Imperceptible. A deterrent rather than a wall."),
        "strong" => (85, Signal, "A faint texture may show in flat areas up close. For work posted publicly."),
        "maximum" => (100, [.. Signal, "decoy"], "Everything on. Visibly altered up close."),
        _ => (60, Signal, "Invisible in normal viewing; survives re-compression by websites."),
    };

    /// <summary>The image formats Mistcoat reads.</summary>
    public static IReadOnlySet<string> SupportedExtensions { get; } =
        new HashSet<string>([".png", ".jpg", ".jpeg", ".webp", ".tif", ".tiff", ".bmp", ".jfif"], StringComparer.OrdinalIgnoreCase);

    public static bool CanProtect(string path) => SupportedExtensions.Contains(Path.GetExtension(path));
}

public sealed record MistcoatProgress(string Stage, double Fraction);

public sealed record MistcoatResult(
    bool Ok,
    string? Output,
    string? Error,
    string? Visibility,
    string? Durability,
    double? Seconds,
    IReadOnlyList<string> Passes)
{
    /// <summary>What the original was and what was written, e.g. "16-bit RGB, Adobe RGB (1998)" (newer Mistcoat).</summary>
    public string? ColorIn { get; init; }
    public string? ColorOut { get; init; }

    /// <summary>Said only when the protected copy's colors differ from the original's (e.g. CMYK saved as PNG).</summary>
    public string? ColorNote => ColorIn is { Length: > 0 } i && ColorOut is { Length: > 0 } o && i != o ? $"colors: {i} → {o}" : null;

    public string Summary => Ok
        ? string.Join(" · ", new[] { Visibility, Durability, ColorNote, Seconds is { } s ? $"{s:0} s" : null }.Where(x => !string.IsNullOrEmpty(x)))
        : Error ?? "Mistcoat failed.";
}

/// <summary>
/// Runs Mistcoat (the separate image-protection app) headless, or opens its window with files queued.
/// Mistcoat never overwrites the original; ArtGander gives it a temporary output file and then tracks the result.
/// </summary>
public sealed class MistcoatRunner(string executable)
{
    public string Executable { get; } = executable;

    // ---- Finding Mistcoat ---------------------------------------------------------------------------

    /// <summary>
    /// The Mistcoat program to use: the configured path if it exists, otherwise the usual install places and the PATH.
    /// On Windows Mistcoat ships as a single exe without an installer, so a configured path is the normal case.
    /// </summary>
    public static string? Find(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var p = Resolve(configured.Trim().Trim('"'));
            if (p is not null) return p;
        }
        foreach (var candidate in Candidates())
            if (File.Exists(candidate)) return candidate;
        return null;
    }

    /// <summary>A file, or the program inside a macOS .app bundle or a folder.</summary>
    private static string? Resolve(string path)
    {
        if (File.Exists(path)) return path;
        if (Directory.Exists(path))
        {
            foreach (var inside in new[] { "Contents/MacOS/Mistcoat", "Mistcoat.exe", "mistcoat-cli.exe", "mistcoat" })
                if (File.Exists(Path.Combine(path, inside))) return Path.Combine(path, inside);
            // A downloads/release folder: Mistcoat-0.1.0-windows-x64.exe (the newest version, this computer's architecture first)
            var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";
            return Directory.EnumerateFiles(path, "Mistcoat-*-windows-*.exe")
                .Where(f => !Path.GetFileName(f).StartsWith("mistcoat-cli", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.EndsWith($"-{arch}.exe", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(f => f, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        return null;
    }

    private static IEnumerable<string> Candidates()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var root in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
            })
            {
                if (string.IsNullOrEmpty(root)) continue;
                yield return Path.Combine(root, "Mistcoat", "Mistcoat.exe");
                yield return Path.Combine(root, "Mistcoat", "mistcoat-cli.exe");
            }
        }
        else
        {
            yield return "/Applications/Mistcoat.app/Contents/MacOS/Mistcoat";
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications/Mistcoat.app/Contents/MacOS/Mistcoat");
            yield return "/usr/local/bin/mistcoat";
            yield return "/opt/homebrew/bin/mistcoat";
        }
        var names = OperatingSystem.IsWindows() ? new[] { "Mistcoat.exe", "mistcoat-cli.exe", "mistcoat.exe" } : ["mistcoat"];
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var name in names)
                yield return Path.Combine(dir.Trim('"'), name);
    }

    /// <summary>The console build (mistcoat-cli.exe) when it sits next to the windowed one: it starts faster.</summary>
    public string HeadlessExecutable
    {
        get
        {
            if (!OperatingSystem.IsWindows() || !HasWindow) return Executable;
            var dir = Path.GetDirectoryName(Executable) ?? "";
            var plain = Path.Combine(dir, "mistcoat-cli.exe");
            if (File.Exists(plain)) return plain;
            // Release downloads carry version and architecture: Mistcoat-0.1.0-windows-x64.exe → mistcoat-cli-0.1.0-windows-x64.exe
            var name = Path.GetFileName(Executable);
            if (name.StartsWith("Mistcoat-", StringComparison.OrdinalIgnoreCase))
            {
                var sibling = Path.Combine(dir, "mistcoat-cli-" + name["Mistcoat-".Length..]);
                if (File.Exists(sibling)) return sibling;
            }
            return Executable;
        }
    }

    /// <summary>Whether this program can open Mistcoat's window (the console build can't).</summary>
    public bool HasWindow => !Path.GetFileNameWithoutExtension(Executable).Contains("cli", StringComparison.OrdinalIgnoreCase);

    // ---- Protecting ---------------------------------------------------------------------------------

    /// <summary>The command-line arguments for <c>mistcoat protect</c>.</summary>
    public static IReadOnlyList<string> BuildArguments(string input, string outputFile, MistcoatOptions o)
    {
        var args = new List<string>();
        if (o.UseGpu is { } gpu) args.Add(gpu ? "--gpu" : "--no-gpu");
        args.AddRange(["protect", input, "--output-file", outputFile, "--on-exists", "overwrite",
            "--preset", MistcoatPasses.Presets.Contains(o.Preset) ? o.Preset : MistcoatPasses.DefaultPreset]);
        if (o.Strength is { } s) args.AddRange(["--strength", Math.Clamp(s, 0, 100).ToString("0.#", CultureInfo.InvariantCulture)]);
        foreach (var (name, on) in o.Passes.OrderBy(p => p.Key, StringComparer.Ordinal))
            if (MistcoatPasses.All.Any(p => p.Name == name)) args.AddRange([on ? "--enable" : "--disable", name]);
        if (!o.Harden) args.Add("--no-harden");
        if (!o.Metadata) args.Add("--no-metadata");
        if (!string.IsNullOrWhiteSpace(o.Credit)) args.AddRange(["--credit", o.Credit.Trim()]);
        if (!string.IsNullOrWhiteSpace(o.Copyright)) args.AddRange(["--copyright", o.Copyright.Trim()]);
        if (o.JpegQuality is { } q) args.AddRange(["--jpeg-quality", Math.Clamp(q, 70, 100).ToString(CultureInfo.InvariantCulture)]);
        if (!string.IsNullOrWhiteSpace(o.Watermark))
        {
            args.AddRange(["--watermark", o.Watermark.Trim()]);
            if (o.WatermarkLayout is { } layout && MistcoatPasses.WatermarkLayouts.Contains(layout)) args.AddRange(["--watermark-layout", layout]);
            if (o.WatermarkResistance is { } resistance && MistcoatPasses.WatermarkResistances.Contains(resistance)) args.AddRange(["--watermark-resistance", resistance]);
            if (o.WatermarkOpacity is { } opacity) args.AddRange(["--watermark-opacity", Math.Clamp(opacity, 5, 100).ToString("0.#", CultureInfo.InvariantCulture)]);
        }
        if (o.Quick) args.Add("--quick");
        args.AddRange(["--json", "--progress", "json"]);
        return args;
    }

    /// <summary>Protects <paramref name="input"/> into <paramref name="outputFile"/> (whose extension picks the format).</summary>
    public async Task<MistcoatResult> ProtectAsync(string input, string outputFile, MistcoatOptions options,
        IProgress<MistcoatProgress>? progress = null, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(HeadlessExecutable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in BuildArguments(input, outputFile, options)) psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var otherErrors = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (stdout) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            if (ParseProgress(e.Data) is { } p) progress?.Report(p);
            else if (e.Data.Trim().Length > 0) lock (otherErrors) otherErrors.AppendLine(e.Data);
        };
        try { process.Start(); }
        catch (Exception ex) { return Failed($"Couldn't start Mistcoat ({Executable}): {ex.Message}"); }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        process.WaitForExit(); // flushes the redirected streams

        string text;
        lock (stdout) text = stdout.ToString();
        string err;
        lock (otherErrors) err = otherErrors.ToString().Trim();
        var result = ParseResult(text);
        if (result is { Ok: false } && !string.IsNullOrEmpty(options.Watermark)
            && $"{result.Error} {err}".Contains("--watermark", StringComparison.Ordinal) && $"{result.Error} {err}".Contains("unrecognized", StringComparison.OrdinalIgnoreCase))
            return Failed("This Mistcoat is too old for watermarks. Please update Mistcoat (or protect without a watermark).");
        if (result is not null) return result;
        return Failed(err.Length > 0 ? err : $"Mistcoat stopped with exit code {process.ExitCode} and no result.");
    }

    private static MistcoatResult Failed(string error) => new(false, null, error, null, null, null, []);

    /// <summary>A progress line from <c>--progress json</c>, or null for any other line.</summary>
    public static MistcoatProgress? ParseProgress(string line)
    {
        line = line.Trim();
        if (!line.StartsWith('{')) return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Str(root, "event") != "progress") return null;
            var fraction = root.TryGetProperty("fraction", out var f) && f.ValueKind == JsonValueKind.Number ? f.GetDouble() : 0;
            return new MistcoatProgress(Str(root, "stage") ?? "", Math.Clamp(fraction, 0, 1));
        }
        catch (JsonException) { return null; }
    }

    /// <summary>The first result of <c>protect --json</c>'s output, or null when there is no JSON document.</summary>
    public static MistcoatResult? ParseResult(string stdout)
    {
        // The document is one line; take the last line that parses (anything before it is noise).
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Reverse())
        {
            if (!line.StartsWith('{')) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array && results.GetArrayLength() > 0)
                {
                    var r = results[0];
                    var ok = Str(r, "status") == "ok";
                    string? visibility = null, durability = null;
                    if (r.TryGetProperty("metrics", out var m) && m.ValueKind == JsonValueKind.Object)
                    {
                        visibility = Str(m, "visibility");
                        durability = Str(m, "durability_label");
                    }
                    var passes = r.TryGetProperty("passes", out var ps) && ps.ValueKind == JsonValueKind.Array
                        ? ps.EnumerateArray().Select(p => p.GetString() ?? "").Where(p => p.Length > 0).ToList()
                        : [];
                    double? seconds = r.TryGetProperty("seconds", out var sec) && sec.ValueKind == JsonValueKind.Number ? sec.GetDouble() : null;
                    string? colorIn = null, colorOut = null;
                    if (r.TryGetProperty("color", out var col) && col.ValueKind == JsonValueKind.Object)
                    {
                        colorIn = Str(col, "input");
                        colorOut = Str(col, "output");
                    }
                    return new MistcoatResult(ok, Str(r, "output"), ok ? null : Str(r, "error") ?? "Mistcoat couldn't protect this image.",
                        visibility, durability, seconds, passes) { ColorIn = colorIn, ColorOut = colorOut };
                }
                if (Str(root, "error") is { } error) return Failed(error);
            }
            catch (JsonException) { }
        }
        return null;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // ---- The window ---------------------------------------------------------------------------------

    /// <summary>
    /// Opens Mistcoat's window with these images queued, so the artist can check the preview and save from there.
    /// If Mistcoat is already open, it receives the files and comes to the front.
    /// </summary>
    public void OpenWindow(IEnumerable<string> files)
    {
        var psi = new ProcessStartInfo(Executable) { UseShellExecute = false };
        foreach (var f in files) psi.ArgumentList.Add(f);
        using var _ = Process.Start(psi);
    }
}

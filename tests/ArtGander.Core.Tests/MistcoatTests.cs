using ArtGander.Core.Integrations;

namespace ArtGander.Core.Tests;

public class MistcoatTests
{
    [Fact]
    public void Quick_protect_uses_the_preset_and_asks_for_json()
    {
        var args = MistcoatRunner.BuildArguments(@"D:\Art\a.png", @"C:\Temp\out.png", new MistcoatOptions { Preset = "strong", Credit = "Hope", Copyright = "© 2026 Hope" });
        Assert.Equal(
            ["protect", @"D:\Art\a.png", "--output-file", @"C:\Temp\out.png", "--on-exists", "overwrite", "--preset", "strong",
             "--credit", "Hope", "--copyright", "© 2026 Hope", "--json", "--progress", "json"],
            args);
    }

    [Fact]
    public void Advanced_options_map_onto_the_command_line()
    {
        var args = MistcoatRunner.BuildArguments("a.png", "b.jpg", new MistcoatOptions
        {
            Preset = "nonsense",
            Strength = 142,
            Passes = new Dictionary<string, bool> { ["decoy"] = true, ["chroma"] = false, ["made_up"] = true },
            Harden = false,
            Metadata = false,
            JpegQuality = 50,
            Quick = true,
            UseGpu = false,
        });
        Assert.Equal("--no-gpu", args[0]);
        Assert.Contains("balanced", args); // unknown preset falls back to the default
        Assert.Equal("100", args[args.ToList().IndexOf("--strength") + 1]);
        Assert.Equal("chroma", args[args.ToList().IndexOf("--disable") + 1]);
        Assert.Equal("decoy", args[args.ToList().IndexOf("--enable") + 1]);
        Assert.DoesNotContain("made_up", args);
        Assert.Contains("--no-harden", args);
        Assert.Contains("--no-metadata", args);
        Assert.Contains("--quick", args);
        Assert.Equal("70", args[args.ToList().IndexOf("--jpeg-quality") + 1]);
    }

    [Fact]
    public void A_watermark_is_passed_with_its_settings()
    {
        var args = MistcoatRunner.BuildArguments("a.png", "b.png", new MistcoatOptions
        {
            Watermark = " © Hope Gans ", WatermarkLayout = "centre", WatermarkResistance = "maximum", WatermarkOpacity = 140,
        }).ToList();
        Assert.Equal("© Hope Gans", args[args.IndexOf("--watermark") + 1]);
        Assert.Equal("centre", args[args.IndexOf("--watermark-layout") + 1]);
        Assert.Equal("maximum", args[args.IndexOf("--watermark-resistance") + 1]);
        Assert.Equal("100", args[args.IndexOf("--watermark-opacity") + 1]);

        // No watermark: none of its options; an unknown layout is left out.
        Assert.DoesNotContain(MistcoatRunner.BuildArguments("a.png", "b.png", new MistcoatOptions { WatermarkLayout = "tiled" }), a => a.StartsWith("--watermark"));
        Assert.DoesNotContain("--watermark-layout", MistcoatRunner.BuildArguments("a.png", "b.png", new MistcoatOptions { Watermark = "X", WatermarkLayout = "diagonal" }));
    }

    [Fact]
    public void Reads_progress_lines_and_ignores_others()
    {
        var p = MistcoatRunner.ParseProgress("""{"event": "progress", "index": 1, "total": 1, "file": "a.png", "stage": "Hardening against re-compression", "fraction": 0.7}""");
        Assert.NotNull(p);
        Assert.Equal("Hardening against re-compression", p.Stage);
        Assert.Equal(0.7, p.Fraction, 3);
        Assert.Null(MistcoatRunner.ParseProgress("Traceback (most recent call last):"));
        Assert.Null(MistcoatRunner.ParseProgress("""{"event": "ready"}"""));
    }

    [Fact]
    public void Reads_the_result_document()
    {
        var ok = """
            {"mistcoat": "0.1.0", "command": "protect", "ok": true, "results": [{"input": "a.png", "output": "b.png", "status": "ok", "error": null,
             "passes": ["texture_warp", "chroma"], "metrics": {"visibility": "Invisible in normal viewing", "durability_label": "Survives re-compression well"}, "seconds": 39.2}]}
            """.ReplaceLineEndings(" ");
        var r = MistcoatRunner.ParseResult("some noise\n" + ok + "\n");
        Assert.NotNull(r);
        Assert.True(r.Ok);
        Assert.Equal("b.png", r.Output);
        Assert.Equal(["texture_warp", "chroma"], r.Passes);
        Assert.Equal("Invisible in normal viewing · Survives re-compression well · 39 s", r.Summary);

        var failed = MistcoatRunner.ParseResult("""{"ok": false, "results": [{"input": "a.png", "status": "failed", "error": "cannot identify image file"}]}""");
        Assert.False(failed!.Ok);
        Assert.Equal("cannot identify image file", failed.Summary);

        var bad = MistcoatRunner.ParseResult("""{"mistcoat": "0.1.0", "ok": false, "error": "bad arguments: unknown preset"}""");
        Assert.Equal("bad arguments: unknown preset", bad!.Error);
        Assert.Null(MistcoatRunner.ParseResult("not json at all"));

        var cmyk = MistcoatRunner.ParseResult("""{"ok": true, "results": [{"status": "ok", "output": "b.png", "color": {"input": "8-bit CMYK, Coated FOGRA39", "output": "8-bit RGB, Adobe RGB (1998)"}}]}""")!;
        Assert.Equal("colors: 8-bit CMYK, Coated FOGRA39 → 8-bit RGB, Adobe RGB (1998)", cmyk.Summary);
        var same = MistcoatRunner.ParseResult("""{"ok": true, "results": [{"status": "ok", "output": "b.tif", "color": {"input": "16-bit RGB", "output": "16-bit RGB"}}]}""")!;
        Assert.Null(same.ColorNote);
    }

    [Fact]
    public void Finds_the_configured_program_or_a_folder_holding_it()
    {
        var dir = Directory.CreateTempSubdirectory("artgander-mistcoat");
        try
        {
            var exe = Path.Combine(dir.FullName, OperatingSystem.IsWindows() ? "Mistcoat.exe" : "mistcoat");
            File.WriteAllText(exe, "");
            Assert.Equal(exe, MistcoatRunner.Find(exe));
            Assert.Equal(exe, MistcoatRunner.Find($"\"{exe}\""));
            Assert.Equal(exe, MistcoatRunner.Find(dir.FullName));
            Assert.True(new MistcoatRunner(exe).HasWindow);
            Assert.False(new MistcoatRunner(Path.Combine(dir.FullName, "mistcoat-cli.exe")).HasWindow);
            if (OperatingSystem.IsWindows())
            {
                File.Delete(exe);
                var release = Path.Combine(dir.FullName, "Mistcoat-0.1.0-windows-x64.exe");
                var cli = Path.Combine(dir.FullName, "mistcoat-cli-0.1.0-windows-x64.exe");
                File.WriteAllText(release, "");
                File.WriteAllText(cli, "");
                Assert.Equal(release, MistcoatRunner.Find(dir.FullName));
                Assert.Equal(cli, new MistcoatRunner(release).HeadlessExecutable); // runs headless without the window build
            }
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void Knows_which_images_it_can_protect()
    {
        Assert.True(MistcoatPasses.CanProtect("Goose-master.PNG"));
        Assert.True(MistcoatPasses.CanProtect("goose.jpeg"));
        Assert.False(MistcoatPasses.CanProtect("goose.psd"));
        Assert.False(MistcoatPasses.CanProtect("goose.pdf"));
    }

    /// <summary>Runs the real Mistcoat when MISTCOAT_EXE points at it (not on CI).</summary>
    [Fact]
    public async Task Protects_a_real_image_when_Mistcoat_is_available()
    {
        var exe = Environment.GetEnvironmentVariable("MISTCOAT_EXE");
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return;
        var dir = Directory.CreateTempSubdirectory("artgander-mistcoat-run");
        try
        {
            var src = Path.Combine(dir.FullName, "goose.bmp");
            WriteBmp(src, 64, 48);
            var dst = Path.Combine(dir.FullName, "out.png");
            var stages = new List<string>();
            var result = await new MistcoatRunner(exe).ProtectAsync(src, dst,
                new MistcoatOptions { Preset = "light", Quick = true, Harden = false, Watermark = "SAMPLE", WatermarkLayout = "centre" }, new SyncProgress(p => stages.Add(p.Stage)),
                TestContext.Current.CancellationToken);
            Assert.True(result.Ok, result.Error);
            Assert.True(File.Exists(dst));
            Assert.True(File.Exists(src)); // the original is never touched
        }
        finally { dir.Delete(recursive: true); }
    }

    private sealed class SyncProgress(Action<MistcoatProgress> report) : IProgress<MistcoatProgress>
    {
        public void Report(MistcoatProgress value) { lock (this) report(value); }
    }

    /// <summary>A tiny 24-bit BMP with a gradient.</summary>
    private static void WriteBmp(string path, int w, int h)
    {
        var stride = (w * 3 + 3) & ~3;
        using var bw = new BinaryWriter(File.Create(path));
        bw.Write((byte)'B'); bw.Write((byte)'M'); bw.Write(54 + stride * h); bw.Write(0); bw.Write(54);
        bw.Write(40); bw.Write(w); bw.Write(h); bw.Write((short)1); bw.Write((short)24); bw.Write(0); bw.Write(stride * h);
        bw.Write(2835); bw.Write(2835); bw.Write(0); bw.Write(0);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++) { bw.Write((byte)(x * 4)); bw.Write((byte)(y * 5)); bw.Write((byte)((x + y) * 2)); }
            for (var p = w * 3; p < stride; p++) bw.Write((byte)0);
        }
    }
}

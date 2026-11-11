using System.Numerics;
using Blueline.Core;
using Blueline.Formats.Audio;
using Blueline.Formats.Models;
using Blueline.Formats.Textures;

namespace Blueline.Tests;

[TestClass]
public sealed class ModelAndAudioTests
{
    private static VirtualFileSystem? _vfs;
    public TestContext TestContext { get; set; } = null!;

    private static VirtualFileSystem Game
    {
        get
        {
            if (_vfs != null)
                return _vfs;
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            {
                var candidate = Environment.GetEnvironmentVariable("BLUELINE_GAME") ?? Path.Combine(d.FullName, "NHL 19", "CUSA11126");
                if (File.Exists(Path.Combine(candidate, "eboot.bin")))
                    return _vfs = VirtualFileSystem.Mount(GameInstall.Open(candidate, useModsFolder: false));
            }
            Assert.Inconclusive("No NHL 19 install found. Set BLUELINE_GAME.");
            return null!;
        }
    }

    [ClassCleanup]
    public static void Stop() => _vfs?.Dispose();

    [TestMethod]
    public void EveryModel_Parses_WithConsistentGeometry()
    {
        var models = Game.Files.Where(f => !f.IsLoose && f.Extension == ".mcd").OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
        int parsed = 0, withShapes = 0, paired = 0, unformatted = 0, notRsf = 0, totalShapes = 0;
        var failures = new List<string>();
        var noShapes = new List<string>();
        var encodings = new Dictionary<string, int>();

        foreach (var file in models)
        {
            try
            {
                var data = Game.ReadContent(file);
                if (!McdFile.IsMcd(data))
                {
                    notRsf++;
                    continue;
                }
                var model = McdFile.Parse(data);
                parsed++;
                if (model.ShapesWerePaired) paired++;
                if (model.Shapes.Count > 0) withShapes++;
                else if (model.Streams.Count > 0) noShapes.Add(file.Path);
                totalShapes += model.Shapes.Count;
                unformatted += model.Streams.Count(s => s.Format == null);
                foreach (var e in model.Formats.SelectMany(f => f.Elements))
                    encodings[e.Encoding] = encodings.GetValueOrDefault(e.Encoding) + 1;

                foreach (var shape in model.Shapes.Take(3))
                {
                    var positions = model.ReadPositions(model.Streams[shape.Stream]);
                    if (positions.Any(p => !float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z)))
                        failures.Add($"{file.Path}: {shape.Name} has non-finite positions");
                }
            }
            catch (Exception e)
            {
                failures.Add($"{file.Path}: {e.GetType().Name} {e.Message}");
            }
        }

        TestContext.WriteLine($"{models.Count} files: {parsed} parsed, {notRsf} not RSF, {withShapes} with shapes, {paired} by pairing, {noShapes.Count} streams without shapes, {totalShapes} shapes, {unformatted} streams with no matching format");
        foreach (var (k, v) in encodings.OrderByDescending(p => p.Value))
            TestContext.WriteLine($"  {v,7} {k}");
        foreach (var f in noShapes.Take(10))
            TestContext.WriteLine("no shapes: " + f);
        foreach (var f in failures.Take(25))
            TestContext.WriteLine("FAIL " + f);
        Assert.IsEmpty(failures, "See the test output.");
        Assert.IsGreaterThan(parsed * 0.95, withShapes * 1.0, "Too many models had geometry but no shapes.");
    }

    [TestMethod]
    public void Model_ExportsObj_WithMatchingCounts()
    {
        var file = Game.Files.Where(f => !f.IsLoose && f.Name.StartsWith("m_avg_hs2_anaheim_duck_hat_lod0", StringComparison.Ordinal)).First();
        var model = McdFile.Parse(Game.ReadContent(file));
        Assert.IsNotEmpty(model.Shapes);
        using var text = new StringWriter();
        model.WriteObj(text, file.Path);
        var lines = text.ToString().Split('\n');
        Assert.AreEqual(model.Shapes.Sum(s => model.Streams[s.Stream].Count), lines.Count(l => l.StartsWith("v ", StringComparison.Ordinal)));
        Assert.AreEqual(model.TriangleCount, lines.Count(l => l.StartsWith("f ", StringComparison.Ordinal)));
        TestContext.WriteLine($"{file.Name}: {model.Shapes.Count} shapes, {model.TriangleCount} triangles, names: {string.Join(", ", model.Shapes.Select(s => s.Name).Take(4))}");
    }

    [TestMethod]
    public void NamePlates_Bgra8_RoundTrip()
    {
        var file = Game.Find("nhl/data/kettle/rendering/characters/player/name_rbk_chicago_away.gnf");
        Assert.IsNotNull(file);
        var original = Game.ReadContent(file);
        var before = GameTexture.TryOpen(original)!;
        Assert.AreEqual("DDS", before.Container);
        Assert.IsNull(TexturePipeline.ImportBlocker(original));
        var image = before.DecodeMip(0);
        var rebuilt = TexturePipeline.Import(original, image);
        Assert.HasCount(original.Length, rebuilt.Gnf);
        CollectionAssert.AreEqual(image.Pixels, GameTexture.TryOpen(rebuilt.Gnf)!.DecodeMip(0).Pixels);
    }

    [TestMethod]
    public void LargeBanks_WalkEveryByte()
    {
        var banks = Game.Files.Where(f => !f.IsLoose && f.Extension == ".sbs").OrderByDescending(f => f.StoredSize).Take(8).ToList();
        foreach (var file in banks)
        {
            var samples = SoundBank.Scan(file.StoredSize, (offset, buffer) => file.Archive!.ReadRaw(file.Entry!, offset, buffer));
            var used = samples.Sum(s => (long)s.Length);
            var longest = samples.Max(s => s.Seconds);
            TestContext.WriteLine($"{file.Name}: {file.StoredSize >> 20} MB, {samples.Count} samples, {used * 100.0 / file.StoredSize:F1}% covered, longest {longest:F0}s, codecs {string.Join("/", samples.Select(s => s.CodecName).Distinct())}");
            Assert.IsGreaterThan(90.0, used * 100.0 / file.StoredSize, file.Path);
        }
    }

    private static string? FindSx()
    {
        var configured = Environment.GetEnvironmentVariable("BLUELINE_SX");
        if (configured != null)
            return configured;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var known = Path.Combine(home, "Downloads", "NHL 19 Mod Bench", "references", "Soundtrack Editing Toolkit", "Menu Music Editing", "sx.exe");
        return File.Exists(known) ? known : null;
    }

    [TestMethod]
    public async Task Sample_DecodesEncodes_AndReplacesInSameSlot()
    {
        if (FindSx() is not { } sxPath)
        {
            Assert.Inconclusive("sx.exe not found. Set BLUELINE_SX.");
            return;
        }

        var tool = new SxTool(sxPath);
        var file = Game.Files.First(f => !f.IsLoose && f.Name == "fesfxbank.sbs");
        var samples = SoundBank.Scan(file.StoredSize, (offset, buffer) => file.Archive!.ReadRaw(file.Entry!, offset, buffer));
        var target = samples.First(s => s.Codec == 0xC && s.Seconds is > 2 and < 60);
        var bytes = new byte[target.Length];
        file.Archive!.ReadRaw(file.Entry!, target.Offset, bytes);

        var folder = Directory.CreateTempSubdirectory("blueline-audio").FullName;
        try
        {
            var wav = Path.Combine(folder, "decoded.wav");
            await tool.DecodeAsync(bytes, wav);
            Assert.IsTrue(File.Exists(wav));
            var wavLength = new FileInfo(wav).Length;
            TestContext.WriteLine($"Decoded {target.Seconds:F2}s of {target.CodecName} to {wavLength:N0} bytes of WAV");
            Assert.IsGreaterThan(44100L, wavLength);

            var fitted = await tool.EncodeToFitAsync(wav, SxTool.FlagFor(target)!, target.Length);
            Assert.IsNotNull(fitted, "A sample should fit its own slot.");
            TestContext.WriteLine($"Re-encoded at quality {fitted.Quality}: {fitted.Sample.Length:N0} of {target.Length:N0} bytes");

            var bank = Game.ReadContent(file);
            var edited = SoundBank.ReplaceSample(bank, target, fitted.Sample);
            Assert.HasCount(bank.Length, edited);

            var after = SoundBank.Scan(edited.Length, (offset, buffer) =>
            {
                var length = (int)Math.Min(buffer.Length, edited.Length - offset);
                edited.AsSpan((int)offset, length).CopyTo(buffer);
                return length;
            });
            Assert.HasCount(samples.Count, after);
            for (var i = 0; i < samples.Count; i++)
                Assert.AreEqual(samples[i].Offset, after[i].Offset, $"Sample {i} moved");
            Assert.AreEqual(target.Codec, after[target.Index].Codec);

            // The result must itself decode.
            var wav2 = Path.Combine(folder, "again.wav");
            await tool.DecodeAsync(fitted.Sample, wav2);
            Assert.IsGreaterThan(44100L, new FileInfo(wav2).Length);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}

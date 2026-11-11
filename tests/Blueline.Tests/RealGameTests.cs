using System.Diagnostics;
using BCnEncoder.Encoder;
using Blueline.Core;
using Blueline.Formats.Audio;
using Blueline.Formats.Eb;
using Blueline.Formats.Tdb;
using Blueline.Formats.Textures;

namespace Blueline.Tests;

/// <summary>
/// Tests against the real game. They are read-only on the install: everything written goes to a temp folder.
/// Point BLUELINE_GAME at an install to run them elsewhere; they are inconclusive when there is no game.
/// </summary>
[TestClass]
public sealed class RealGameTests
{
    private static VirtualFileSystem? _vfs;
    private static string _temp = "";

    public TestContext TestContext { get; set; } = null!;

    private static string? GameFolder()
    {
        var configured = Environment.GetEnvironmentVariable("BLUELINE_GAME");
        if (configured != null)
            return configured;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "NHL 19", "CUSA11126");
            if (File.Exists(Path.Combine(candidate, "eboot.bin")))
                return candidate;
        }
        return null;
    }

    [ClassInitialize]
    public static void Start(TestContext _)
    {
        _temp = Path.Combine(Path.GetTempPath(), "blueline-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
        if (GameFolder() is { } folder)
            _vfs = VirtualFileSystem.Mount(GameInstall.Open(folder, useModsFolder: false));
    }

    [ClassCleanup]
    public static void Stop()
    {
        _vfs?.Dispose();
        Directory.Delete(_temp, true);
    }

    private static VirtualFileSystem Game
    {
        get
        {
            if (_vfs == null)
                Assert.Inconclusive("No NHL 19 install found. Set BLUELINE_GAME.");
            return _vfs!;
        }
    }

    private GameFile FindOne(Func<GameFile, bool> match) =>
        Game.Files.Where(f => !f.IsLoose).OrderBy(f => f.Path, StringComparer.Ordinal).First(match);

    [TestMethod]
    public void EveryArchive_PassesAudit()
    {
        var entries = 0;
        foreach (var archive in Game.Archives)
        {
            var audit = EbAudit.Check(archive);
            entries += audit.Entries;
            Assert.IsTrue(audit.Clean, $"{archive.Name}: {string.Join("; ", audit.Problems.Take(5))}");
        }
        TestContext.WriteLine($"{Game.Archives.Count} archives, {entries:N0} entries, {Game.Files.Count:N0} merged paths");
    }

    [TestMethod]
    public void EveryPathHash_Matches()
    {
        var bad = 0;
        foreach (var archive in Game.Archives.Where(a => (a.Flags & EbArchive.FlagHashedRows) != 0))
            foreach (var entry in archive.Entries)
            {
                var path = entry.Path.StartsWith("./", StringComparison.Ordinal) ? entry.Path[2..] : entry.Path;
                if (EbPathHash.Compute(path) != entry.Hash)
                    bad++;
            }
        Assert.AreEqual(0, bad);
    }

    [TestMethod]
    public void EveryTextureCodec_DecodesAndMipSizesAgree()
    {
        var textures = Game.Files.Where(f => !f.IsLoose && f.Extension == ".gnf").ToList();
        var counts = new Dictionary<string, int>();
        var failures = new List<string>();
        var rng = new Random(19);

        // Headers across a sample, full decodes of a few of each format.
        foreach (var file in textures.OrderBy(_ => rng.Next()).Take(4000))
        {
            try
            {
                var data = Game.ReadContent(file);
                var texture = GameTexture.TryOpen(data);
                if (texture == null)
                {
                    if (data.Length > 0)
                        failures.Add($"{file.Path}: unrecognized");
                    continue;
                }
                var key = texture.Container + " " + texture.Summary.Split(", ")[1];
                counts[key] = counts.GetValueOrDefault(key) + 1;
                if (counts[key] > 6 || texture.Blocker != null)
                    continue;
                var mip = texture.MipCount - 1;
                var image = texture.DecodeMip(mip);
                Assert.AreEqual(image.Width * image.Height * 4, image.Pixels.Length);
            }
            catch (Exception e)
            {
                failures.Add($"{file.Path}: {e.GetType().Name} {e.Message}");
            }
        }
        foreach (var (k, v) in counts.OrderByDescending(c => c.Value))
            TestContext.WriteLine($"{v,6}  {k}");
        foreach (var failure in failures.Take(20))
            TestContext.WriteLine(failure);
        Assert.AreEqual(0, failures.Count, "See the test output.");
    }

    [TestMethod]
    public void Chunkzip_RoundTrips()
    {
        var checkedCount = 0;
        foreach (var file in Game.Files.Where(f => !f.IsLoose && f.StoredSize is > 1000 and < 2_000_000).OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            if (!Game.IsWrapped(file))
                continue;
            var content = Game.ReadContent(file);
            var wrapped = Chunkzip.Wrap(content, System.IO.Compression.CompressionLevel.Fastest);
            CollectionAssert.AreEqual(content, Chunkzip.Unwrap(wrapped), file.Path);
            if (++checkedCount == 300)
                break;
        }
        Assert.IsGreaterThan(50, checkedCount);
    }

    [TestMethod]
    public void TextureImport_ReproducesImage_ForEachEncodableCodec()
    {
        var seen = new Dictionary<TextureCodec, double>();
        var textures = Game.Files.Where(f => !f.IsLoose && f.Extension == ".gnf" && f.StoredSize is > 4000 and < 800_000)
            .OrderBy(f => f.Path, StringComparer.Ordinal);
        var clock = Stopwatch.StartNew();
        foreach (var file in textures)
        {
            if (clock.Elapsed > TimeSpan.FromMinutes(2))
                break;
            var head = Game.ReadHead(file, 0x100);
            if (!GnfTexture.IsGnf(head))
                continue;
            var info = GnfTexture.ReadInfo(head);
            if (seen.ContainsKey(info.Codec) || !BlockCodec.CanEncode(info.Codec) || info.Faces != 1)
                continue;

            var original = Game.ReadContent(file);
            if (TexturePipeline.ImportBlocker(original) != null)
                continue;
            var before = GameTexture.TryOpen(original)!.DecodeMip(0);
            var rebuilt = TexturePipeline.Import(original, before, CompressionQuality.Balanced);

            Assert.HasCount(original.Length, rebuilt.Gnf, $"{file.Path} changed size");
            CollectionAssert.AreEqual(original[..0x100], rebuilt.Gnf[..0x100], $"{file.Path} header changed");

            var after = GameTexture.TryOpen(rebuilt.Gnf)!.DecodeMip(0);
            var psnr = Psnr(before.Pixels, after.Pixels);
            seen[info.Codec] = psnr;
            TestContext.WriteLine($"{info.Codec}: {file.Path} {info.Width}x{info.Height} PSNR {psnr:F1} dB, {rebuilt.Elapsed.TotalMilliseconds:F0} ms");
            Assert.IsGreaterThan(30.0, psnr, $"{file.Path} ({info.Codec})");
        }
        Assert.IsGreaterThanOrEqualTo(3, seen.Count, "Too few codecs exercised.");
    }

    private static double Psnr(byte[] a, byte[] b)
    {
        double sum = 0;
        for (var i = 0; i < a.Length; i++)
        {
            var d = a[i] - b[i];
            sum += d * d;
        }
        return sum == 0 ? 99 : 10 * Math.Log10(255 * 255 / (sum / a.Length));
    }

    [TestMethod]
    public void AllDatabases_CrcsVerify()
    {
        var failures = new List<string>();
        var databases = Game.Files.Where(f => f.Extension == ".db").ToList();
        foreach (var file in databases)
        {
            var data = Game.ReadContent(file);
            if (!TdbFile.IsTdb(data))
                continue;
            var tdb = TdbFile.Parse(data);
            var checks = tdb.VerifyCrcs();
            var bad = checks.Where(c => !c.Matches).ToList();
            TestContext.WriteLine($"{file.Path}: {tdb.Tables.Count} tables, {checks.Count - bad.Count}/{checks.Count} CRCs match");
            if (bad.Count > 0)
                failures.Add($"{file.Path}: {string.Join(", ", bad.Take(4).Select(b => b.Name))}");
        }
        foreach (var f in failures)
            TestContext.WriteLine("FAIL " + f);
        Assert.AreEqual(0, failures.Count, "See the test output.");
    }

    [TestMethod]
    public void GameDatabase_CellEdit_KeepsCrcsValid_AndNames_FromMeta()
    {
        var file = FindOne(f => f.Path.EndsWith("/nhl_game.db", StringComparison.Ordinal));
        var data = Game.ReadContent(file);
        var tdb = TdbFile.Parse(data);
        var meta = Game.Find(file.Folder + "/nhl_game-meta.xml");
        Assert.IsNotNull(meta, "Schema not found beside the database.");
        tdb.ApplyMeta(System.Text.Encoding.UTF8.GetString(Game.ReadContent(meta)));
        var named = tdb.Tables.Count(t => t.Name != null);
        TestContext.WriteLine($"{tdb.Tables.Count} tables, {named} named");
        Assert.AreEqual(tdb.Tables.Count, named);

        var table = tdb.Tables.First(t => t.Count > 10 && t.Fields.Any(f => !f.IsInlineString && !f.IsPoolPointer && f.Depth >= 4));
        var field = table.Fields.First(f => !f.IsInlineString && !f.IsPoolPointer && f.Depth >= 4);
        var before = (long)table.Read(3, field);
        var neighbour = (long)table.Read(4, field);
        var value = before == 0 ? 1 : 0;
        table.Write(3, field, value);
        tdb.RepairCrcs();
        Assert.IsTrue(tdb.VerifyCrcs().All(c => c.Matches));

        var reopened = TdbFile.Parse(tdb.Data);
        var t2 = reopened.Tables.First(t => t.ShortName == table.ShortName);
        var f2 = t2.Fields.First(f => f.ShortName == field.ShortName);
        Assert.AreEqual((long)value, (long)t2.Read(3, f2));
        Assert.AreEqual(neighbour, (long)t2.Read(4, f2));
    }

    [TestMethod]
    public void Localization_RoundTrips_AndEditReadsBack()
    {
        var files = Game.Files.Where(f => f.Name.StartsWith("eng_", StringComparison.Ordinal) && f.Extension == ".db").ToList();
        Assert.IsNotEmpty(files, "No English dictionary found.");
        var file = files[0];
        var data = Game.ReadContent(file);
        var dictionary = new LocDictionary(data);
        TestContext.WriteLine($"{file.Path}: {dictionary.Rows.Count} rows");

        foreach (var row in dictionary.Rows)
            if (row.Key != null)
                Assert.AreEqual(row.Hash, LocDictionary.KeyHash(row.Key), row.Key);

        var unchanged = new LocDictionary(dictionary.Save());
        Assert.HasCount(dictionary.Rows.Count, unchanged.Rows);
        for (var i = 0; i < dictionary.Rows.Count; i++)
        {
            Assert.AreEqual(dictionary.Rows[i].Key, unchanged.Rows[i].Key);
            Assert.AreEqual(dictionary.Rows[i].Text, unchanged.Rows[i].Text);
        }

        var target = dictionary.Rows.First(r => r.Key != null && r.Text is { Length: > 3 });
        dictionary.SetText(target.Key!, "BLUELINE TEST");
        dictionary.AddKey("BLUELINE_TEST_KEY", "Added key");
        var saved = dictionary.Save();

        var reread = new LocDictionary(saved);
        Assert.IsTrue(reread.TryGet(target.Key!, out var edited));
        Assert.AreEqual("BLUELINE TEST", edited.Text);
        Assert.IsTrue(reread.TryGet("BLUELINE_TEST_KEY", out var added));
        Assert.AreEqual("Added key", added.Text);
        for (var i = 1; i < reread.Rows.Count; i++)
            Assert.IsLessThanOrEqualTo(reread.Rows[i].Hash, reread.Rows[i - 1].Hash);

        var tdb = TdbFile.Parse(saved);
        Assert.IsTrue(tdb.VerifyCrcs().All(c => c.Matches), string.Join(", ", tdb.VerifyCrcs().Where(c => !c.Matches).Select(c => c.Name)));
    }

    [TestMethod]
    public void SoundBanks_WalkToAPlausibleEnd()
    {
        var banks = Game.Files.Where(f => !f.IsLoose && f.Extension == ".sbs").OrderBy(f => f.StoredSize).Take(12).ToList();
        Assert.IsNotEmpty(banks);
        foreach (var file in banks)
        {
            var samples = SoundBank.Scan(file.StoredSize, (offset, buffer) => file.Archive!.ReadRaw(file.Entry!, offset, buffer));
            var total = samples.Sum(s => (long)s.Length);
            TestContext.WriteLine($"{file.Name}: {samples.Count} samples, {total * 100 / file.StoredSize}% of bank, {samples.FirstOrDefault()?.CodecName}");
            Assert.IsNotEmpty(samples, file.Path);
            Assert.IsGreaterThan(50, total * 100 / file.StoredSize, file.Path);
            Assert.IsTrue(samples.All(s => s.SampleRate is > 4000 and < 96001), file.Path);
        }
    }

    [TestMethod]
    public void Project_BuildsVerifiedOverlay_AndWinsResolution()
    {
        var inxpFile = FindOne(f => f.Family == ArchiveFamily.Inxp && f.Extension == ".json" && Game.IsWrapped(f));
        var gameFile = FindOne(f => f.Family == ArchiveFamily.Game && f.Extension == ".gnf" && f.StoredSize < 200_000);

        var project = ModProject.Create(Path.Combine(_temp, "project"), "Test Mod!");
        Assert.AreEqual("test-mod", project.Slug);

        var replacement = System.Text.Encoding.UTF8.GetBytes("{\"blueline\":true}");
        project.Set(inxpFile.Path, replacement, "Edit json");
        project.Set(gameFile.Path, Game.ReadContent(gameFile), "Same texture");
        project.Set("nhl/blueline/new_file.txt", "hello"u8.ToArray(), "New file");

        var build = OverlayBuilder.Build(project, Game, project.BuildFolder);
        Assert.HasCount(2, build.Archives);
        Assert.IsTrue(build.Warnings.Any(w => w.Contains("new_file")), "A new path should warn.");

        using (var inxp = EbArchive.Open(build.Archives.First(a => a.FileName.StartsWith("patch_inxp_99")).Path))
        {
            var entry = inxp.Entries.First(e => e.Path == inxpFile.Path);
            CollectionAssert.AreEqual(replacement, inxp.ReadContent(entry));
            Assert.IsTrue(Chunkzip.IsWrapped(inxp.ReadRaw(entry)), "Wrapper should match the original.");
            Assert.AreEqual(2, inxp.Entries.Count);
        }

        // Undo, redo and reopen.
        project.Undo();
        Assert.IsFalse(project.TryGet("nhl/blueline/new_file.txt", out _));
        project.Redo();
        Assert.IsTrue(project.TryGet("nhl/blueline/new_file.txt", out _));
        var reopened = ModProject.Open(project.Folder);
        Assert.AreEqual(3, reopened.ChangeCount);
    }

    [TestMethod]
    public void Install_AndUninstall_LeaveOriginalsUntouched_OnASandbox()
    {
        var sandbox = Path.Combine(_temp, "sandbox");
        var databigs = Directory.CreateDirectory(Path.Combine(sandbox, "databigs")).FullName;
        File.WriteAllBytes(Path.Combine(sandbox, "eboot.bin"), [1]);
        File.WriteAllBytes(Path.Combine(databigs, "boot.big"), [2]);
        var install = GameInstall.Open(sandbox);

        var project = ModProject.Create(Path.Combine(_temp, "install-project"), "Install Test");
        project.Set("nhl/blueline/a.txt", [1, 2, 3], "File");
        var build = OverlayBuilder.Build(project, Game, project.BuildFolder);

        ModInstaller.Install(project, install, build);
        Assert.IsTrue(File.Exists(Path.Combine(databigs, "patch_inxp_99_install-test.big")));
        Assert.IsTrue(ModInstaller.IsInstalled(project, install));

        Assert.IsEmpty(ModInstaller.Uninstall(project, install));
        CollectionAssert.AreEqual(new[] { "boot.big" }, Directory.GetFiles(databigs).Select(Path.GetFileName).ToArray());
    }
}

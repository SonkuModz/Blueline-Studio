using System.Diagnostics;
using Blueline.Core;
using Blueline.Formats.Detect;
using Blueline.Formats.Video;

namespace Blueline.Tests;

/// <summary>Video against the game's own files. FFmpeg is the user's, so these are inconclusive without it.</summary>
[TestClass]
public sealed class VideoTests
{
    private const string Movies = "nhl/data/common/movie/";
    private const string Bootflow = Movies + "bootflow/press_start_reveal.mkv";
    private const string Threes = Movies + "threes/threes_europe_in.mkv";
    private const string Cove = Movies + "ones/intro_logo_cove.vp6";
    private static VirtualFileSystem _vfs = null!;
    private static FfmpegTool? _tool;
    private string _folder = "";
    public TestContext TestContext { get; set; } = null!;

    [ClassInitialize]
    public static void Mount(TestContext _)
    {
        _tool = FfmpegTool.Locate(null);
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null && _vfs == null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "NHL 19", "CUSA11126");
            if (File.Exists(Path.Combine(candidate, "eboot.bin")))
                _vfs = VirtualFileSystem.Mount(GameInstall.Open(candidate, useModsFolder: false));
        }
    }

    [ClassCleanup]
    public static void Unmount() => _vfs?.Dispose();

    [TestInitialize]
    public void Setup()
    {
        if (_vfs == null || _tool == null)
            Assert.Inconclusive("The game or FFmpeg is not here.");
        _folder = Path.Combine(Path.GetTempPath(), "BluelineVideoTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (_folder.Length > 0 && Directory.Exists(_folder))
            Directory.Delete(_folder, true);
    }

    private static byte[] Game(string path) => _vfs.ReadContent(_vfs.Find(path) ?? throw new AssertFailedException($"No such file: {path}"));

    private static string FirstCommentary() =>
        _vfs.Files.First(f => f.Path.Contains("/commentary/context_", StringComparison.Ordinal) && f.Path.EndsWith(".mkv", StringComparison.Ordinal)).Path;

    private string Save(string name, byte[] content)
    {
        var file = Path.Combine(_folder, name);
        File.WriteAllBytes(file, content);
        return file;
    }

    private async Task<string> Ffmpeg(params string[] arguments)
    {
        var info = new ProcessStartInfo(_tool!.Executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.CancellationTokenSource.Token);
        var errors = await process.StandardError.ReadToEndAsync(TestContext.CancellationTokenSource.Token);
        await process.WaitForExitAsync(TestContext.CancellationTokenSource.Token);
        Assert.AreEqual(0, process.ExitCode, errors);
        return await output + errors;
    }

    private async Task<int> FrameCount(string file, int track)
    {
        var text = await Ffmpeg("-hide_banner", "-nostats", "-i", file, "-map", $"0:v:{track}", "-f", "null", "-");
        Assert.IsFalse(text.Contains("Invalid data", StringComparison.Ordinal), text);
        var last = text.Split('\n').Last(l => l.Contains("frame=", StringComparison.Ordinal));
        return int.Parse(last[(last.IndexOf("frame=", StringComparison.Ordinal) + 6)..].TrimStart().Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture);
    }

    [TestMethod]
    public async Task ReadsTheGamesVideos()
    {
        var bootflow = await _tool!.ProbeAsync(Save("a.mkv", Game(Bootflow)));
        Assert.IsTrue(bootflow.IsMatroska);
        Assert.AreEqual("vp8", bootflow.Picture.Codec);
        Assert.AreEqual(1920, bootflow.Picture.Width);
        Assert.AreEqual("vorbis", bootflow.AudioCodec);
        Assert.AreEqual(8, bootflow.AudioChannels);
        Assert.IsFalse(bootflow.HasMatte);

        var commentary = await _tool.ProbeAsync(Save("b.mkv", Game(FirstCommentary())));
        Assert.IsTrue(commentary.HasMatte);
        Assert.AreEqual(2, commentary.Videos.Count);
        Assert.AreEqual("EAA", commentary.Videos[1].Language);

        var cove = await _tool.ProbeAsync(Save("c.vp6", Game(Cove)));
        Assert.IsTrue(cove.IsVp6);
        Assert.IsTrue(cove.HasVp6Matte);
        Assert.AreEqual("vp6", cove.Picture.Codec);
    }

    [TestMethod]
    public async Task PreviewsKeepEveryFrameAndAreCached()
    {
        var content = Game(Threes);
        var source = Save("source.mkv", content);
        var cache = new VideoPreviewCache(_tool!, Path.Combine(_folder, "cache"));
        var first = await cache.GetAsync(content, ".mkv", VideoTrack.Picture);
        Assert.AreEqual(await FrameCount(source, 0), await FrameCount(first, 0));
        var written = File.GetCreationTimeUtc(first);
        Assert.AreEqual(first, await cache.GetAsync(content, ".mkv", VideoTrack.Picture));
        Assert.AreEqual(written, File.GetCreationTimeUtc(first));
        Assert.AreEqual(1, Directory.GetFiles(Path.Combine(_folder, "cache"), "*.mp4").Length);
    }

    [TestMethod]
    public async Task Vp6MatteDecodesAsItsOwnVideo()
    {
        var content = Game(Cove);
        var matte = Save("matte.vp6", Vp6File.MatteAsVideo(content));
        Assert.AreEqual(await FrameCount(Save("cove.vp6", content), 0), await FrameCount(matte, 0));
    }

    [TestMethod]
    public async Task Vp6FramesFromAnAviRebuildTheExactFile()
    {
        foreach (var path in _vfs.Files.Where(f => f.Path.EndsWith(".vp6", StringComparison.Ordinal)).Select(f => f.Path).Take(6))
        {
            var content = Game(path);
            var source = Save("in.vp6", content);
            var avi = Path.Combine(_folder, "copy.avi");
            await Ffmpeg("-v", "error", "-y", "-i", source, "-c:v", "copy", "-f", "avi", avi);
            var picture = AviVideo.Read(File.ReadAllBytes(avi));
            CollectionAssert.AreEqual(content, Vp6File.ReplaceFrames(content, picture), path);
        }
    }

    [TestMethod]
    public async Task Vp6ReplacementIsRefusedWhenItDoesNotFit()
    {
        var content = Game(Cove);
        var avi = Path.Combine(_folder, "copy.avi");
        await Ffmpeg("-v", "error", "-y", "-i", Save("in.vp6", content), "-c:v", "copy", "-f", "avi", avi);
        var picture = AviVideo.Read(File.ReadAllBytes(avi));

        Assert.ThrowsExactly<InvalidDataException>(() => Vp6File.ReplaceFrames(content, picture with { Frames = picture.Frames.Skip(1).ToList() }));
        Assert.ThrowsExactly<InvalidDataException>(() => Vp6File.ReplaceFrames(content, picture with { Width = 640 }));
        Assert.ThrowsExactly<InvalidDataException>(() => Vp6File.ReplaceFrames(content, picture with { Codec = "VP62" }));
    }

    [TestMethod]
    public async Task ReplacementsMatchTheGamesFile()
    {
        var clip = Path.Combine(_folder, "clip.mp4");
        await Ffmpeg("-v", "error", "-y", "-f", "lavfi", "-i", "testsrc=size=1280x720:rate=30:duration=2", "-pix_fmt", "yuv420p", clip);

        var path = FirstCommentary();
        var target = await _tool!.ProbeAsync(Save("target.mkv", Game(path)));
        var result = await _tool.ConvertForGameAsync(clip, target);
        Assert.IsTrue(result.AddedSilence);
        Assert.IsTrue(result.Content.AsSpan(0, 64).IndexOf("webm"u8) >= 0, "The game's files are typed webm.");

        var file = Save("made.mkv", result.Content);
        var made = await _tool.ProbeAsync(file);
        Assert.AreEqual(target.Videos.Count, made.Videos.Count);
        Assert.AreEqual("EAA", made.Videos[1].Language);
        Assert.AreEqual(target.Picture.Width, made.Picture.Width);
        Assert.AreEqual(target.AudioChannels, made.AudioChannels);
        Assert.AreEqual(await FrameCount(file, 0), await FrameCount(file, 1));

        // The clip has no transparency, so its matte should be as white as the game's solid areas.
        var ours = await PeakBrightness(file, 1);
        var theirs = await PeakBrightness(Path.Combine(_folder, "target.mkv"), 1);
        TestContext.WriteLine($"Matte white: ours {ours}, the game's {theirs}");
        Assert.IsTrue(Math.Abs(ours - theirs) <= 6, $"Matte white: ours {ours}, the game's {theirs}");
    }

    private async Task<int> PeakBrightness(string file, int track)
    {
        var text = await Ffmpeg("-hide_banner", "-nostats", "-i", file, "-map", $"0:v:{track}", "-vf", "signalstats,metadata=print:key=lavfi.signalstats.YMAX:file=-", "-frames:v", "30", "-f", "null", "-");
        return text.Split('\n').Where(l => l.Contains("YMAX=", StringComparison.Ordinal))
            .Max(l => int.Parse(l[(l.IndexOf('=') + 1)..].Trim(), System.Globalization.CultureInfo.InvariantCulture));
    }

    [TestMethod]
    public void SimpleFormatsAreIdentified()
    {
        Assert.AreEqual(AssetKind.Image, FormatDetector.Identify(Game("nhl/data/kettle/saveload/000/icon0.png"), "icon0.png").Kind);
        Assert.AreEqual(AssetKind.Image, FormatDetector.Identify(Game("nhl/data/kettle/online/session.jpg"), "session.jpg").Kind);
        Assert.AreEqual(AssetCategory.Texture, AssetCategories.Of("x/session.jpg"));
    }
}

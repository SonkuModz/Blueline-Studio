using Blueline.Core;
using Blueline.Core.Teams;
using Blueline.Formats.Audio;

namespace Blueline.Tests;

/// <summary>
/// The goal horn bank and EA Opus. Read-only on the install; encodes go to a temp folder. Inconclusive without the game,
/// and the FFmpeg ones without FFmpeg.
/// </summary>
[TestClass]
public sealed class GoalHornTests
{
    private static byte[]? _bank;
    private static string _temp = "";

    [ClassInitialize]
    public static void Start(TestContext _)
    {
        _temp = Path.Combine(Path.GetTempPath(), "blueline-horn-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "NHL 19", "CUSA11126");
            if (!File.Exists(Path.Combine(candidate, "eboot.bin")))
                continue;
            using var vfs = VirtualFileSystem.Mount(GameInstall.Open(Environment.GetEnvironmentVariable("BLUELINE_GAME") ?? candidate, useModsFolder: false));
            _bank = vfs.Find(GoalHorns.BankPath) is { } file ? vfs.ReadContent(file) : null;
            break;
        }
    }

    [ClassCleanup]
    public static void Stop()
    {
        try { Directory.Delete(_temp, true); }
        catch (IOException) { }
    }

    private static byte[] Bank => _bank ?? throw new AssertInconclusiveException("No game install with the horn bank.");

    private static OpusTool Tool => FfmpegTool.Locate(null) is { } ffmpeg ? new OpusTool(ffmpeg) : throw new AssertInconclusiveException("No FFmpeg.");

    [TestMethod]
    public void HornNumbersFollowTheEnum()
    {
        Assert.AreEqual(127, GoalHorns.All.Count);
        Assert.AreEqual("Anaheim", GoalHorns.Find(1)!.Name);
        Assert.AreEqual("Vegas", GoalHorns.Find(31)!.Name);
        Assert.AreEqual("Generic", GoalHorns.Find(GoalHorns.Generic)!.Name);
        Assert.AreEqual("Air Horn 1", GoalHorns.Find(42)!.Name);
        Assert.AreEqual("Horn Jet Flyby", GoalHorns.Find(126)!.Name);
        Assert.AreEqual(100, GoalHorns.Parse("wolf")!.Value);
        Assert.AreEqual(41, GoalHorns.Parse("41")!.Value);
    }

    /// <summary>Heard in game with the spoken test bank.</summary>
    [TestMethod]
    public void NoveltyHornSamplesMatchWhatWasHeard()
    {
        Assert.AreEqual(63, GoalHorns.SampleOf(49));
        Assert.AreEqual(59, GoalHorns.SampleOf(53));
        Assert.IsTrue(GoalHorns.IsSampleConfirmed(126));
        Assert.IsFalse(GoalHorns.IsSampleConfirmed(GoalHorns.Generic));
        Assert.IsNull(GoalHorns.SampleOf(35));
    }

    /// <summary>The table is the bank's own list from entry 32 on: one distinct EA Opus sample per novelty horn.</summary>
    [TestMethod]
    public void NoveltySamplesAreDistinctOpusSamples()
    {
        var samples = HornBank.Samples(Bank);
        var picked = Enumerable.Range(GoalHorns.Generic + 1, 85).Select(h => GoalHorns.SampleOf(h)!.Value).ToList();
        Assert.AreEqual(85, picked.Distinct().Count());
        Assert.IsTrue(picked.All(i => OpusTool.CanHandle(samples[i])));
    }

    [TestMethod]
    public void BankHas410SamplesMostlyEaOpus()
    {
        var samples = HornBank.Samples(Bank);
        Assert.AreEqual(410, samples.Count);
        Assert.AreEqual(405, samples.Count(OpusTool.CanHandle));
        Assert.IsTrue(samples.All(s => s.SampleRate == 48000));
    }

    /// <summary>EA to Ogg and back gives the same bytes, so the block layout, sample counts and end padding are all understood.</summary>
    [TestMethod]
    public void EveryHornRoundTripsThroughOggUnchanged()
    {
        var samples = HornBank.Samples(Bank).Where(OpusTool.CanHandle).ToList();
        foreach (var sample in samples)
        {
            var original = HornBank.Read(Bank, sample);
            var chain = EaOpus.Read(original);
            Assert.AreEqual(312, chain.PreSkip, $"sample {sample.Index}");
            var rebuilt = EaOpus.FromOgg(EaOpus.ToOgg(original), chain.Header);
            CollectionAssert.AreEqual(original, rebuilt, $"sample {sample.Index}");
        }
    }

    [TestMethod]
    public async Task DecodedHornHasItsHeaderLength()
    {
        var sample = HornBank.Samples(Bank)[0];
        var wav = Path.Combine(_temp, "horn0.wav");
        await Tool.DecodeAsync(HornBank.Read(Bank, sample), wav);
        var frames = (new FileInfo(wav).Length - 44) / 4;
        Assert.AreEqual(sample.PcmSamples, frames, 2);
    }

    [TestMethod]
    public async Task ReplacementFitsItsSlotAndDecodes()
    {
        var bank = Bank;
        var samples = HornBank.Samples(bank);
        var source = Path.Combine(_temp, "source.wav");
        await Tool.DecodeAsync(HornBank.Read(bank, samples[3]), source);

        var result = await HornBank.ReplaceAsync(Tool, bank, 0, source);
        Assert.AreEqual(bank.Length, result.Bank.Length);
        Assert.IsTrue(result.Encoded.Sample.Length <= samples[0].Length);

        var made = EaOpus.Read(result.Encoded.Sample);
        Assert.AreEqual(2, made.Channels);
        Assert.IsTrue(made.Packets.All(p => p[0] == 0xFC), "every packet is CELT fullband 20 ms stereo, as in stock");

        var after = HornBank.Samples(result.Bank);
        Assert.AreEqual(samples.Count, after.Count);
        for (var i = 1; i < samples.Count; i++)
            Assert.AreEqual(samples[i].Offset, after[i].Offset, $"sample {i} moved");
        Assert.AreEqual(samples[3].PcmSamples, after[0].PcmSamples, 960);

        var wav = Path.Combine(_temp, "replaced.wav");
        await Tool.DecodeAsync(HornBank.Read(result.Bank, after[0]), wav);
        Assert.IsTrue(new FileInfo(wav).Length > 100_000);
    }

    [TestMethod]
    public async Task TooLongAudioIsRefused()
    {
        var bank = Bank;
        var samples = HornBank.Samples(bank);
        var shortest = samples.Where(OpusTool.CanHandle).OrderBy(s => s.Length).First();
        var longest = samples.Where(OpusTool.CanHandle).OrderByDescending(s => s.Length).First();
        var source = Path.Combine(_temp, "long.wav");
        await Tool.DecodeAsync(HornBank.Read(bank, longest), source);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => HornBank.ReplaceAsync(Tool, bank, shortest.Index, source));
    }

    [TestMethod]
    public void StockPacketsAreCeltFullband()
    {
        foreach (var sample in HornBank.Samples(Bank).Where(OpusTool.CanHandle))
            Assert.IsTrue(EaOpus.Read(HornBank.Read(Bank, sample)).Packets.All(p => p[0] == 0xFC), $"sample {sample.Index}");
    }
}

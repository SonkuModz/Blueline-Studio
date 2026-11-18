using Blueline.Core;
using Blueline.Formats.Flash;

namespace Blueline.Tests;

/// <summary>The Flash and ABC reader and writer against the game's own nhluidata.swf, which holds the score clock's code.</summary>
[TestClass]
public sealed class FlashTests
{
    public const string UiCode = "nhl/ui/lib/nhluidata.swf";
    public const string ScoreClockClass = "nhl.overlays.scoreclock::ScoreClockOverlay";

    private static byte[] ReadUiCode()
    {
        string? game = null;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null && game == null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "NHL 19", "CUSA11126", "eboot.bin")))
                game = Path.Combine(d.FullName, "NHL 19", "CUSA11126");
        if (game == null)
            Assert.Inconclusive("The game is not here.");
        using var vfs = VirtualFileSystem.Mount(GameInstall.Open(game, useModsFolder: false));
        return vfs.ReadContent(vfs.Find(UiCode)!);
    }

    [TestMethod]
    public void UiCodeRoundTripsUnchanged()
    {
        var data = ReadUiCode();
        var swf = SwfFile.Parse(data);
        Assert.AreEqual('C', swf.Signature);
        var abcTags = swf.Tags.Where(t => t.Code == SwfTag.DoAbc).ToList();
        Assert.IsTrue(abcTags.Count > 0);

        foreach (var tag in abcTags)
        {
            var (start, _) = SwfFile.AbcStart(tag.Body);
            var block = tag.Body[start..];
            Assert.IsTrue(AbcFile.Parse(block).ToBytes().AsSpan().SequenceEqual(block), "the ABC block writes back byte for byte");
        }

        var again = SwfFile.Parse(swf.ToBytes());
        Assert.IsTrue(again.Body().AsSpan().SequenceEqual(swf.Body()), "the movie writes back with the same tags");
        Assert.AreEqual(BitConverter.ToUInt32(data, 4), BitConverter.ToUInt32(swf.ToBytes(), 4), "the same length field");
    }

    [TestMethod]
    public void FindsTheScoreClocksMethodsAndDecodesThem()
    {
        var swf = SwfFile.Parse(ReadUiCode());
        var (abc, _) = OpenClock(swf);
        var clock = abc.Class(ScoreClockClass);
        foreach (var name in new[] { "_createScriptAnimClock", "_createScriptAnimGoal", "_setScores" })
        {
            var body = abc.BodyOf(abc.MethodOf(clock, name));
            var code = AbcCode.Decode(body.Code);
            Assert.AreEqual(body.Code.Length, code.Sum(i => i.Length), name + " decodes to its end");
        }
        // The clock's slide in: _framesToMilliseconds(14) on scoreClock, the only pushbyte 14 in the method.
        var slide = AbcCode.Decode(abc.BodyOf(abc.MethodOf(clock, "_createScriptAnimClock")).Code).Where(i => i.Name == "pushbyte" && i.Operands[0] == 14).ToList();
        Assert.AreEqual(1, slide.Count);
    }

    [TestMethod]
    public void AddedConstantsAndAChangedBodySurviveAWrite()
    {
        var swf = SwfFile.Parse(ReadUiCode());
        var (abc, tag) = OpenClock(swf);
        var strings = abc.Strings.Count;
        var s = abc.AddString("snGoalWipeBlue");
        var d = abc.AddDouble(0.123456);
        var n = abc.PublicName("snGoalWipeBlue");
        var body = abc.BodyOf(abc.MethodOf(abc.Class(ScoreClockClass), "_createScriptAnimClock"));
        var at = AbcCode.Decode(body.Code).Single(i => i.Name == "pushbyte" && i.Operands[0] == 14).Offset;
        body.Code[at + 1] = 120;
        abc.Changed(body);

        var (start, _) = SwfFile.AbcStart(tag.Body);
        tag.Body = [.. tag.Body[..start], .. abc.ToBytes()];
        var reread = SwfFile.Parse(swf.ToBytes());
        var (abc2, _) = OpenClock(reread);
        Assert.AreEqual(strings + 1, abc2.Strings.Count);
        Assert.AreEqual("snGoalWipeBlue", abc2.Strings[s]);
        Assert.AreEqual(0.123456, abc2.Doubles[d]);
        Assert.AreEqual("snGoalWipeBlue", abc2.NameOf(n));
        var body2 = abc2.BodyOf(abc2.MethodOf(abc2.Class(ScoreClockClass), "_createScriptAnimClock"));
        Assert.AreEqual(1, AbcCode.Decode(body2.Code).Count(i => i.Name == "pushbyte" && i.Operands[0] == 120));
    }

    internal static (AbcFile Abc, SwfTag Tag) OpenClock(SwfFile swf)
    {
        foreach (var tag in swf.Tags.Where(t => t.Code == SwfTag.DoAbc))
        {
            var abc = AbcFile.Parse(tag.Body[SwfFile.AbcStart(tag.Body).Start..]);
            if (abc.Instances.Any(i => abc.FullNameOf(i.Name) == ScoreClockClass))
                return (abc, tag);
        }
        throw new AssertFailedException("No ABC block holds the score clock.");
    }
}

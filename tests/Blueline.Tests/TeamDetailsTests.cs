using System.Text;
using Blueline.Core;
using Blueline.Core.Teams;
using Blueline.Formats.Tdb;

namespace Blueline.Tests;

/// <summary>The team details editor against the real game's team, arena and palette tables. Read-only on the install.</summary>
[TestClass]
public sealed class TeamDetailsTests
{
    private static VirtualFileSystem? _vfs;
    private static TeamCatalog? _catalog;

    [ClassInitialize]
    public static void Start(TestContext _)
    {
        var configured = Environment.GetEnvironmentVariable("BLUELINE_GAME");
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); configured == null && d != null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "NHL 19", "CUSA11126");
            if (File.Exists(Path.Combine(candidate, "eboot.bin")))
                configured = candidate;
        }
        if (configured == null)
            return;
        _vfs = VirtualFileSystem.Mount(GameInstall.Open(configured, useModsFolder: false));
        _catalog = TeamCatalog.Load(Read, _vfs.Files.Select(f => f.Path).ToList());
    }

    [ClassCleanup]
    public static void Stop() => _vfs?.Dispose();

    private static byte[]? Read(string path) => _vfs!.Find(path) is { } file ? _vfs.ReadContent(file) : null;

    private static TeamDetails Open(byte[]? data = null)
    {
        if (_vfs == null)
            Assert.Inconclusive("NHL 19 is not next to the repository and BLUELINE_GAME is not set.");
        var file = TdbFile.Parse(data ?? Read(TeamCatalog.DatabasePath)!);
        file.ApplyMeta(Encoding.UTF8.GetString(Read(TeamCatalog.MetaPath)!));
        return TeamDetails.Open(file);
    }

    private static TeamInfo Blues => _catalog!.Teams.Single(t => t.Name.StartsWith("St. Louis Blues", StringComparison.Ordinal) && t.LeagueId == 0);

    private static TeamField Field(TeamDetails details, string key) => details.FieldsOf(Blues.Index).Single(f => f.Key == key);

    [TestMethod]
    public void StLouis_ReadsItsColoursArenaAndPalette()
    {
        var details = Open();
        var fields = details.FieldsOf(Blues.Index);
        Assert.AreEqual(0x0C284D, Convert.ToInt32(Field(details, "team.overlayprimarycolor").Value));
        Assert.AreEqual(0xD6B033, Convert.ToInt32(Field(details, "team.overlaysecondarycolor").Value));
        Assert.AreEqual(0x00529B, Convert.ToInt32(Field(details, "team.feprimarycolor").Value));
        Assert.AreEqual(TeamFieldKind.Colour, Field(details, "team.overlayprimarycolor").Kind);
        Assert.AreEqual("STL", Field(details, "team.abbrname").Value);
        Assert.AreEqual(TeamFieldKind.Text, Field(details, "team.abbrname").Kind);
        Assert.AreEqual(TeamFieldKind.Team, Field(details, "team.rivalteam").Kind);
        StringAssert.StartsWith((string)Field(details, "arena.arenaname").Value, "Enterprise Center");
        Assert.IsTrue(fields.Any(f => f.Table == TeamTable.Palette), "The palette rows are listed.");
        Assert.IsTrue(Field(details, "team.artid").IsLink);

        // Every field of the team row is offered once.
        var teamTable = details.File.Tables.Single(t => t.Label == TeamDetails.TeamTableName);
        Assert.HasCount(teamTable.Fields.Count, fields.Where(f => f.Table == TeamTable.Team).ToList());
        CollectionAssert.AreEquivalent(fields.Select(f => f.Group).Distinct().ToList(), TeamDetails.Groups.Intersect(fields.Select(f => f.Group)).ToList());
    }

    [TestMethod]
    public void Edits_SaveVerifyAndReplayOntoAnotherCopy()
    {
        var details = Open();
        details.Set(Field(details, "team.overlayprimarycolor"), 0x00FF00);
        details.Set(Field(details, "team.abbrname"), "SLB");
        details.Set(Field(details, "team.teamname"), "St. Louis Notes®");
        details.Set(Field(details, "arena.maxattendance"), 18000L);
        var palette = details.FieldsOf(Blues.Index).First(f => f.Table == TeamTable.Palette);
        details.Set(palette, 0x123456);
        Assert.HasCount(5, details.Edits);
        var saved = details.Save();

        var reread = Open(saved);
        Assert.IsTrue(reread.File.VerifyCrcs().All(c => c.Matches));
        Assert.AreEqual(0x00FF00, Convert.ToInt32(Field(reread, "team.overlayprimarycolor").Value));
        Assert.AreEqual("SLB", Field(reread, "team.abbrname").Value);
        Assert.AreEqual("St. Louis Notes®", Field(reread, "team.teamname").Value);
        Assert.AreEqual(18000L, Convert.ToInt64(Field(reread, "arena.maxattendance").Value));
        Assert.AreEqual(0x123456, Convert.ToInt32(reread.FieldsOf(Blues.Index).First(f => f.Key == palette.Key).Value));
        Assert.AreEqual(0xD6B033, Convert.ToInt32(Field(reread, "team.overlaysecondarycolor").Value), "Neighbouring fields are untouched.");

        // Replaying onto a fresh copy gives the same bytes.
        var fresh = Open();
        TeamDetails.Replay(fresh, details.Edits);
        CollectionAssert.AreEqual(saved, fresh.Save());
    }

    [TestMethod]
    public void Edits_KeepGearChangesMadeMeanwhile()
    {
        // Another panel saved glove colours after this one opened: replaying keeps both.
        var gear = GearColours.Open(Open().File);
        var row = gear.RowsOf(GearColours.GearTeamOf(Blues)!.Value).First();
        gear.SetMain(row, 0xFF00FF);
        var withGear = gear.Save();

        var details = Open();
        details.Set(Field(details, "team.overlayprimarycolor"), 0x00FF00);
        var current = Open(withGear);
        TeamDetails.Replay(current, details.Edits);
        var merged = Open(current.Save());
        Assert.AreEqual(0x00FF00, Convert.ToInt32(Field(merged, "team.overlayprimarycolor").Value));
        Assert.AreEqual(0xFF00FF, GearColours.Open(merged.File).RowsOf(row.Team).Single(r => r.Index == row.Index).Main);
    }

    [TestMethod]
    public void BadValues_AreRefused()
    {
        var details = Open();
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => details.Set(Field(details, "team.overlayprimarycolor"), 0x1000000L));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => details.Set(Field(details, "team.abbrname"), "ABCDEFGHIJKL"));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => details.Set(Field(details, "team.avgattendance"), -1L));
        Assert.ThrowsExactly<InvalidOperationException>(() => details.Set(Field(details, "team.index"), 3L));
        Assert.ThrowsExactly<FormatException>(() => TeamDetails.Parse(Field(details, "team.overlayprimarycolor"), "blue"));
        Assert.AreEqual(0x0C284D, TeamDetails.Parse(Field(details, "team.overlayprimarycolor"), "#0c284d"));
        Assert.IsEmpty(details.Edits);
    }
}

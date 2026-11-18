using System.Text;
using Blueline.Core;
using Blueline.Core.Teams;
using Blueline.Formats.Tdb;

namespace Blueline.Tests;

/// <summary>
/// The roster save reader, writer, CRCValue and sync, against the player's own shadPS4 saves when they exist. The saves are
/// only read; everything written goes to a temporary folder.
/// </summary>
[TestClass]
public sealed class RosterSyncTests
{
    private static VirtualFileSystem? _vfs;
    private static string? _meta;
    private static string SaveData => RosterSync.SaveDataFolder(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "shadPS4"));

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
        _meta = Encoding.UTF8.GetString(_vfs.ReadContent(_vfs.Find(TeamCatalog.MetaPath)!));
    }

    [ClassCleanup]
    public static void Stop() => _vfs?.Dispose();

    private static RosterSave OpenSave(string name)
    {
        if (_vfs == null)
            Assert.Inconclusive("NHL 19 is not next to the repository and BLUELINE_GAME is not set.");
        var folder = Path.Combine(SaveData, name);
        if (!RosterSave.IsRosterSave(folder))
            Assert.Inconclusive($"There is no {name} save in {SaveData}.");
        return RosterSave.Read(folder);
    }

    private static string TempFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "blueline-roster-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static TdbFile Open(byte[] data)
    {
        var file = TdbFile.Parse((byte[])data.Clone());
        file.ApplyMeta(_meta!);
        return file;
    }

    private static byte[] GameDatabase() => _vfs!.ReadContent(_vfs.Find(TeamCatalog.DatabasePath)!);

    [TestMethod]
    [DataRow("RosterCustom0")]
    [DataRow("RosterOnline0")]
    public void CrcValue_MatchesTheGamesOwnSaves(string name)
    {
        var save = OpenSave(name);
        Assert.HasCount(RosterCrc.Length, save.CrcValue);
        Assert.IsTrue(TdbFile.Parse((byte[])save.Database.Clone()).VerifyCrcs().All(c => c.Matches));
        Assert.AreEqual(save.CrcValue, RosterCrc.Compute(TdbFile.Parse((byte[])save.Database.Clone()), _meta!));
    }

    [TestMethod]
    public void Repack_ReadsBackTheSameRosterWithTheSizeTail()
    {
        var save = OpenSave("RosterCustom0");
        var folder = Path.Combine(TempFolder(), "RosterCustom0");
        try
        {
            save.WriteTo(folder, save.Database, _meta!);
            var again = RosterSave.Read(folder);
            CollectionAssert.AreEqual(save.Database, again.Database);
            Assert.AreEqual(save.CrcValue, again.CrcValue);
            var data = File.ReadAllBytes(Path.Combine(folder, "DATA"));
            Assert.AreEqual(save.Database.Length + 18, BitConverter.ToInt32(data, data.Length - 4), "The record ends with the unpacked size.");
            Assert.IsTrue(File.Exists(Path.Combine(folder, "sce_sys", "param.sfo")), "The rest of the folder is copied.");
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(folder)!, true);
        }
    }

    [TestMethod]
    public void Sync_WritesOnlyTheProjectsCellsAndKeepsABackup()
    {
        var original = OpenSave("RosterCustom0");
        var root = TempFolder();
        try
        {
            var folder = Path.Combine(root, "RosterCustom0");
            RosterSave.CopyFolder(original.Folder, folder);

            // The project changes one team's score clock colour and its abbreviation.
            var game = GameDatabase();
            var project = Open(game);
            var teams = project.Tables.Single(t => t.Label == TeamDetails.TeamTableName);
            var abbr = teams.Fields.Single(f => f.Label == "abbrname");
            var colour = teams.Fields.Single(f => f.Label == "overlayprimarycolor");
            var row = Enumerable.Range(0, teams.Count).First(r => (string)teams.Read(r, abbr) == "STL");
            teams.Write(row, colour, 0x123456);
            teams.Write(row, abbr, "SLB");
            project.RepairCrcs();

            var save = RosterSave.Read(folder);
            var plan = RosterSync.Plan(game, project.Data, save, _meta!);
            Assert.HasCount(2, plan.Changes);
            Assert.IsTrue(plan.Changes.All(c => c.Table == TeamDetails.TeamTableName));
            var backup = RosterSync.Write(save, plan, _meta!, Path.Combine(root, "backups"));

            Assert.IsNotNull(backup);
            CollectionAssert.AreEqual(File.ReadAllBytes(Path.Combine(original.Folder, "DATA")), File.ReadAllBytes(Path.Combine(backup, "DATA")));
            var written = RosterSave.Read(folder);
            var before = Open(original.Database);
            var after = Open(written.Database);
            Assert.IsTrue(after.VerifyCrcs().All(c => c.Matches));
            Assert.AreEqual(written.CrcValue, RosterCrc.Compute(after, _meta!));

            // Only the two cells changed; every other row of every table is as the roster had it.
            var rosterTeams = after.Tables.Single(t => t.Label == TeamDetails.TeamTableName);
            var rosterRow = plan.Changes[0].RosterRow;
            Assert.AreEqual(0x123456L, Convert.ToInt64(rosterTeams.Read(rosterRow, rosterTeams.Fields.Single(f => f.Label == "overlayprimarycolor"))));
            Assert.AreEqual("SLB", rosterTeams.Read(rosterRow, rosterTeams.Fields.Single(f => f.Label == "abbrname")));
            foreach (var (a, b) in before.Tables.Zip(after.Tables))
                for (var r = 0; r < a.Count; r++)
                    if (!(a.Label == TeamDetails.TeamTableName && r == rosterRow))
                        Assert.IsTrue(before.Data.AsSpan(a.RecordsAt + r * a.Stride, a.Stride).SequenceEqual(after.Data.AsSpan(b.RecordsAt + r * b.Stride, b.Stride)), $"{a.Label} row {r} changed.");

            // A second sync has nothing left to do.
            var again = RosterSync.Plan(game, project.Data, written, _meta!);
            Assert.IsEmpty(again.Pending);
            Assert.IsNull(RosterSync.Write(written, again, _meta!, Path.Combine(root, "backups")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void Sync_MatchesGearRowsByKitAndAddsNewOnes()
    {
        var original = OpenSave("RosterCustom0");
        var root = TempFolder();
        try
        {
            var folder = Path.Combine(root, "RosterCustom0");
            RosterSave.CopyFolder(original.Folder, folder);
            var save = RosterSave.Read(folder);
            var rosterGear = Open(save.Database).Tables.Single(t => t.Label == GearColours.TableName);

            var game = GameDatabase();
            var project = Open(game);
            var gear = project.Tables.Single(t => t.Label == GearColours.TableName);
            static TdbField F(TdbTable t, string name) => t.Fields.Single(f => f.Label == name);
            static string Key(TdbTable t, int r) => string.Join("/", RosterSync.RowKeys[GearColours.TableName].Select(n => Convert.ToInt64(t.Read(r, F(t, n)))));
            static Dictionary<string, int> Counts(TdbTable t) => Enumerable.Range(0, t.Count).GroupBy(r => Key(t, r)).ToDictionary(g => g.Key, g => g.Count());
            var gameKeys = Counts(gear);
            var rosterKeys = Counts(rosterGear);

            // The project recolours a kit row the game and the roster both have once...
            var changed = Enumerable.Range(0, gear.Count).First(r => gameKeys[Key(gear, r)] == 1 && rosterKeys.GetValueOrDefault(Key(gear, r)) == 1);
            gear.Write(changed, F(gear, "zone1"), 0xABCDEF);
            // ...and adds a row for a kit neither has.
            long Number(TdbTable t, int r, string name) => Convert.ToInt64(t.Read(r, F(t, name)));
            bool SameGroup(TdbTable t, int r) => new[] { "team", "equipment", "era" }.All(n => Number(t, r, n) == Number(gear, changed, n));
            var variant = Enumerable.Range(0, gear.Count).Where(r => SameGroup(gear, r)).Select(r => Number(gear, r, "variant"))
                .Concat(Enumerable.Range(0, rosterGear.Count).Where(r => SameGroup(rosterGear, r)).Select(r => Number(rosterGear, r, "variant"))).Max() + 1;
            var added = gear.AddRow(changed);
            gear.Write(added, F(gear, "variant"), variant);
            gear.Write(added, F(gear, "index"), Enumerable.Range(0, gear.Count - 1).Max(r => Number(gear, r, "index")) + 1);
            gear.Write(added, F(gear, "zone2"), 0x13579B);
            project.RepairCrcs();
            var changedKey = Key(gear, changed);
            var addedKey = Key(gear, added);

            var plan = RosterSync.Plan(game, project.Data, save, _meta!);
            Assert.IsEmpty(plan.Skipped, string.Join(" ", plan.Skipped));
            Assert.HasCount(1, plan.Changes);
            Assert.AreEqual("zone1", plan.Changes[0].Field.Label);
            Assert.HasCount(1, plan.Adds);
            Assert.IsNotNull(RosterSync.Write(save, plan, _meta!, Path.Combine(root, "backups")));

            var written = RosterSave.Read(folder);
            var after = Open(written.Database);
            Assert.IsTrue(after.VerifyCrcs().All(c => c.Matches));
            Assert.AreEqual(written.CrcValue, RosterCrc.Compute(after, _meta!));
            var afterGear = after.Tables.Single(t => t.Label == GearColours.TableName);
            Assert.AreEqual(rosterGear.Count + 1, afterGear.Count);
            var rows = Enumerable.Range(0, afterGear.Count).ToLookup(r => Key(afterGear, r));
            Assert.AreEqual(0xABCDEFL, Number(afterGear, rows[changedKey].Single(), "zone1"));
            var newRow = rows[addedKey].Single();
            Assert.AreEqual(afterGear.Count - 1, newRow);
            Assert.AreEqual(0x13579BL, Number(afterGear, newRow, "zone2"));
            Assert.AreEqual(afterGear.Count, Enumerable.Range(0, afterGear.Count).Select(r => Number(afterGear, r, "index")).Distinct().Count(), "Indexes stay unique.");

            // A second sync neither adds the row again nor rewrites the cell.
            var again = RosterSync.Plan(game, project.Data, written, _meta!);
            Assert.AreEqual(0, again.ToWrite, RosterSync.Describe(again));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void Sync_OfTheStockDatabase_HasNothingToDo()
    {
        var save = OpenSave("RosterCustom0");
        var game = GameDatabase();
        var plan = RosterSync.Plan(game, game, save, _meta!);
        Assert.IsEmpty(plan.Changes);
        Assert.IsEmpty(plan.Skipped);
    }

    [TestMethod]
    public void XxHash32_MatchesKnownValues()
    {
        Assert.AreEqual(0x02CC5D05u, RosterSave.XxHash32([]));
        Assert.AreEqual(0x550D7456u, RosterSave.XxHash32("a"u8));
        Assert.AreEqual(0x32D153FFu, RosterSave.XxHash32("abc"u8));
        Assert.AreEqual(0xE2293B2Fu, RosterSave.XxHash32("Nobody inspects the spammish repetition"u8));
    }
}

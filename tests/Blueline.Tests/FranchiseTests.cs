using System.Text;
using System.Text.RegularExpressions;
using Blueline.Core;
using Blueline.Core.Franchise;
using Blueline.Core.Teams;
using Blueline.Formats.Tdb;

namespace Blueline.Tests;

/// <summary>
/// The Franchise save reader and writer, the league sync and the immersion data, against the stock game and the player's
/// own BeAGM0 when it exists. The save is only read; everything written goes to a temporary folder.
/// </summary>
[TestClass]
public sealed class FranchiseTests
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

    private static VirtualFileSystem Game => _vfs ?? throw new AssertInconclusiveException("NHL 19 is not next to the repository and BLUELINE_GAME is not set.");

    private static string SaveFolder()
    {
        _ = Game;
        var folder = Path.Combine(SaveData, "BeAGM0");
        if (!FranchiseSave.IsFranchiseSave(folder))
            Assert.Inconclusive($"There is no BeAGM0 save in {SaveData}.");
        return folder;
    }

    private static string TempFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "blueline-franchise-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static TdbFile Open(byte[] data)
    {
        var file = TdbFile.Parse((byte[])data.Clone());
        file.ApplyMeta(_meta!);
        return file;
    }

    private static TdbFile StockDatabase() => Open(Game.ReadContent(Game.Find(TeamCatalog.DatabasePath)!));

    [TestMethod]
    public void ReadsTheLeagueDatabaseWithoutTouchingTheSave()
    {
        var folder = SaveFolder();
        var before = File.GetLastWriteTimeUtc(Path.Combine(folder, "DATA"));
        var save = FranchiseSave.Read(folder);
        Assert.AreEqual(File.GetLastWriteTimeUtc(Path.Combine(folder, "DATA")), before);

        var league = Open(save.Database);
        Assert.IsTrue(league.VerifyCrcs().All(c => c.Matches));
        Assert.IsTrue(league.Tables.Any(t => t.Label == "leagueteams"));
        Assert.IsTrue(save.FileInfo.Contains("TeamFullName"));
    }

    [TestMethod]
    public void AnUneditedRepackReadsBackTheSame()
    {
        var save = FranchiseSave.Read(SaveFolder());
        var temp = TempFolder();
        try
        {
            var copy = Path.Combine(temp, "BeAGM0");
            save.WriteTo(copy, save.Database);
            var again = FranchiseSave.Read(copy);
            CollectionAssert.AreEqual(save.Database, again.Database);
            CollectionAssert.AreEqual(save.BlockSizes.ToList(), again.BlockSizes.ToList());
            Assert.AreEqual(save.TailLength, again.TailLength);
        }
        finally
        {
            Directory.Delete(temp, true);
        }
    }

    [TestMethod]
    public void SyncWritesIntoACopyAndReadsBack()
    {
        var save = FranchiseSave.Read(SaveFolder());
        var source = StockDatabase();
        FranchiseImmersion.ApplyArenas(source);
        source.RepairCrcs();
        var league = Open(save.Database);
        var plan = FranchiseSync.Plan(source, league);
        Assert.IsTrue(plan.Count > 0, "The 2025-26 arenas should differ from a 2018 franchise.");
        Assert.IsTrue(plan.All(c => c.Table is "leagueteams" or "leaguearena"));

        FranchiseSync.Apply(league, plan);
        Assert.AreEqual(0, FranchiseSync.Plan(source, league).Count, "A second sync should have nothing left to do.");
        var temp = TempFolder();
        try
        {
            var copy = Path.Combine(temp, "BeAGM0");
            save.WriteTo(copy, league.Data);
            var again = Open(FranchiseSave.Read(copy).Database);
            Assert.IsTrue(again.VerifyCrcs().All(c => c.Matches));
            Assert.AreEqual(0, FranchiseSync.Plan(source, again).Count);
        }
        finally
        {
            Directory.Delete(temp, true);
        }
    }

    [TestMethod]
    public void AffiliatesAreTranslatedToLeagueRows()
    {
        var save = FranchiseSave.Read(SaveFolder());
        var source = StockDatabase();
        var league = Open(save.Database);
        var teams = league.Tables.First(t => t.Label == "leagueteams");
        foreach (var change in FranchiseSync.Plan(source, league).Where(c => c.Field.Label == "ahlaffiliate"))
        {
            var farm = Convert.ToInt64(change.New);
            Assert.IsTrue(farm == 0 || teams.Read((int)farm - 1, teams.Fields.First(f => f.Label == "league")) is 1L or 1, $"{change.What} points at a row that is not an AHL club.");
        }
    }

    [TestMethod]
    public void RosterLeagueCopyRunsOnceOnAClone()
    {
        _ = Game;
        var folder = Path.Combine(SaveData, "RosterCustom0");
        if (!RosterSave.IsRosterSave(folder))
            Assert.Inconclusive($"There is no RosterCustom0 save in {SaveData}.");
        var roster = Open(RosterSave.Read(folder).Database);
        var source = StockDatabase();
        FranchiseImmersion.ApplyArenas(source);
        var plan = FranchiseSync.PlanRoster(source, roster);
        Assert.IsTrue(plan.Changes.All(c => c.Table is "exhibitionteams" or "exhibitionarena"));
        RosterSync.Apply(roster, plan);
        Assert.IsTrue(roster.VerifyCrcs().All(c => c.Matches));
        Assert.AreEqual(0, FranchiseSync.PlanRoster(source, roster).Pending.Count);
    }

    [TestMethod]
    public void ImmersionDataFitsAndRunsOnce()
    {
        var db = StockDatabase();
        var (cells, notFound) = FranchiseImmersion.ApplyArenas(db);
        Assert.IsTrue(cells > 0);
        // The data is matched by the 2025-26 names, so the stock (2018) database misses the clubs renamed or added since
        // (Seattle is not an exhibition club at all). Clubs that kept their names must all be found.
        string[] renamedSince = ["Seattle Kraken", "Utah Mammoth", "Abbotsford Canucks", "Hamilton Hammers", "Henderson Silver Knights",
            "Calgary Wranglers", "Coachella Valley Firebirds", "Colorado Eagles", "Laval Rocket", "Iowa Wild", "Cleveland Monsters",
            "Bakersfield Condors", "San Jose Barracuda", "Belleville Senators"];
        Assert.IsTrue(notFound.All(renamedSince.Contains), "Not found: " + string.Join(", ", notFound));
        Assert.AreEqual(20, FranchiseImmersion.ApplyInsiders(db));
        db.RepairCrcs();
        Assert.AreEqual(0, FranchiseImmersion.ApplyArenas(db).Cells);
        Assert.AreEqual(0, FranchiseImmersion.ApplyInsiders(db));
    }

    [TestMethod]
    public void TextFixesKeepEveryPlaceholder()
    {
        var text = new LocDictionary(Game.ReadContent(Game.Find(FranchiseImmersion.TextPath)!));
        var before = text.Rows.Where(r => r.Key != null).ToDictionary(r => r.Key!, r => r.Text);
        Assert.IsTrue(FranchiseImmersion.ApplyText(text) > 0);
        var placeholder = new Regex(@"\[<%\d+ \w+>\]|%\d+s");
        foreach (var row in text.Rows.Where(r => r.Key != null && r.Text != before[r.Key!]))
        {
            if (FranchiseImmersion.Rewrites.ContainsKey(row.Key!) || row.Key == FranchiseImmersion.AnalystNameKey)
                continue;
            CollectionAssert.AreEqual(placeholder.Matches(before[row.Key!]!).Select(m => m.Value).ToList(),
                placeholder.Matches(row.Text!).Select(m => m.Value).ToList(), row.Key);
        }
        Assert.AreEqual(FranchiseImmersion.AnalystName, text.Rows.First(r => r.Key == FranchiseImmersion.AnalystNameKey).Text);
        Assert.AreEqual(0, FranchiseImmersion.ApplyText(new LocDictionary(text.Save())));
    }
}

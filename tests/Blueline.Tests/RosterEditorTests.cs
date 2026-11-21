using Blueline.Core;
using Blueline.Core.Roster;
using Blueline.Core.Teams;
using Blueline.Formats.Tdb;

namespace Blueline.Tests;

/// <summary>
/// The roster editor's player model and its roster-save sync, on the stock game database (docs/ROSTER_EDITOR.md). Everything
/// works on copies in memory; nothing is written to the install, a save or %APPDATA%.
/// </summary>
[TestClass]
public sealed class RosterEditorTests
{
    private static VirtualFileSystem? _vfs;
    private static byte[]? _db;
    private static string? _meta;

    private const int Edmonton = 11, Toronto = 27, Pittsburgh = 23;

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
        _db = _vfs.ReadContent(_vfs.Find(TeamCatalog.DatabasePath)!);
        _meta = RosterSource.Meta(_vfs);
    }

    [ClassCleanup]
    public static void Stop() => _vfs?.Dispose();

    private static TdbFile Fresh()
    {
        if (_db == null)
            Assert.Inconclusive("NHL 19 is not next to the repository and BLUELINE_GAME is not set.");
        return RosterSource.Parse(_db, _meta!);
    }

    private static RosterBook Book() => RosterBook.Open(Fresh());

    private static PlayerSummary Player(RosterBook book, string name) => book.Players().Single(p => p.Name == name);

    private static object Value(RosterBook book, long gid, string key) => book.Find(gid, key)!.Value;

    [TestMethod]
    public void CrosbysBioDecodesToHisRealDetails()
    {
        var book = Book();
        var crosby = Player(book, "Sidney Crosby");
        Assert.AreEqual(3109, crosby.GameId);
        Assert.AreEqual(0, crosby.Position);
        Assert.AreEqual(87, crosby.Jersey);
        Assert.AreEqual(Pittsburgh, crosby.Club);
        Assert.AreEqual(1987L, Value(book, crosby.GameId, "bio.year"));
        Assert.AreEqual(8L, Value(book, crosby.GameId, "bio.month"));
        Assert.AreEqual(7L, Value(book, crosby.GameId, "bio.day"));
        Assert.AreEqual(71L, Value(book, crosby.GameId, "bio.height"));
        Assert.AreEqual(200L, Value(book, crosby.GameId, "bio.weight"));
        Assert.AreEqual(8_700_000L, Value(book, crosby.GameId, "bio.contractdollars"));
        Assert.AreEqual((long)Pittsburgh, Value(book, crosby.GameId, "bio.team"), "Bio team is stored shifted by one (range starts at -1).");
        Assert.AreEqual(2005L, Value(book, crosby.GameId, "bio.draftyear"));
        Assert.AreEqual("Cole Harbour", Value(book, crosby.GameId, "bio.birthplace"));
    }

    [TestMethod]
    public void McDavidIsAPlaymakerWithSixTeams()
    {
        var book = Book();
        var mcdavid = Player(book, "Connor McDavid");
        Assert.AreEqual(Edmonton, mcdavid.Club);
        Assert.AreEqual(6, mcdavid.Style);
        Assert.IsTrue(book.Memberships(mcdavid.GameId).Count >= 5);
        Assert.AreEqual(mcdavid.GameId, book.Lines(Edmonton)["l1c"]);
        Assert.AreEqual(1L, Value(book, mcdavid.GameId, $"roster.{Edmonton}.captain"));
        var groups = book.FieldsOf(mcdavid.GameId).Select(f => f.Group).Distinct().ToList();
        CollectionAssert.IsSubsetOf(new[] { RosterBook.NameGroup, RosterBook.RatingGroup, RosterBook.GearGroup, RosterBook.LinesGroup, RosterBook.TraitGroup }, groups);
    }

    [TestMethod]
    public void ShiftedFieldsStoreValueMinusTheirLowEnd()
    {
        var field = new TdbField("test", 0, 0, 10) { RangeLow = -1, RangeHigh = 1022 };
        Assert.AreEqual(12, RosterBook.Encode(field, 11));
        Assert.AreEqual(11, RosterBook.Decode(field, 12));
        Assert.AreEqual(-1, RosterBook.Decode(field, 0));
        var plain = new TdbField("test", 0, 0, 7) { RangeLow = 0, RangeHigh = 99 };
        Assert.AreEqual(50, RosterBook.Encode(plain, 50));
    }

    [TestMethod]
    public void NameAndRatingEditsSurviveASave()
    {
        var book = Book();
        var gid = Player(book, "Connor McDavid").GameId;
        book.Set(book.Find(gid, "bio.lastname")!, "McTest");
        book.Set(book.Find(gid, "skater.c_speed")!, 61L);
        book.Set(book.Find(gid, "bio.height")!, book.Parse(book.Find(gid, "bio.height")!, "6'4\""));
        book.Set(book.Find(gid, "bio.contractdollars")!, book.Parse(book.Find(gid, "bio.contractdollars")!, "13.25M"));
        var bytes = book.Save();

        var again = RosterBook.Open(RosterSource.Parse(bytes, _meta!));
        Assert.AreEqual("McTest", Value(again, gid, "bio.lastname"));
        Assert.AreEqual(61L, Value(again, gid, "skater.c_speed"));
        Assert.AreEqual(76L, Value(again, gid, "bio.height"));
        Assert.AreEqual(13_250_000L, Value(again, gid, "bio.contractdollars"));
        Assert.IsTrue(again.File.VerifyCrcs().All(c => c.Matches));
    }

    [TestMethod]
    public void OutOfRangeAndKeyEditsAreRefused()
    {
        var book = Book();
        var gid = Player(book, "Connor McDavid").GameId;
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => book.Set(book.Find(gid, "skater.c_speed")!, 100L));
        Assert.ThrowsExactly<InvalidOperationException>(() => book.Set(book.Find(gid, "bio.game_id")!, 1L));
        Assert.ThrowsExactly<InvalidOperationException>(() => book.Set(book.Find(gid, $"roster.{Edmonton}.team")!, (long)Toronto));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => book.Set(book.Find(gid, "bio.contractdollars")!, 12_501_000L));
    }

    [TestMethod]
    public void MovingAPlayerChangesHisTeamAndClearsHisLines()
    {
        var book = Book();
        var gid = Player(book, "Connor McDavid").GameId;
        book.Move(gid, Edmonton, Toronto);

        Assert.AreEqual(Toronto, book.Club(gid));
        Assert.AreEqual((long)Toronto, Value(book, gid, "bio.team"));
        Assert.AreEqual((long)Toronto, Value(book, gid, "bio.proteam"));
        CollectionAssert.Contains(book.PlayersOn(Toronto).ToList(), gid);
        CollectionAssert.DoesNotContain(book.PlayersOn(Edmonton).ToList(), gid);
        Assert.IsFalse(book.Lines(Edmonton).Values.Contains(gid));
        Assert.IsFalse(book.Lines(Toronto).Values.Contains(gid), "He starts on no line on the new team.");
        Assert.AreEqual(0L, Value(book, gid, $"roster.{Toronto}.captain"));
        Assert.ThrowsExactly<InvalidOperationException>(() => book.Move(gid, Edmonton, Pittsburgh));
    }

    [TestMethod]
    public void DraggingOntoAFilledSlotSwapsTheTwoPlayers()
    {
        var book = Book();
        var lines = book.Lines(Edmonton);
        var first = lines["l1c"];
        var second = lines["l2c"];
        book.SetSlot(Edmonton, "l1c", second);
        var after = book.Lines(Edmonton);
        Assert.AreEqual(second, after["l1c"]);
        Assert.AreEqual(first, after["l2c"]);

        book.SetSlot(Edmonton, "l1c", null);
        Assert.IsFalse(book.Lines(Edmonton).ContainsKey("l1c"));
    }

    [TestMethod]
    public void BulkRatingsStayInRange()
    {
        var book = Book();
        var oilers = book.PlayersOn(Edmonton).ToList();
        var changed = book.AddToRatings(oilers, 50);
        Assert.IsTrue(changed > 0);
        foreach (var gid in oilers)
            Assert.IsTrue(book.FieldsOf(gid).Where(f => f.Kind == PlayerFieldKind.Rating).All(f => Convert.ToInt64(f.Value) <= 99));
    }

    [TestMethod]
    public void ArchetypeAndNumberAreWrittenOnEveryTeam()
    {
        var book = Book();
        var gid = Player(book, "Connor McDavid").GameId;
        book.SetArchetype(gid, 7);
        book.SetJersey(gid, 11);
        Assert.AreEqual(7, book.Summary(gid)!.Style);
        foreach (var m in book.Memberships(gid))
        {
            Assert.AreEqual(7L, Value(book, gid, $"roster.{m.Team}.playerstyle"));
            Assert.AreEqual(11L, Value(book, gid, $"roster.{m.Team}.jersey"));
        }
    }

    [TestMethod]
    public void SyncFindsPlayersByGameIdEvenWhenRowsMoved()
    {
        var game = Fresh();
        var projectBook = Book();
        var gid = Player(projectBook, "Connor McDavid").GameId;
        projectBook.Set(projectBook.Find(gid, "bio.lastname")!, "McTest");
        projectBook.Set(projectBook.Find(gid, "skater.c_speed")!, 61L);
        projectBook.Move(gid, Edmonton, Toronto);
        projectBook.Save();

        // The "save": the same roster with McDavid's bio row swapped with the first row, as real saves order rows differently.
        var roster = Fresh();
        var bio = roster.Tables.First(t => t.Label == RosterBook.BioTable);
        var at = RosterBook.Open(roster).RowOf(PlayerTable.Bio, gid)!.Value;
        var a = roster.Data.AsSpan(bio.RecordsAt, bio.Stride).ToArray();
        var b = roster.Data.AsSpan(bio.RecordsAt + at * bio.Stride, bio.Stride).ToArray();
        b.CopyTo(roster.Data, bio.RecordsAt);
        a.CopyTo(roster.Data, bio.RecordsAt + at * bio.Stride);

        var plan = RosterSync.Plan(game, projectBook.File, roster);
        var name = plan.Pending.Single(c => c.Table == RosterBook.BioTable && c.Field.Label == "lastname");
        Assert.AreEqual(0, name.RosterRow, "Found at the save's own row, not the project's.");
        Assert.IsTrue(plan.Pending.Any(c => c.Table == RosterBook.RosterTable && c.Field.Label == "team" && Convert.ToInt64(c.Project) == Toronto));
        Assert.IsFalse(plan.Pending.Any(c => c.Field.Label is "key" or "playerindex"));

        RosterSync.Apply(roster, plan);
        var synced = RosterBook.Open(roster);
        Assert.AreEqual("McTest", Value(synced, gid, "bio.lastname"));
        Assert.AreEqual(61L, Value(synced, gid, "skater.c_speed"));
        Assert.AreEqual(Toronto, synced.Club(gid));
        Assert.IsTrue(roster.VerifyCrcs().All(c => c.Matches));
    }

    [TestMethod]
    public void SyncGivesALineSlotToItsHolderEvenWhenTheSaveHasOtherLines()
    {
        var game = Fresh();
        var projectBook = Book();
        var matthews = Player(projectBook, "Auston Matthews").GameId;
        var draisaitl = Player(projectBook, "Leon Draisaitl").GameId;
        projectBook.Move(matthews, Toronto, Edmonton);
        projectBook.SetSlot(Edmonton, "l2c", matthews);
        projectBook.Save();

        // The "save" has its own lines: Draisaitl at Line 2 C, as the user's real roster save does.
        var roster = Fresh();
        var saveBook = RosterBook.Open(roster);
        saveBook.SetSlot(Edmonton, "l2c", draisaitl, swap: false);
        var torontoBefore = saveBook.Lines(Toronto).Where(l => l.Value != matthews).ToDictionary();
        saveBook.Save();

        RosterSync.Apply(roster, RosterSync.Plan(game, projectBook.File, roster));
        var synced = RosterBook.Open(roster);
        Assert.AreEqual(Edmonton, synced.Club(matthews));
        Assert.AreEqual(matthews, synced.Lines(Edmonton)["l2c"]);
        var holders = synced.PlayersOn(Edmonton).Count(g => RosterBook.Raw(synced.File.Tables.First(t => t.Label == RosterBook.RosterTable),
            synced.MembershipOn(g, Edmonton)!.RosterRow, synced.File.Tables.First(t => t.Label == RosterBook.RosterTable).Fields.First(f => f.Label == "l2c")) == 1);
        Assert.AreEqual(1, holders, "Only the new holder keeps Line 2 C.");
        Assert.AreEqual(1, synced.Lines(Edmonton).Count(l => l.Value == matthews), "His Toronto slots did not follow him.");
        var torontoAfter = synced.Lines(Toronto);
        foreach (var (slot, gid) in torontoBefore)
            Assert.AreEqual(gid, torontoAfter.GetValueOrDefault(slot), $"The save's own Toronto {slot} was kept.");
        Assert.IsTrue(roster.VerifyCrcs().All(c => c.Matches));
    }

    [TestMethod]
    public void PortraitImportKeepsThePortraitFormat()
    {
        Fresh();
        var existing = _vfs!.Files.First(f => f.Path.StartsWith(RosterSource.PortraitFolder, StringComparison.Ordinal) && f.Extension == ".gnf");
        var original = Blueline.Formats.Textures.GameTexture.TryOpen(_vfs.ReadContent(existing))!;
        var pixels = new byte[64 * 64 * 4];
        for (var i = 0; i < pixels.Length; i += 4)
            (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (200, 30, 30, 255);
        var picture = new Blueline.Formats.Textures.RgbaImage(64, 64, pixels);

        // An art id with no portrait yet borrows another portrait's format.
        var (path, gnf, _) = RosterSource.ImportPortrait(_vfs, 987654, picture);
        Assert.AreEqual(RosterSource.PortraitPath(987654), path);
        var made = Blueline.Formats.Textures.GameTexture.TryOpen(gnf)!;
        Assert.IsNull(made.Blocker);
        Assert.AreEqual(original.Width, made.Width);
        Assert.AreEqual(original.Height, made.Height);
        var decoded = made.DecodeMip(0);
        Assert.IsTrue(decoded.Pixels[0] > 150 && decoded.Pixels[1] < 80, "The picture's colour came through.");
    }

    [TestMethod]
    public void YearShiftIsFoundAgainstTheStockDatabase()
    {
        var stock = Book();
        Assert.AreEqual(0, RosterBook.DetectYearShift(stock, Book()));

        var shifted = Fresh();
        var bio = shifted.Tables.First(t => t.Label == RosterBook.BioTable);
        var year = bio.Fields.First(f => f.Label == "year");
        for (var r = 0; r < bio.Count; r += 2)
            if (RosterBook.Raw(bio, r, year) >= 8)
                bio.Write(r, year, RosterBook.Raw(bio, r, year) - 8);
        Assert.AreEqual(8, RosterBook.DetectYearShift(stock, RosterBook.Open(shifted)));
    }

    [TestMethod]
    public void ChoicesComeFromTheGamesOwnLists()
    {
        var book = Book();
        var choices = RosterSource.Choices(_vfs!, book);
        Assert.AreEqual("Playmaker", choices.NameOf(RosterChoices.SkaterStyle, 6));
        Assert.AreEqual("Butterfly", choices.NameOf(RosterChoices.GoalieStyle, 2));
        Assert.AreEqual("Dangler", choices.NameOf(RosterChoices.Trait, 16));
        Assert.IsNotNull(choices.NameOf(RosterChoices.Origin, 9));
        Assert.IsTrue(choices.Get(RosterChoices.Gloves)!.Count > 10);
        Assert.IsTrue(choices.Get(RosterChoices.PlayByPlay)!.Count > 100);
        var mcdavid = Player(book, "Connor McDavid").GameId;
        Assert.AreEqual(7L, book.Parse(book.Find(mcdavid, "ai.playerstyle")!, "sniper", choices));
        Assert.AreEqual((long)Toronto, book.Parse(book.Find(mcdavid, "bio.team")!, "TOR", choices));
    }

    [TestMethod]
    public void SlotLabelsReadLikeTheGame()
    {
        Assert.AreEqual("Line 1 C", RosterBook.SlotLabel("l1c"));
        Assert.AreEqual("PP 2 LD", RosterBook.SlotLabel("pp2ld"));
        Assert.AreEqual("PK 3 1 RD", RosterBook.SlotLabel("pk3_1rd"));
        Assert.AreEqual("Shootout 3", RosterBook.SlotLabel("s3"));
        Assert.AreEqual("Starting goalie", RosterBook.SlotLabel("g1"));
    }
}

using Blueline.Formats.Tdb;

namespace Blueline.Core.Roster;

/// <summary>How a value is shown and typed. Stored values are converted (docs/ROSTER_EDITOR.md section 2).</summary>
public enum PlayerFieldKind { Text, Number, Rating, Flag, Choice, Colour, Year, Month, Day, Height, Weight, Money, Team }

public enum PlayerTable { Bio, SkaterAi, GoalieAi, SkaterGear, GoalieGear, Roster }

/// <summary>
/// One editable value of one player. <see cref="Value"/> is what the editor shows: a real year, inches, pounds, dollars, a
/// 1-based month or day, a team index (−1 none), or the field's own number. <see cref="Team"/> is set for roster-row fields:
/// the team of that membership.
/// </summary>
public sealed record PlayerField(PlayerTable Table, int Row, TdbField Field, string Group, string Label, PlayerFieldKind Kind, string? Choices,
    string? Hint, int? Team, object Value)
{
    public string Key => Table == PlayerTable.Roster ? $"roster.{Team}.{Field.Label}" : $"{TableKey(Table)}.{Field.Label}";

    public static string TableKey(PlayerTable table) => table switch
    {
        PlayerTable.Bio => "bio",
        PlayerTable.SkaterAi => "skater",
        PlayerTable.GoalieAi => "goalie",
        PlayerTable.SkaterGear => "skatergear",
        PlayerTable.GoalieGear => "goaliegear",
        _ => "roster",
    };

    public bool IsText => Field.IsInlineString;
    public int TextCapacity => Field.Depth / 8 - 1;
}

/// <summary>One team a player belongs to: his <c>exhibitionplayers</c> index and the roster row on that team.</summary>
public sealed record Membership(long PlayerIndex, int RosterRow, int Team);

/// <summary>One row of the player list.</summary>
public sealed record PlayerSummary(long GameId, string FirstName, string LastName, int Position, int Club, int Jersey, int Overall, int Style,
    bool Active, int ArtId)
{
    public string Name => $"{FirstName} {LastName}".Trim();
    public bool IsGoalie => Position == 4;
}

/// <summary>A team as the roster editor lists it.</summary>
public sealed record RosterTeam(int Index, string Name, string Abbreviation, int League);

/// <summary>
/// The players of an NHL 19 roster database (nhl_game.db or the TDB inside a roster save): bio, ratings, AI, gear and each
/// team membership with its lines. Values are read and written through the encodings in docs/ROSTER_EDITOR.md. A player is his
/// <c>game_id</c>; nothing here adds or removes rows.
/// </summary>
public sealed class RosterBook
{
    public const string BioTable = "exhibitionplayerbiotable", SkaterAiTable = "exhibitionskaterai", GoalieAiTable = "exhibitiongoalieai",
        SkaterGearTable = "exhibitionskaterequipment", GoalieGearTable = "exhibitiongoalieequipment", MembershipTable = "exhibitionplayers",
        RosterTable = "exhibitionrostertable", TeamTable = "exhibitionteams", FreeAgentTable = "exhibitionfreeagents";

    /// <summary>The leagues whose teams count as a player's club: NHL (0) and AHL (1).</summary>
    public static readonly IReadOnlySet<int> ClubLeagues = new HashSet<int> { 0, 1 };

    private readonly TdbFile _file;
    private readonly Dictionary<PlayerTable, TdbTable> _tables = [];
    private readonly Dictionary<PlayerTable, Dictionary<long, int>> _rows = [];
    private readonly Dictionary<long, List<long>> _indexesOf = [];
    private readonly Dictionary<long, List<int>> _rosterRowsOf = [];
    private readonly TdbTable _memberships;
    private readonly Dictionary<int, RosterTeam> _teams = [];

    private RosterBook(TdbFile file)
    {
        _file = file;
        _tables[PlayerTable.Bio] = Table(file, BioTable);
        _tables[PlayerTable.SkaterAi] = Table(file, SkaterAiTable);
        _tables[PlayerTable.GoalieAi] = Table(file, GoalieAiTable);
        _tables[PlayerTable.SkaterGear] = Table(file, SkaterGearTable);
        _tables[PlayerTable.GoalieGear] = Table(file, GoalieGearTable);
        _tables[PlayerTable.Roster] = Table(file, RosterTable);
        _memberships = Table(file, MembershipTable);
        foreach (var table in new[] { PlayerTable.Bio, PlayerTable.SkaterAi, PlayerTable.GoalieAi, PlayerTable.SkaterGear, PlayerTable.GoalieGear })
        {
            var source = _tables[table];
            var key = Field(source, "game_id");
            var map = new Dictionary<long, int>();
            for (var r = 0; r < source.Count; r++)
                map.TryAdd(Raw(source, r, key), r);
            _rows[table] = map;
        }

        var index = Field(_memberships, "index");
        var playerId = Field(_memberships, "playerid");
        for (var r = 0; r < _memberships.Count; r++)
        {
            var gid = Raw(_memberships, r, playerId);
            if (!_indexesOf.TryGetValue(gid, out var list))
                _indexesOf[gid] = list = [];
            list.Add(Raw(_memberships, r, index));
        }
        var roster = _tables[PlayerTable.Roster];
        var playerIndex = Field(roster, "playerindex");
        for (var r = 0; r < roster.Count; r++)
        {
            var pi = Raw(roster, r, playerIndex);
            if (!_rosterRowsOf.TryGetValue(pi, out var list))
                _rosterRowsOf[pi] = list = [];
            list.Add(r);
        }

        if (file.Tables.FirstOrDefault(t => t.Label == TeamTable) is { } teams)
        {
            var ti = Field(teams, "index");
            var name = teams.Fields.FirstOrDefault(f => f.Label == "teamname");
            var abbr = teams.Fields.FirstOrDefault(f => f.Label == "abbrname");
            var league = teams.Fields.FirstOrDefault(f => f.Label == "league");
            for (var r = 0; r < teams.Count; r++)
            {
                var i = (int)Raw(teams, r, ti);
                _teams[i] = new RosterTeam(i, name == null ? $"Team {i}" : ((string)teams.Read(r, name)).TrimEnd(),
                    abbr == null ? "" : (string)teams.Read(r, abbr), league == null ? -1 : (int)Raw(teams, r, league));
            }
        }
    }

    /// <summary>Reads a database that already has its field names applied.</summary>
    public static RosterBook Open(TdbFile file) => new(file);

    public TdbFile File => _file;

    /// <summary>Years added when showing a year, and taken off when one is typed: 8 for the Overtime roster, whose years are
    /// moved back 8 so ages fit NHL 19's 2018 calendar. See <see cref="DetectYearShift"/>.</summary>
    public int YearShift { get; set; }

    public IReadOnlyDictionary<int, RosterTeam> Teams => _teams;

    public string TeamName(int team) => team < 0 ? "None" : _teams.TryGetValue(team, out var t) ? t.Name : $"Team {team}";

    // ---- Players

    public IEnumerable<long> GameIds => _rows[PlayerTable.Bio].Keys;

    public bool Has(long gameId) => _rows[PlayerTable.Bio].ContainsKey(gameId);

    public PlayerSummary? Summary(long gameId)
    {
        if (!_rows[PlayerTable.Bio].TryGetValue(gameId, out var row))
            return null;
        var bio = _tables[PlayerTable.Bio];
        var position = (int)Value(bio, row, "position");
        var ai = position == 4 ? PlayerTable.GoalieAi : PlayerTable.SkaterAi;
        int overall = 0, style = 0;
        if (_rows[ai].TryGetValue(gameId, out var aiRow))
        {
            overall = (int)Value(_tables[ai], aiRow, "skill_overall");
            style = (int)Value(_tables[ai], aiRow, "playerstyle");
        }
        return new PlayerSummary(gameId, (string)bio.Read(row, Field(bio, "firstname")), (string)bio.Read(row, Field(bio, "lastname")), position,
            Club(gameId) ?? (int)Value(bio, row, "team"), (int)Value(bio, row, "jersey"), overall, style, Value(bio, row, "active") == 1,
            (int)Value(bio, row, "artid"));
    }

    public IReadOnlyList<PlayerSummary> Players() => GameIds.Select(id => Summary(id)!).ToList();

    public IReadOnlyList<Membership> Memberships(long gameId)
    {
        var list = new List<Membership>();
        if (!_indexesOf.TryGetValue(gameId, out var indexes))
            return list;
        var roster = _tables[PlayerTable.Roster];
        var team = Field(roster, "team");
        foreach (var index in indexes)
            foreach (var row in _rosterRowsOf.GetValueOrDefault(index) ?? [])
                list.Add(new Membership(index, row, (int)Raw(roster, row, team)));
        return list;
    }

    /// <summary>The team of his NHL or AHL membership, or null when he has none.</summary>
    public int? Club(long gameId) =>
        Memberships(gameId).Select(m => m.Team).Where(t => _teams.TryGetValue(t, out var info) && ClubLeagues.Contains(info.League)).Cast<int?>().FirstOrDefault();

    public Membership? MembershipOn(long gameId, int team) => Memberships(gameId).FirstOrDefault(m => m.Team == team);

    /// <summary>Every player with a roster row on the team.</summary>
    public IReadOnlyList<long> PlayersOn(int team)
    {
        var roster = _tables[PlayerTable.Roster];
        var teamField = Field(roster, "team");
        var playerIndex = Field(roster, "playerindex");
        var owner = OwnerByIndex();
        var list = new List<long>();
        for (var r = 0; r < roster.Count; r++)
            if (Raw(roster, r, teamField) == team && owner.TryGetValue(Raw(roster, r, playerIndex), out var gid))
                list.Add(gid);
        return list;
    }

    private Dictionary<long, long> OwnerByIndex()
    {
        var map = new Dictionary<long, long>();
        foreach (var (gid, indexes) in _indexesOf)
            foreach (var index in indexes)
                map.TryAdd(index, gid);
        return map;
    }

    // ---- Fields

    public const string NameGroup = "Name and number", BirthGroup = "Birth and body", ClubGroup = "Club", ContractGroup = "Contract",
        DraftGroup = "Draft and career", RatingGroup = "Ratings", CategoryGroup = "Overall and categories", StyleGroup = "Potential and style",
        TraitGroup = "Traits", TendencyGroup = "Tendencies and presentation", AppearanceGroup = "Appearance", GearGroup = "Gear",
        GearColourGroup = "Gear colours", LinesGroup = "Teams and lines", PersonalityGroup = "Personality and morale (Franchise)",
        OtherGroup = "Other";

    public static readonly IReadOnlyList<string> Groups =
    [
        NameGroup, BirthGroup, ClubGroup, ContractGroup, DraftGroup, CategoryGroup, RatingGroup, StyleGroup, TraitGroup, TendencyGroup,
        AppearanceGroup, GearGroup, GearColourGroup, LinesGroup, PersonalityGroup, OtherGroup,
    ];

    private sealed record Spec(string Group, string Label, PlayerFieldKind? Kind = null, string? Choices = null, string? Hint = null);

    private const string RatingHint = "0 to 99. The game's create-a-player screen edits this rating.";

    private static readonly Dictionary<string, Spec> BioSpecs = new(StringComparer.Ordinal)
    {
        ["firstname"] = new(NameGroup, "First name"),
        ["lastname"] = new(NameGroup, "Last name"),
        ["nickname"] = new(NameGroup, "Nickname", Hint: "Empty on stock players; where it shows is not known."),
        ["audioid"] = new(NameGroup, "Commentary id", PlayerFieldKind.Number, Hint: "The player's own commentary id, seen equal to his art id for McDavid (9857). It is not a playernames entry; which recording it picks is not mapped yet."),
        ["jersey"] = new(NameGroup, "Jersey number", Hint: "His preferred number. Each team he is on keeps its own number too (Teams and lines)."),
        ["position"] = new(NameGroup, "Position", PlayerFieldKind.Choice, RosterChoices.Position),
        ["handedness"] = new(NameGroup, "Shoots / catches", PlayerFieldKind.Choice, RosterChoices.Hand),

        ["year"] = new(BirthGroup, "Birth year", PlayerFieldKind.Year),
        ["month"] = new(BirthGroup, "Birth month", PlayerFieldKind.Month),
        ["day"] = new(BirthGroup, "Birth day", PlayerFieldKind.Day),
        ["birthplace"] = new(BirthGroup, "Birth city"),
        ["state"] = new(BirthGroup, "Born in", PlayerFieldKind.Choice, RosterChoices.Origin, "Province, state or country."),
        ["intlcountry"] = new(BirthGroup, "Nationality", PlayerFieldKind.Choice, RosterChoices.Origin, "The country he plays for: Canada, USA or the country itself."),
        ["height"] = new(BirthGroup, "Height (inches)", PlayerFieldKind.Height),
        ["weight"] = new(BirthGroup, "Weight (lb)", PlayerFieldKind.Weight),

        ["team"] = new(ClubGroup, "Team", PlayerFieldKind.Team, RosterChoices.Team, "The club his bio names. Moving him (Teams and lines) changes this too."),
        ["proteam"] = new(ClubGroup, "Pro team", PlayerFieldKind.Team, RosterChoices.Team),
        ["active"] = new(ClubGroup, "Active", Hint: "Retired players have 0."),
        ["rookie"] = new(ClubGroup, "NHL rookie"),
        ["rookieahl"] = new(ClubGroup, "AHL rookie"),
        ["rookiechl"] = new(ClubGroup, "Junior rookie"),

        ["contractdollars"] = new(ContractGroup, "Salary ($)", PlayerFieldKind.Money),
        ["contractlength"] = new(ContractGroup, "Years left"),
        ["contractis2way"] = new(ContractGroup, "Contract type", PlayerFieldKind.Choice, RosterChoices.ContractType),
        ["contractismultiyear"] = new(ContractGroup, "Multi-year"),
        ["contractstatus"] = new(ContractGroup, "Contract status", Hint: "−1 to 6; the kinds are not known."),
        ["contractneedsentrylevel"] = new(ContractGroup, "Needs entry-level deal"),
        ["extensiondollars"] = new(ContractGroup, "Extension salary ($)", PlayerFieldKind.Money),
        ["extensionlength"] = new(ContractGroup, "Extension years"),
        ["extensionis2way"] = new(ContractGroup, "Extension type", PlayerFieldKind.Choice, RosterChoices.ContractType),
        ["retainedsalarytrades"] = new(ContractGroup, "Salary-retained trades"),
        ["yearufa"] = new(ContractGroup, "UFA year", PlayerFieldKind.Year),
        ["yearwaivereligible"] = new(ContractGroup, "Waiver-eligible year", PlayerFieldKind.Year),
        ["yearfirstsigned"] = new(ContractGroup, "First signed", PlayerFieldKind.Year),

        ["draftteam"] = new(DraftGroup, "Drafted by", PlayerFieldKind.Team, RosterChoices.Team),
        ["draftyear"] = new(DraftGroup, "Draft year", PlayerFieldKind.Year),
        ["draftround"] = new(DraftGroup, "Draft round"),
        ["draftposition"] = new(DraftGroup, "Pick overall"),
        ["isdraftable"] = new(DraftGroup, "In the draft pool"),
        ["yearprodebut"] = new(DraftGroup, "Pro debut", PlayerFieldKind.Year),
        ["juniorteam"] = new(DraftGroup, "Junior team", PlayerFieldKind.Team, RosterChoices.Team),
        ["juniorleague"] = new(DraftGroup, "Junior league"),

        ["artid"] = new(AppearanceGroup, "Portrait / art id", Hint: "Picks the menu portrait playerportraits/p<art id>.gnf."),
        ["hasportrait"] = new(AppearanceGroup, "Has a portrait"),
        ["headid"] = new(AppearanceGroup, "Head model"),
        ["headtexture"] = new(AppearanceGroup, "Head texture"),
        ["skincolor"] = new(AppearanceGroup, "Skin tone"),
        ["hairid"] = new(AppearanceGroup, "Hair style", PlayerFieldKind.Choice, RosterChoices.HairStyle),
        ["haircolorcode"] = new(AppearanceGroup, "Hair colour", PlayerFieldKind.Choice, RosterChoices.HairColour),
        ["eyecolorcode"] = new(AppearanceGroup, "Eye colour", PlayerFieldKind.Choice, RosterChoices.EyeColour),
        ["eyebrowid"] = new(AppearanceGroup, "Eyebrows"),
        ["eyebrowcolorcode"] = new(AppearanceGroup, "Eyebrow colour"),
        ["playoffbeardtype"] = new(AppearanceGroup, "Beard style"),
        ["playoffbeardcolor"] = new(AppearanceGroup, "Beard colour"),
        ["playoffbeardstart"] = new(AppearanceGroup, "Beard length now"),
        ["playoffbeardmax"] = new(AppearanceGroup, "Playoff beard length"),
        ["playoffbeardgrowthrate"] = new(AppearanceGroup, "Beard growth rate"),
    };

    private static readonly Dictionary<string, Spec> SkaterSpecs = new(StringComparer.Ordinal)
    {
        ["skill_overall"] = new(CategoryGroup, "Overall", PlayerFieldKind.Rating, Hint: "The overall the menus show. Whether the game recomputes it from the ratings is to be seen."),
        ["skill_skate"] = new(CategoryGroup, "Skating", PlayerFieldKind.Rating),
        ["skill_shoot"] = new(CategoryGroup, "Shooting", PlayerFieldKind.Rating),
        ["skill_puck"] = new(CategoryGroup, "Puck skills", PlayerFieldKind.Rating),
        ["skill_sense"] = new(CategoryGroup, "Senses", PlayerFieldKind.Rating),
        ["skill_defense"] = new(CategoryGroup, "Defence", PlayerFieldKind.Rating),
        ["skill_physical"] = new(CategoryGroup, "Physical", PlayerFieldKind.Rating),
        ["c_speed"] = new(RatingGroup, "Speed", PlayerFieldKind.Rating, Hint: RatingHint),
        ["c_acceleration"] = new(RatingGroup, "Acceleration", PlayerFieldKind.Rating),
        ["c_agility"] = new(RatingGroup, "Agility", PlayerFieldKind.Rating),
        ["c_balance"] = new(RatingGroup, "Balance", PlayerFieldKind.Rating),
        ["c_endurance"] = new(RatingGroup, "Endurance", PlayerFieldKind.Rating),
        ["c_durability"] = new(RatingGroup, "Durability", PlayerFieldKind.Rating),
        ["c_puckcontrol"] = new(RatingGroup, "Puck control", PlayerFieldKind.Rating),
        ["c_deking"] = new(RatingGroup, "Deking", PlayerFieldKind.Rating),
        ["c_handeye"] = new(RatingGroup, "Hand-eye", PlayerFieldKind.Rating),
        ["c_passing"] = new(RatingGroup, "Passing", PlayerFieldKind.Rating),
        ["c_poise"] = new(RatingGroup, "Poise", PlayerFieldKind.Rating),
        ["c_wristshotpower"] = new(RatingGroup, "Wrist shot power", PlayerFieldKind.Rating),
        ["c_wristaccuracy"] = new(RatingGroup, "Wrist shot accuracy", PlayerFieldKind.Rating),
        ["c_slapshotpower"] = new(RatingGroup, "Slap shot power", PlayerFieldKind.Rating),
        ["c_slapaccuracy"] = new(RatingGroup, "Slap shot accuracy", PlayerFieldKind.Rating),
        ["c_offawareness"] = new(RatingGroup, "Offensive awareness", PlayerFieldKind.Rating),
        ["c_defawareness"] = new(RatingGroup, "Defensive awareness", PlayerFieldKind.Rating),
        ["c_shotblocking"] = new(RatingGroup, "Shot blocking", PlayerFieldKind.Rating),
        ["c_stickchecking"] = new(RatingGroup, "Stick checking", PlayerFieldKind.Rating),
        ["c_bodychecking"] = new(RatingGroup, "Body checking", PlayerFieldKind.Rating),
        ["c_strength"] = new(RatingGroup, "Strength", PlayerFieldKind.Rating),
        ["c_faceoffs"] = new(RatingGroup, "Faceoffs", PlayerFieldKind.Rating),
        ["c_aggression"] = new(RatingGroup, "Aggressiveness", PlayerFieldKind.Rating),
        ["c_discipline"] = new(RatingGroup, "Discipline", PlayerFieldKind.Rating),
        ["c_fightingskill"] = new(RatingGroup, "Fighting skill", PlayerFieldKind.Rating),
        ["c_fighting"] = new(StyleGroup, "Fights", PlayerFieldKind.Choice, RosterChoices.Fighting),
        ["playerstyle"] = new(StyleGroup, "Archetype", PlayerFieldKind.Choice, RosterChoices.SkaterStyle, "Each team he is on keeps a copy; Studio writes them all."),
        ["growthtier"] = new(StyleGroup, "Potential tier", Hint: "−1 to 14. How the game labels the tiers is to be seen in game."),
        ["growthletter"] = new(StyleGroup, "Potential likelihood", Hint: "−1 to 6: the likelihood letter."),
        ["growthspeed"] = new(StyleGroup, "Development speed"),
        ["growthage"] = new(StyleGroup, "Development age"),
        ["growthmod"] = new(StyleGroup, "Development modifier"),
        ["c_overall"] = new(StyleGroup, "c_overall", Hint: "Not the overall the menus show (that is Overall above). Meaning unknown."),
        ["c_potential"] = new(StyleGroup, "c_potential", Hint: "Not set by create-a-player. Meaning unknown."),
        ["off_defbias"] = new(TendencyGroup, "Offence / defence bias"),
        ["pass_carrybias"] = new(TendencyGroup, "Pass / carry bias"),
        ["shoot_passbias"] = new(TendencyGroup, "Shoot / pass bias"),
        ["skatingstyle"] = new(TendencyGroup, "Skating style", PlayerFieldKind.Choice, RosterChoices.SkatingStyle),
        ["goalcelebration"] = new(TendencyGroup, "Goal celebration", PlayerFieldKind.Choice, RosterChoices.Celebration),
        ["goalsongid"] = new(TendencyGroup, "Goal song"),
    };

    private static readonly Dictionary<string, Spec> GoalieSpecs = new(StringComparer.Ordinal)
    {
        ["skill_overall"] = SkaterSpecs["skill_overall"],
        ["skill_reflex"] = new(CategoryGroup, "Reflexes", PlayerFieldKind.Rating),
        ["skill_control"] = new(CategoryGroup, "Control", PlayerFieldKind.Rating),
        ["skill_athletics"] = new(CategoryGroup, "Athleticism", PlayerFieldKind.Rating),
        ["c_gsl"] = new(RatingGroup, "Glove low", PlayerFieldKind.Rating, Hint: RatingHint),
        ["c_gsh"] = new(RatingGroup, "Glove high", PlayerFieldKind.Rating),
        ["c_ssl"] = new(RatingGroup, "Stick low", PlayerFieldKind.Rating),
        ["c_ssh"] = new(RatingGroup, "Stick high", PlayerFieldKind.Rating),
        ["c_5hole"] = new(RatingGroup, "Five hole", PlayerFieldKind.Rating),
        ["c_positioning"] = new(RatingGroup, "Angles", PlayerFieldKind.Rating),
        ["c_breakaway"] = new(RatingGroup, "Breakaway", PlayerFieldKind.Rating),
        ["c_reboundcontrol"] = new(RatingGroup, "Rebound control", PlayerFieldKind.Rating),
        ["c_shotrecover"] = new(RatingGroup, "Recover", PlayerFieldKind.Rating),
        ["c_pokecheck"] = new(RatingGroup, "Poke check", PlayerFieldKind.Rating),
        ["c_vision"] = new(RatingGroup, "Vision", PlayerFieldKind.Rating),
        ["c_consistency"] = new(RatingGroup, "Consistency", PlayerFieldKind.Rating),
        ["c_puckplayfrequency"] = new(RatingGroup, "Puck playing frequency", PlayerFieldKind.Rating),
        ["c_speed"] = SkaterSpecs["c_speed"] with { Hint = null },
        ["c_agility"] = SkaterSpecs["c_agility"],
        ["c_endurance"] = SkaterSpecs["c_endurance"],
        ["c_durability"] = SkaterSpecs["c_durability"],
        ["c_passing"] = SkaterSpecs["c_passing"],
        ["c_poise"] = SkaterSpecs["c_poise"],
        ["c_aggression"] = SkaterSpecs["c_aggression"],
        ["c_fighting"] = SkaterSpecs["c_fighting"],
        ["playerstyle"] = new(StyleGroup, "Goalie style", PlayerFieldKind.Choice, RosterChoices.GoalieStyle),
        ["stance"] = new(StyleGroup, "Stance", PlayerFieldKind.Choice, RosterChoices.Stance),
        ["c_paddledown"] = new(StyleGroup, "Paddle down"),
        ["warmup"] = new(TendencyGroup, "Warm-up"),
        ["growthtier"] = SkaterSpecs["growthtier"],
        ["growthletter"] = SkaterSpecs["growthletter"],
        ["growthspeed"] = SkaterSpecs["growthspeed"],
        ["growthage"] = SkaterSpecs["growthage"],
        ["growthmod"] = SkaterSpecs["growthmod"],
        ["c_overall"] = SkaterSpecs["c_overall"],
        ["c_potential"] = SkaterSpecs["c_potential"],
    };

    private static readonly Dictionary<string, Spec> GearSpecs = new(StringComparer.Ordinal)
    {
        ["helmetbrand"] = new(GearGroup, "Helmet", PlayerFieldKind.Choice, RosterChoices.Helmet),
        ["gloves"] = new(GearGroup, "Gloves", PlayerFieldKind.Choice, RosterChoices.Gloves),
        ["stick"] = new(GearGroup, "Stick", PlayerFieldKind.Choice, RosterChoices.Stick),
        ["skatebrand"] = new(GearGroup, "Skates", PlayerFieldKind.Choice, RosterChoices.Skates),
        ["visortype"] = new(GearGroup, "Visor", PlayerFieldKind.Choice, RosterChoices.Visor),
        ["visorcolor"] = new(GearGroup, "Visor tint", PlayerFieldKind.Choice, RosterChoices.VisorTint),
        ["jerseytuck"] = new(GearGroup, "Jersey tuck", PlayerFieldKind.Choice, RosterChoices.JerseyTuck),
        ["pantstypecode"] = new(GearGroup, "Pant length", PlayerFieldKind.Choice, RosterChoices.Pants),
        ["earguard"] = new(GearGroup, "Ear guards"),
        ["mouthguard"] = new(GearGroup, "Mouth guard"),
        ["stickcurve"] = new(GearGroup, "Stick curve"),
        ["stickflex"] = new(GearGroup, "Stick flex"),
        ["shoulderpads"] = new(GearGroup, "Shoulder pads"),
        ["maskid"] = new(GearGroup, "Mask"),
        ["masktype"] = new(GearGroup, "Mask type"),
        ["blocker"] = new(GearGroup, "Blocker"),
        ["trapper"] = new(GearGroup, "Trapper"),
        ["pads"] = new(GearGroup, "Pads"),
        ["neckprotector"] = new(GearGroup, "Neck guard"),
        ["showcustomcolors"] = new(GearGroup, "Use his own colours", Hint: "Off: the team's gear colours."),
        ["showcustomstick"] = new(GearGroup, "Use his own stick colours"),
        ["keepcustomiftraded"] = new(GearGroup, "Keep colours when traded"),
        ["keepcustomstickiftraded"] = new(GearGroup, "Keep stick colours when traded"),
    };

    private static readonly Dictionary<string, Spec> RosterSpecs = new(StringComparer.Ordinal)
    {
        ["team"] = new(LinesGroup, "Team", PlayerFieldKind.Team, RosterChoices.Team, "Change it with Move, which also clears his lines."),
        ["jersey"] = new(LinesGroup, "Number on this team"),
        ["captain"] = new(LinesGroup, "Captaincy", PlayerFieldKind.Choice, RosterChoices.Captain),
        ["rosterstatus"] = new(LinesGroup, "Dressed", Hint: "1 on the active roster; 0 not (scratched or in the minors)."),
        ["playerstyle"] = new(LinesGroup, "Archetype on this team", PlayerFieldKind.Choice, RosterChoices.SkaterStyle),
    };

    /// <summary>Fields that are keys or links between tables. They are shown but cannot be changed.</summary>
    private static readonly HashSet<string> ReadOnlyFields = ["game_id", "key", "playerindex"];

    /// <summary>The line slots of a roster row: every one-bit field but the dressed flag.</summary>
    public IReadOnlyList<string> LineSlots => _tables[PlayerTable.Roster].Fields.Where(f => f.Depth == 1 && f.Label != "rosterstatus").Select(f => f.Label)
        .OrderBy(SlotOrder).ThenBy(s => s, StringComparer.Ordinal).ToList();

    private static int SlotOrder(string slot) => slot switch
    {
        _ when slot.StartsWith("l", StringComparison.Ordinal) && slot.Length <= 4 => 0,
        _ when slot.StartsWith("pp4_", StringComparison.Ordinal) => 2,
        _ when slot.StartsWith("pp", StringComparison.Ordinal) => 1,
        _ when slot.StartsWith("pk4_", StringComparison.Ordinal) => 3,
        _ when slot.StartsWith("pk3_", StringComparison.Ordinal) => 4,
        _ when slot.StartsWith("ot3_", StringComparison.Ordinal) => 6,
        _ when slot.StartsWith("ot", StringComparison.Ordinal) => 5,
        _ when slot.StartsWith("s", StringComparison.Ordinal) => 7,
        _ when slot.StartsWith("g", StringComparison.Ordinal) => 8,
        _ when slot.StartsWith("three", StringComparison.Ordinal) => 9,
        _ => 10,
    };

    /// <summary>"l1c" as "Line 1 C", "pp1ld" as "PP 1 LD", and so on.</summary>
    public static string SlotLabel(string slot)
    {
        var m = System.Text.RegularExpressions.Regex.Match(slot, "^(l|pp4_|pp|pk4_|pk3_|ot3_|ot_)(\\d)(lw|c|rw|ld|rd)$");
        if (m.Success)
        {
            var kind = m.Groups[1].Value switch
            {
                "l" => "Line", "pp" => "PP", "pp4_" => "PP 4-on-3", "pk4_" => "PK 4", "pk3_" => "PK 3", "ot_" => "OT 4-on-4", _ => "OT 3-on-3",
            };
            return $"{kind} {m.Groups[2].Value} {m.Groups[3].Value.ToUpperInvariant()}";
        }
        return slot switch
        {
            "g1" => "Starting goalie", "g2" => "Backup goalie", "x1" => "Extra attacker 1", "x2" => "Extra attacker 2",
            ['s', var n] when char.IsDigit(n) => $"Shootout {n}",
            _ when slot.StartsWith("three_", StringComparison.Ordinal) => "Threes " + slot[6..].ToUpperInvariant(),
            _ => slot,
        };
    }

    public IReadOnlyList<PlayerField> FieldsOf(long gameId)
    {
        var list = new List<PlayerField>();
        if (!Has(gameId))
            return list;
        var goalie = Summary(gameId)!.IsGoalie;
        void From(PlayerTable table, IReadOnlyDictionary<string, Spec> specs, string other)
        {
            if (!_rows[table].TryGetValue(gameId, out var row))
                return;
            foreach (var field in _tables[table].Fields)
            {
                if (field.IsPoolPointer)
                    continue;
                var spec = specs.GetValueOrDefault(field.Label) ?? Guess(table, field.Label, other);
                list.Add(Make(table, row, field, spec, null));
            }
        }
        From(PlayerTable.Bio, BioSpecs, OtherGroup);
        From(goalie ? PlayerTable.GoalieAi : PlayerTable.SkaterAi, goalie ? GoalieSpecs : SkaterSpecs, OtherGroup);
        From(goalie ? PlayerTable.GoalieGear : PlayerTable.SkaterGear, GearSpecs, GearGroup);

        var roster = _tables[PlayerTable.Roster];
        foreach (var membership in Memberships(gameId))
            foreach (var field in roster.Fields)
            {
                if (field.Depth == 1 && field.Label != "rosterstatus")
                    continue;
                var spec = RosterSpecs.GetValueOrDefault(field.Label) ?? new(LinesGroup, field.Label);
                list.Add(Make(PlayerTable.Roster, membership.RosterRow, field, spec with { Label = $"{TeamName(membership.Team)}: {spec.Label}" }, membership.Team));
            }

        var order = Groups.Select((g, i) => (g, i)).ToDictionary(p => p.g, p => p.i);
        int Rank(PlayerField f) => f.Table switch
        {
            PlayerTable.Bio => Index(BioSpecs, f.Field.Label),
            PlayerTable.SkaterAi => Index(SkaterSpecs, f.Field.Label),
            PlayerTable.GoalieAi => Index(GoalieSpecs, f.Field.Label),
            PlayerTable.Roster => Index(RosterSpecs, f.Field.Label),
            _ => Index(GearSpecs, f.Field.Label),
        };
        return list.OrderBy(f => order.GetValueOrDefault(f.Group, order.Count)).ThenBy(f => f.Team ?? -1).ThenBy(Rank)
            .ThenBy(f => NaturalKey(f.Field.Label), StringComparer.Ordinal).ToList();
    }

    private static int Index(Dictionary<string, Spec> specs, string label)
    {
        var i = 0;
        foreach (var key in specs.Keys)
        {
            if (key == label)
                return i;
            i++;
        }
        return int.MaxValue;
    }

    /// <summary>trait2 before trait10.</summary>
    private static string NaturalKey(string label) => System.Text.RegularExpressions.Regex.Replace(label, "\\d+", m => m.Value.PadLeft(4, '0'));

    private static Spec Guess(PlayerTable table, string field, string other)
    {
        if (field.StartsWith("trait", StringComparison.Ordinal) && int.TryParse(field[5..], out var n))
            return new(TraitGroup, $"Trait {n + 1}", PlayerFieldKind.Choice, table == PlayerTable.GoalieAi ? null : RosterChoices.Trait);
        if (field.EndsWith("color", StringComparison.Ordinal) || field.Contains("color", StringComparison.Ordinal))
            return new(table is PlayerTable.SkaterGear or PlayerTable.GoalieGear ? GearColourGroup : other, field);
        if (table == PlayerTable.Bio && (field.StartsWith("personality", StringComparison.Ordinal) || field.StartsWith("morale", StringComparison.Ordinal)
            || field.StartsWith("friendenemy", StringComparison.Ordinal) || field is "swagger" or "playercomp" or "playerclassid" or "expansiondraftstatus"))
            return new(PersonalityGroup, field);
        if (table == PlayerTable.Bio && field.StartsWith("nhl", StringComparison.Ordinal) || field == "progamesplayedcareer")
            return new(DraftGroup, field);
        if (table == PlayerTable.Bio && field.StartsWith("mascot", StringComparison.Ordinal) || field == "ismascot")
            return new(AppearanceGroup, field);
        return new(other, field);
    }

    private PlayerField Make(PlayerTable table, int row, TdbField field, Spec spec, int? team)
    {
        var kind = spec.Kind ?? (field.IsInlineString ? PlayerFieldKind.Text
            : field.Depth == 1 ? PlayerFieldKind.Flag
            : field.Depth == 24 && field.RangeHigh == 0xFFFFFF ? PlayerFieldKind.Colour
            : PlayerFieldKind.Number);
        var hint = ReadOnlyFields.Contains(field.Label) ? "A key that links tables. Read only." : spec.Hint;
        return new PlayerField(table, row, field, spec.Group, spec.Label, kind, spec.Choices, hint, team, Show(kind, _tables[table], row, field));
    }

    // ---- Encodings

    /// <summary>A field's stored bits as the number they mean: fields whose range starts below 0 store value − low.</summary>
    public static long Decode(TdbField field, long raw) => field.RangeLow is long low && low < 0 ? raw + low : raw;

    public static long Encode(TdbField field, long value) => field.RangeLow is long low && low < 0 ? value - low : value;

    public static long Raw(TdbTable table, int row, TdbField field) => Convert.ToInt64(table.Read(row, field));

    private long Value(TdbTable table, int row, string name) => Decode(Field(table, name), Raw(table, row, Field(table, name)));

    private object Show(PlayerFieldKind kind, TdbTable table, int row, TdbField field)
    {
        if (field.IsInlineString)
            return ((string)table.Read(row, field)).TrimEnd();
        var v = Decode(field, Raw(table, row, field));
        return kind switch
        {
            PlayerFieldKind.Year => v < 0 || v == 255 ? 0L : 1900 + v + YearShift,
            PlayerFieldKind.Month or PlayerFieldKind.Day => v + 1,
            PlayerFieldKind.Height => v + 54,
            PlayerFieldKind.Weight => v + 120,
            PlayerFieldKind.Money => v * 5000,
            _ => v,
        };
    }

    /// <summary>The decoded number a shown value stands for.</summary>
    private long Unshow(PlayerField field, long shown) => field.Kind switch
    {
        PlayerFieldKind.Year => shown == 0 ? (field.Field.RangeLow is < 0 ? -1 : 255) : shown - 1900 - YearShift,
        PlayerFieldKind.Month or PlayerFieldKind.Day => shown - 1,
        PlayerFieldKind.Height => shown - 54,
        PlayerFieldKind.Weight => shown - 120,
        PlayerFieldKind.Money => shown % 5000 == 0 ? shown / 5000 : throw new ArgumentOutOfRangeException(nameof(shown), "Salaries go in steps of $5,000."),
        _ => shown,
    };

    // ---- Writing

    /// <summary>Changes one value (as shown) and returns the field as it now reads. The caller saves with <see cref="Save"/>.</summary>
    public PlayerField Set(PlayerField field, object value)
    {
        if (ReadOnlyFields.Contains(field.Field.Label))
            throw new InvalidOperationException($"{field.Field.Label} links tables and cannot be changed.");
        if (field.Table == PlayerTable.Roster && field.Field.Label == "team")
            throw new InvalidOperationException("Use Move to change a player's team; it also clears his lines.");
        var table = _tables[field.Table];
        if (field.Field.IsInlineString)
            table.Write(field.Row, field.Field, value as string ?? throw new ArgumentException($"{field.Label} takes text."));
        else
            WriteNumber(table, field.Row, field.Field, Unshow(field, Convert.ToInt64(value)), field.Label);
        return field with { Value = Show(field.Kind, table, field.Row, field.Field) };
    }

    private static void WriteNumber(TdbTable table, int row, TdbField field, long value, string label)
    {
        var low = field.RangeLow ?? 0;
        var high = field.RangeHigh ?? (1L << field.Depth) - 1;
        if (value < low || value > high)
            throw new ArgumentOutOfRangeException(nameof(value), $"{label} takes {low} to {high:N0} as stored ({value} given).");
        table.Write(row, field, Encode(field, value));
    }

    /// <summary>The player's field with this key, e.g. bio.lastname, skater.c_speed or roster.11.jersey.</summary>
    public PlayerField? Find(long gameId, string key) =>
        FieldsOf(gameId).FirstOrDefault(f => f.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
        ?? (key.StartsWith("ai.", StringComparison.OrdinalIgnoreCase)
            ? FieldsOf(gameId).FirstOrDefault(f => f.Table is PlayerTable.SkaterAi or PlayerTable.GoalieAi && f.Field.Label.Equals(key[3..], StringComparison.OrdinalIgnoreCase))
            : null);

    /// <summary>
    /// Sets the archetype in the AI row and on every roster row, so all his teams agree. The value is the archetype number.
    /// </summary>
    public void SetArchetype(long gameId, long style)
    {
        var goalie = Summary(gameId)?.IsGoalie ?? throw new KeyNotFoundException($"No player {gameId}.");
        var ai = goalie ? PlayerTable.GoalieAi : PlayerTable.SkaterAi;
        if (_rows[ai].TryGetValue(gameId, out var row))
            WriteNumber(_tables[ai], row, Field(_tables[ai], "playerstyle"), style, "Archetype");
        var roster = _tables[PlayerTable.Roster];
        foreach (var m in Memberships(gameId))
            WriteNumber(roster, m.RosterRow, Field(roster, "playerstyle"), style, "Archetype");
    }

    /// <summary>Sets his number in the bio and on every team he is on.</summary>
    public void SetJersey(long gameId, int number)
    {
        if (!_rows[PlayerTable.Bio].TryGetValue(gameId, out var row))
            throw new KeyNotFoundException($"No player {gameId}.");
        var bio = _tables[PlayerTable.Bio];
        WriteNumber(bio, row, Field(bio, "jersey"), number, "Jersey");
        var roster = _tables[PlayerTable.Roster];
        foreach (var m in Memberships(gameId))
            WriteNumber(roster, m.RosterRow, Field(roster, "jersey"), number, "Jersey");
    }

    /// <summary>Adds to every 0-99 rating of these players (skater c_ ratings or goalie c_ ratings), clamped to 0..99. Returns cells changed.</summary>
    public int AddToRatings(IEnumerable<long> gameIds, int delta, bool includeOverall = true)
    {
        var changed = 0;
        foreach (var gid in gameIds)
            foreach (var field in FieldsOf(gid).Where(f => f.Kind == PlayerFieldKind.Rating && (includeOverall || f.Group != CategoryGroup)))
            {
                var value = Math.Clamp(Convert.ToInt64(field.Value) + delta, 0, 99);
                if (value != Convert.ToInt64(field.Value))
                {
                    Set(field, value);
                    changed++;
                }
            }
        return changed;
    }

    /// <summary>
    /// Moves a player's membership from one team to another: that roster row's team changes and its line slots are cleared, so he
    /// starts as a scratch on the new team. When the new team is an NHL or AHL club his bio's team (and pro team, for NHL) follow.
    /// No rows are added or removed.
    /// </summary>
    public void Move(long gameId, int fromTeam, int toTeam)
    {
        if (fromTeam == toTeam)
            return;
        if (!_teams.ContainsKey(toTeam))
            throw new KeyNotFoundException($"There is no team {toTeam}.");
        var membership = MembershipOn(gameId, fromTeam) ?? throw new InvalidOperationException($"{Summary(gameId)?.Name ?? gameId.ToString()} is not on {TeamName(fromTeam)}.");
        if (MembershipOn(gameId, toTeam) != null)
            throw new InvalidOperationException($"{Summary(gameId)!.Name} is already on {TeamName(toTeam)}.");
        var roster = _tables[PlayerTable.Roster];
        roster.Write(membership.RosterRow, Field(roster, "team"), toTeam);
        foreach (var slot in LineSlots)
            roster.Write(membership.RosterRow, Field(roster, slot), 0);
        roster.Write(membership.RosterRow, Field(roster, "captain"), 0);

        var league = _teams[toTeam].League;
        if (ClubLeagues.Contains(league) && _rows[PlayerTable.Bio].TryGetValue(gameId, out var row))
        {
            var bio = _tables[PlayerTable.Bio];
            WriteNumber(bio, row, Field(bio, "team"), toTeam, "Team");
            var pro = Field(bio, "proteam");
            if (league == 0 && toTeam <= (pro.RangeHigh ?? 62))
                WriteNumber(bio, row, pro, toTeam, "Pro team");
        }
    }

    /// <summary>Who holds each line slot on a team. A slot nobody holds is missing.</summary>
    public IReadOnlyDictionary<string, long> Lines(int team)
    {
        var roster = _tables[PlayerTable.Roster];
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var gid in PlayersOn(team))
        {
            var row = MembershipOn(gid, team)!.RosterRow;
            foreach (var slot in LineSlots)
                if (Raw(roster, row, Field(roster, slot)) == 1)
                    result.TryAdd(slot, gid);
        }
        return result;
    }

    /// <summary>
    /// Gives a slot to a player on that team (or to nobody, with null). Whoever held it loses it. When <paramref name="swap"/>
    /// is true and the incoming player held another slot of the same kind (both even-strength lines, say), the player he
    /// replaces takes that slot, so a drag between two filled slots swaps them.
    /// </summary>
    public void SetSlot(int team, string slot, long? gameId, bool swap = true)
    {
        var roster = _tables[PlayerTable.Roster];
        var field = Field(roster, slot);
        if (field.Depth != 1 || slot == "rosterstatus")
            throw new ArgumentException($"{slot} is not a line slot.");
        var lines = Lines(team);
        long? previous = lines.TryGetValue(slot, out var p) ? p : null;
        if (previous == gameId)
            return;
        Membership? incoming = null;
        if (gameId is { } gid)
            incoming = MembershipOn(gid, team) ?? throw new InvalidOperationException($"{Summary(gid)?.Name ?? gid.ToString()} is not on {TeamName(team)}.");

        string? vacated = null;
        if (swap && incoming != null && previous != null)
            vacated = lines.Where(l => l.Value == gameId && SlotOrder(l.Key) == SlotOrder(slot)).Select(l => l.Key).FirstOrDefault();

        if (previous is { } old)
            roster.Write(MembershipOn(old, team)!.RosterRow, field, 0);
        if (incoming != null)
        {
            if (vacated != null)
                roster.Write(incoming.RosterRow, Field(roster, vacated), 0);
            roster.Write(incoming.RosterRow, field, 1);
        }
        if (vacated != null && previous is { } displaced)
            roster.Write(MembershipOn(displaced, team)!.RosterRow, Field(roster, vacated), 1);
    }


    /// <summary>Repairs the checksums and returns the database bytes, or throws if they do not verify.</summary>
    public byte[] Save()
    {
        _file.RepairCrcs();
        if (_file.VerifyCrcs().FirstOrDefault(c => !c.Matches) is { } bad)
            throw new InvalidDataException($"The database checksum {bad.Name} did not verify after repair.");
        return (byte[])_file.Data.Clone();
    }

    // ---- Typed text

    /// <summary>Reads a value as typed: text, yes/no for flags, RRGGBB for colours, a choice by number or name, or a number.</summary>
    public object Parse(PlayerField field, string text, RosterChoices? choices = null)
    {
        if (field.IsText)
            return text;
        text = text.Trim();
        if (field.Kind == PlayerFieldKind.Flag)
            return text.ToLowerInvariant() switch
            {
                "1" or "yes" or "on" or "true" => 1L,
                "0" or "no" or "off" or "false" => 0L,
                _ => throw new FormatException($"{field.Label} is yes or no."),
            };
        if (field.Kind == PlayerFieldKind.Colour)
            return Blueline.Core.Teams.GearColours.TryParseHex(text, out var rgb) ? (long)rgb : throw new FormatException($"{text} is not a colour. Use six hex digits.");
        if (field.Kind == PlayerFieldKind.Money)
        {
            var clean = text.Replace("$", "").Replace(",", "").Trim();
            var scale = 1m;
            if (clean.EndsWith('m') || clean.EndsWith('M'))
                (clean, scale) = (clean[..^1], 1_000_000m);
            else if (clean.EndsWith('k') || clean.EndsWith('K'))
                (clean, scale) = (clean[..^1], 1_000m);
            return decimal.TryParse(clean, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var money)
                ? (long)Math.Round(money * scale / 5000m) * 5000 : throw new FormatException($"{text} is not an amount, e.g. 12.5M or 850000.");
        }
        if (field.Kind == PlayerFieldKind.Height && System.Text.RegularExpressions.Regex.Match(text, "^(\\d)'\\s*(\\d{1,2})\"?$") is { Success: true } feet)
            return long.Parse(feet.Groups[1].Value) * 12 + long.Parse(feet.Groups[2].Value);
        if (long.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var number))
            return number;
        var named = (field.Kind is PlayerFieldKind.Team ? choices?.Get(RosterChoices.Team) : choices?.Get(field.Choices))?
            .FirstOrDefault(c => c.Name.Equals(text, StringComparison.OrdinalIgnoreCase));
        if (named == null && field.Kind == PlayerFieldKind.Team)
        {
            var team = _teams.Values.FirstOrDefault(t => t.Abbreviation.Equals(text, StringComparison.OrdinalIgnoreCase) && ClubLeagues.Contains(t.League))
                ?? _teams.Values.FirstOrDefault(t => t.Name.Equals(text, StringComparison.OrdinalIgnoreCase));
            if (team != null)
                return (long)team.Index;
        }
        return named?.Value ?? throw new FormatException($"{text} is not a number{(field.Choices != null ? " or one of its names" : "")}.");
    }

    public string Format(PlayerField field, RosterChoices? choices = null) => field.Kind switch
    {
        PlayerFieldKind.Colour => Blueline.Core.Teams.GearColours.Hex(Convert.ToInt32(field.Value)),
        PlayerFieldKind.Team => $"{field.Value} {TeamName(Convert.ToInt32(field.Value))}",
        PlayerFieldKind.Height => $"{field.Value} ({Convert.ToInt64(field.Value) / 12}'{Convert.ToInt64(field.Value) % 12}\")",
        PlayerFieldKind.Money => $"{Convert.ToInt64(field.Value):N0}",
        PlayerFieldKind.Year when Convert.ToInt64(field.Value) == 0 => "0 (none)",
        PlayerFieldKind.Choice when choices?.NameOf(field.Choices, Convert.ToInt64(field.Value)) is { } name => $"{field.Value} {name}",
        _ => field.Value?.ToString() ?? "",
    };

    // ---- Years

    /// <summary>
    /// How many years the current roster moved birth years back from the stock database (8 for the Overtime roster), found from
    /// the players both have: the most common gap from 1 to 10 when it covers most players who differ, else 0.
    /// </summary>
    public static int DetectYearShift(RosterBook stock, RosterBook current)
    {
        // Retired and untouched players keep their years, so only the players whose year moved are weighed.
        var gaps = new Dictionary<long, int>();
        var bioS = stock._tables[PlayerTable.Bio];
        var bioC = current._tables[PlayerTable.Bio];
        var yearS = Field(bioS, "year");
        var yearC = Field(bioC, "year");
        foreach (var (gid, rowC) in current._rows[PlayerTable.Bio])
        {
            if (!stock._rows[PlayerTable.Bio].TryGetValue(gid, out var rowS))
                continue;
            var gap = Raw(bioS, rowS, yearS) - Raw(bioC, rowC, yearC);
            if (gap != 0)
                gaps[gap] = gaps.GetValueOrDefault(gap) + 1;
        }
        if (gaps.Count == 0)
            return 0;
        var top = gaps.MaxBy(g => g.Value);
        return top.Key is >= 1 and <= 10 && top.Value >= 50 && top.Value * 2 > gaps.Values.Sum() ? (int)top.Key : 0;
    }

    // ---- Helpers

    private static TdbTable Table(TdbFile file, string name) =>
        file.Tables.FirstOrDefault(t => t.Label == name) ?? throw new InvalidDataException($"The database has no {name} table.");

    internal static TdbField Field(TdbTable table, string name) =>
        table.Fields.FirstOrDefault(f => f.Label == name) ?? throw new InvalidDataException($"The {table.Label} table has no {name} field.");

    internal TdbTable TableOf(PlayerTable table) => _tables[table];

    public int? RowOf(PlayerTable table, long gameId) => _rows[table].TryGetValue(gameId, out var r) ? r : null;
}

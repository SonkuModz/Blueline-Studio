using Blueline.Formats.Tdb;

namespace Blueline.Core.Teams;

public enum TeamFieldKind { Text, Colour, Number, Flag, Team, GoalHorn }

/// <summary>Where a team's value lives: its team row, its arena's row, or one row of its six-colour palette.</summary>
public enum TeamTable { Team, Arena, Palette }

/// <summary>One editable value of one team, with what it is believed to do.</summary>
public sealed record TeamField(TeamTable Table, int Row, TdbField Field, string Group, string Label, TeamFieldKind Kind, string? Hint, bool IsLink, object Value)
{
    public string Key => Table == TeamTable.Palette ? $"palette.{Row}" : $"{Table.ToString().ToLowerInvariant()}.{Field.Label}";

    /// <summary>Largest value the field takes: the meta's high end, else what its bits hold.</summary>
    public long Max => Field.RangeHigh ?? (Field.Depth >= 63 ? long.MaxValue : (1L << Field.Depth) - 1);

    /// <summary>Bytes a text field holds, leaving room for the end marker.</summary>
    public int TextCapacity => Field.Depth / 8 - 1;
}

/// <summary>A change to one cell, kept so it can be replayed onto whatever copy of the database the project holds when saving.</summary>
public sealed record TeamEdit(TeamTable Table, int Row, string Field, object Value);

/// <summary>
/// Everything about a team the game database holds per team: names, the three colour sets, its palette, its arena, songs, goalie
/// masks, gear brands, mascot, effects and links. Read from <c>exhibitionteams</c>, <c>exhibitionarena</c> and
/// <c>stockteamcolorstable</c> in nhl_game.db. These are roster tables, so a roster save the player has loaded keeps its own copy.
/// </summary>
public sealed class TeamDetails
{
    public const string TeamTableName = "exhibitionteams";
    public const string ArenaTableName = "exhibitionarena";
    public const string PaletteTableName = "stockteamcolorstable";

    private readonly TdbFile _file;
    private readonly TdbTable _teams, _arenas, _palette;
    private readonly List<TeamEdit> _edits = [];

    private TeamDetails(TdbFile file)
    {
        _file = file;
        _teams = Table(file, TeamTableName);
        _arenas = Table(file, ArenaTableName);
        _palette = Table(file, PaletteTableName);
    }

    /// <summary>Reads a database that already has its field names applied.</summary>
    public static TeamDetails Open(TdbFile file) => new(file);

    public TdbFile File => _file;
    public IReadOnlyList<TeamEdit> Edits => _edits;

    // ---- What each field is

    private sealed record Spec(string Group, string Label, string? Hint = null, TeamFieldKind? Kind = null, bool Link = false);

    public const string NamesGroup = "Names";
    public const string OverlayGroup = "Score clock and in-game overlays";
    public const string MenuGroup = "Menu colours";
    public const string PresentationGroup = "Presentation colours";
    public const string PaletteGroup = "Team palette";
    public const string ArenaColourGroup = "Arena colours";
    public const string ArenaGroup = "Arena";
    public const string SoundGroup = "Songs and sound";
    public const string MaskGroup = "Goalie masks";
    public const string GearGroup = "Gear brands";
    public const string ClubGroup = "Club";
    public const string EffectGroup = "Lights and effects";
    public const string MascotGroup = "Mascot";
    public const string LinkGroup = "Links";
    public const string OtherGroup = "Other";

    /// <summary>The order groups are shown in.</summary>
    public static readonly IReadOnlyList<string> Groups =
    [
        NamesGroup, OverlayGroup, MenuGroup, PresentationGroup, PaletteGroup, ArenaColourGroup, ArenaGroup, SoundGroup, MaskGroup,
        GearGroup, ClubGroup, EffectGroup, MascotGroup, LinkGroup, OtherGroup,
    ];

    private const string LinkHint = "Points at other data (art, audio, arena or league). Changing it re-points this team, which can show another team's things or nothing.";

    private static readonly Dictionary<string, Spec> TeamSpecs = new(StringComparer.Ordinal)
    {
        ["cityname"] = new(NamesGroup, "City"),
        ["teamname"] = new(NamesGroup, "Team name"),
        ["nickname"] = new(NamesGroup, "Nickname", "Stock rows hold the same text as the team name."),
        ["abbrname"] = new(NamesGroup, "Abbreviation", "Three letters, as on the score clock."),
        ["mascotcustomname"] = new(NamesGroup, "Mascot name"),

        ["overlayprimarycolor"] = new(OverlayGroup, "Primary", "Calgary's is half as bright as its menu red, which matches the measured score clock panel."),
        ["overlaysecondarycolor"] = new(OverlayGroup, "Secondary"),
        ["overlaytertiarycolor"] = new(OverlayGroup, "Tertiary"),
        ["feprimarycolor"] = new(MenuGroup, "Primary", "Front-end (menu) colours. Studio's team swatches show these."),
        ["fesecondarycolor"] = new(MenuGroup, "Secondary"),
        ["fetertiarycolor"] = new(MenuGroup, "Tertiary"),
        ["presprimarycolor"] = new(PresentationGroup, "Primary", "Broadcast presentation colours."),
        ["pressecondarycolor"] = new(PresentationGroup, "Secondary"),
        ["prestertiarycolor"] = new(PresentationGroup, "Tertiary"),
        ["forceteamcolors"] = new(OverlayGroup, "Force team colours", "Set on no stock team."),

        ["goalpostcolor"] = new(ArenaColourGroup, "Goal posts"),
        ["stanchioncolor"] = new(ArenaColourGroup, "Stanchions"),
        ["stanchionzone1"] = new(ArenaColourGroup, "Stanchion zone 1"),
        ["stanchionzone2"] = new(ArenaColourGroup, "Stanchion zone 2"),
        ["stanchionzone3"] = new(ArenaColourGroup, "Stanchion zone 3"),
        ["boardtrimzone1"] = new(ArenaColourGroup, "Board trim zone 1"),
        ["boardtrimzone2"] = new(ArenaColourGroup, "Board trim zone 2"),
        ["boardtrimzone3"] = new(ArenaColourGroup, "Board trim zone 3"),
        ["goalspotlightzone1"] = new(ArenaColourGroup, "Goal spotlight"),
        ["skaterspotlightzone1"] = new(ArenaColourGroup, "Skater spotlight"),
        ["itechvisorcolor"] = new(GearGroup, "Visor colour"),

        ["goalsongid"] = new(SoundGroup, "Goal song"),
        ["winsongid"] = new(SoundGroup, "Win song"),
        ["powerplaysongid"] = new(SoundGroup, "Power play song"),
        ["intromusicid"] = new(SoundGroup, "Intro music"),
        ["chantaudioid"] = new(SoundGroup, "Crowd chant"),
        ["powerplaycrowdreaction"] = new(SoundGroup, "Power play crowd reaction"),
        ["mascotaudioid"] = new(SoundGroup, "Mascot sound"),
        ["outtro"] = new(SoundGroup, "Outro"),
        ["outropresentationid"] = new(SoundGroup, "Outro presentation"),

        ["helmetbrand"] = new(GearGroup, "Helmet brand"),
        ["gloves"] = new(GearGroup, "Gloves"),
        ["stick"] = new(GearGroup, "Stick"),
        ["puck"] = new(GearGroup, "Puck"),

        ["rivalteam"] = new(ClubGroup, "Rival", Kind: TeamFieldKind.Team),
        ["rivalteam2"] = new(ClubGroup, "Second rival", Kind: TeamFieldKind.Team),
        ["ahlaffiliate"] = new(ClubGroup, "AHL affiliate", Kind: TeamFieldKind.Team),
        ["avgattendance"] = new(ClubGroup, "Average attendance (% full)"),
        ["prestige"] = new(ClubGroup, "Prestige"),
        ["overallrating"] = new(ClubGroup, "Overall rating"),
        ["offenserating"] = new(ClubGroup, "Offence rating"),
        ["defenserating"] = new(ClubGroup, "Defence rating"),
        ["goalrating"] = new(ClubGroup, "Goalie rating"),
        ["coachstyle"] = new(ClubGroup, "Coach style"),
        ["coachheadid"] = new(ClubGroup, "Coach head"),
        ["state"] = new(ClubGroup, "State"),

        ["index"] = new(LinkGroup, "Team number", "The row's key. Read only.", Link: true),
        ["artid"] = new(LinkGroup, "Art id", "Which logos, ice and menu art the team uses (tN files). " + LinkHint, Link: true),
        ["artabbr"] = new(LinkGroup, "Art abbreviation", LinkHint, Link: true),
        ["audioid"] = new(LinkGroup, "Audio id", LinkHint, Link: true),
        ["audioid2"] = new(LinkGroup, "Second audio id", LinkHint, Link: true),
        ["arenaid"] = new(LinkGroup, "Arena", "Which arena row the team plays in; the Arena section follows it. " + LinkHint, Link: true),
        ["league"] = new(LinkGroup, "League", LinkHint, Link: true),
        ["leaguegroup"] = new(LinkGroup, "League group", LinkHint, Link: true),
        ["conferencegroup"] = new(LinkGroup, "Conference", LinkHint, Link: true),
        ["divisiongroup"] = new(LinkGroup, "Division", LinkHint, Link: true),
        ["teamtype"] = new(LinkGroup, "Team type", LinkHint, Link: true),
        ["active"] = new(LinkGroup, "Active", LinkHint, Link: true),
        ["centerline"] = new(LinkGroup, "Centre line art", LinkHint, Link: true),
        ["bouncemap"] = new(LinkGroup, "Bounce map", LinkHint, Link: true),
    };

    private static readonly Dictionary<string, Spec> ArenaSpecs = new(StringComparer.Ordinal)
    {
        ["arenaname"] = new(ArenaGroup, "Arena name"),
        ["cityname"] = new(ArenaGroup, "Arena city"),
        ["maxattendance"] = new(ArenaGroup, "Capacity"),
        ["yearopened"] = new(ArenaGroup, "Year opened"),
        ["index"] = new(LinkGroup, "Arena number", "The arena row's key. Read only.", Link: true),
        ["arenaartid"] = new(LinkGroup, "Arena art id", LinkHint, Link: true),
        ["arenaaudioid"] = new(LinkGroup, "Arena audio id", LinkHint, Link: true),
        ["cityaudioid"] = new(LinkGroup, "City audio id", LinkHint, Link: true),
        ["goalhorn"] = new(SoundGroup, "Goal horn", "Which horn the arena plays. Every stock AHL arena plays Generic (41). Its audio is in hornbank.sbr; change a horn's sound in the Goal horns panel.", TeamFieldKind.GoalHorn),
        ["periodhorn"] = new(SoundGroup, "Period horn"),
        ["goalhorniscustom"] = new(SoundGroup, "Built horn (Creation Zone)", "Plays the horn built from the parts below instead of the goal horn."),
        ["goalhornelement1"] = new(SoundGroup, "Built horn part 1"),
        ["goalhornelement2"] = new(SoundGroup, "Built horn part 2"),
        ["goalhornelement3"] = new(SoundGroup, "Built horn part 3"),
        ["goalhornflair"] = new(SoundGroup, "Built horn flair"),
        ["goalhornblastsnum"] = new(SoundGroup, "Built horn blasts"),
        ["goalhornblast1length"] = new(SoundGroup, "Blast 1 length"),
        ["goalhornpause1length"] = new(SoundGroup, "Pause 1 length"),
        ["goalhornblast2length"] = new(SoundGroup, "Blast 2 length"),
        ["goalhornpause2length"] = new(SoundGroup, "Pause 2 length"),
        ["goalhornblast3length"] = new(SoundGroup, "Blast 3 length"),
    };

    private static readonly Dictionary<string, int> TeamRanks = TeamSpecs.Keys.Select((k, i) => (k, i)).ToDictionary(p => p.k, p => p.i, StringComparer.Ordinal);
    private static readonly Dictionary<string, int> ArenaRanks = ArenaSpecs.Keys.Select((k, i) => (k, i)).ToDictionary(p => p.k, p => p.i, StringComparer.Ordinal);

    private static Spec Guess(TeamTable table, string field)
    {
        // Fields with no known meaning keep their database name as the label.
        if (table == TeamTable.Arena)
            return new(ArenaGroup, field);
        if (field.StartsWith("goaliemask", StringComparison.Ordinal))
        {
            var parts = field["goaliemask".Length..].Split('_');
            return new(MaskGroup, $"Mask {parts[0]} {(parts.Length > 1 ? parts[1] : "")}".Trim());
        }
        if (field.StartsWith("mascot", StringComparison.Ordinal) || field == "showmascot")
            return new(MascotGroup, field);
        if (field.StartsWith("signature", StringComparison.Ordinal) || field.StartsWith("stanchion", StringComparison.Ordinal)
            || field.Contains("spotlight", StringComparison.Ordinal) || field is "spotlight" or "powerring" or "tunnel" or "propid")
            return new(EffectGroup, field);
        if (field.StartsWith("franchise", StringComparison.Ordinal))
            return new(ClubGroup, field);
        if (field.StartsWith("centericelogo", StringComparison.Ordinal) || field.StartsWith("logostyle", StringComparison.Ordinal) || field == "mascotflagid")
            return new(LinkGroup, field, LinkHint, Link: true);
        return new(OtherGroup, field);
    }

    private static TeamFieldKind KindOf(TdbField field, Spec spec)
    {
        if (spec.Kind is { } kind)
            return kind;
        if (field.IsInlineString)
            return TeamFieldKind.Text;
        if (field.Depth == 1)
            return TeamFieldKind.Flag;
        if (field.Depth == 24 && field.RangeHigh == 0xFFFFFF)
            return TeamFieldKind.Colour;
        return TeamFieldKind.Number;
    }

    // ---- Reading

    /// <summary>The team's row in the team table, or null when the team number is not there.</summary>
    public int? TeamRow(int teamIndex) => RowWithKey(_teams, teamIndex);

    /// <summary>The arena row of the team's current arena.</summary>
    public int? ArenaRow(int teamIndex) =>
        TeamRow(teamIndex) is { } row ? RowWithKey(_arenas, Number(_teams, row, Field(_teams, "arenaid"))) : null;

    /// <summary>Numbers of the other teams that play in the same arena row, so an arena edit changes them too.</summary>
    public IReadOnlyList<int> TeamsSharingArena(int teamIndex)
    {
        if (TeamRow(teamIndex) is not { } row)
            return [];
        var arenaId = Field(_teams, "arenaid");
        var index = Field(_teams, "index");
        var arena = Number(_teams, row, arenaId);
        return Enumerable.Range(0, _teams.Count).Where(r => r != row && Number(_teams, r, arenaId) == arena).Select(r => Number(_teams, r, index)).ToList();
    }

    /// <summary>Rows of the team's palette, by colour number.</summary>
    public IReadOnlyList<int> PaletteRows(int teamIndex)
    {
        var team = Field(_palette, "team");
        var colourId = Field(_palette, "colorid");
        return Enumerable.Range(0, _palette.Count).Where(r => Number(_palette, r, team) == teamIndex).OrderBy(r => Number(_palette, r, colourId)).ToList();
    }

    /// <summary>Every value of the team, grouped, in display order.</summary>
    public IReadOnlyList<TeamField> FieldsOf(int teamIndex)
    {
        var list = new List<TeamField>();
        if (TeamRow(teamIndex) is not { } row)
            return list;
        foreach (var field in _teams.Fields)
            list.Add(Make(TeamTable.Team, row, field, TeamSpecs.GetValueOrDefault(field.Label) ?? Guess(TeamTable.Team, field.Label)));

        var colour = Field(_palette, "color");
        var colourId = Field(_palette, "colorid");
        var palette = PaletteRows(teamIndex);
        foreach (var paletteRow in palette)
            list.Add(Make(TeamTable.Palette, paletteRow, colour,
                new(PaletteGroup, $"Colour {Number(_palette, paletteRow, colourId) + 1}", paletteRow == palette[0] ? "The team's own colour list. Where the game uses it has not been seen." : null, TeamFieldKind.Colour)));

        if (ArenaRow(teamIndex) is { } arena)
            foreach (var field in _arenas.Fields)
            {
                list.Add(Make(TeamTable.Arena, arena, field, ArenaSpecs.GetValueOrDefault(field.Label) ?? Guess(TeamTable.Arena, field.Label)));
            }

        // Groups in display order; inside one, known fields in the order listed above, then the rest by name.
        var order = Groups.Select((g, i) => (g, i)).ToDictionary(p => p.g, p => p.i);
        int Rank(TeamField f) => f.Table switch
        {
            TeamTable.Palette => f.Row,
            TeamTable.Team => TeamRanks.GetValueOrDefault(f.Field.Label, int.MaxValue),
            _ => ArenaRanks.TryGetValue(f.Field.Label, out var r) ? r : int.MaxValue,
        };
        return list.OrderBy(f => order.GetValueOrDefault(f.Group, order.Count)).ThenBy(f => f.Table == TeamTable.Arena ? 1 : 0)
            .ThenBy(Rank).ThenBy(f => f.Field.Label, StringComparer.Ordinal).ToList();
    }

    private TeamField Make(TeamTable table, int row, TdbField field, Spec spec)
    {
        var value = TableOf(table).Read(row, field);
        var link = spec.Link || field.Label == "index" || field.IsPoolPointer;
        return new TeamField(table, row, field, spec.Group, spec.Label, KindOf(field, spec), spec.Hint, link, value);
    }

    // ---- Writing

    /// <summary>
    /// Changes one value in the working copy and remembers it. Text must fit the field's bytes; numbers must sit in its range.
    /// The row's key (<c>index</c>) cannot be changed.
    /// </summary>
    public TeamField Set(TeamField field, object value)
    {
        Apply(field.Table, field.Row, field.Field, value);
        _edits.RemoveAll(e => e.Table == field.Table && e.Row == field.Row && e.Field == field.Field.Label);
        _edits.Add(new TeamEdit(field.Table, field.Row, field.Field.Label, value));
        return field with { Value = TableOf(field.Table).Read(field.Row, field.Field) };
    }

    private void Apply(TeamTable table, int row, TdbField field, object value)
    {
        if (field.Label == "index")
            throw new InvalidOperationException("The row's key cannot be changed.");
        var source = TableOf(table);
        if (field.IsInlineString)
        {
            var text = value as string ?? throw new ArgumentException($"{field.Label} takes text.");
            source.Write(row, field, text);
            return;
        }
        var number = Convert.ToInt64(value);
        var low = Math.Max(0, field.RangeLow ?? 0);
        var high = field.RangeHigh ?? (1L << field.Depth) - 1;
        if (number < low || number > high)
            throw new ArgumentOutOfRangeException(nameof(value), $"{field.Label} takes {low} to {high:N0}.");
        source.Write(row, field, number);
    }

    /// <summary>
    /// Replays these edits onto another copy of the same database, such as the project's current one after another panel saved
    /// into it, so neither panel's changes are lost.
    /// </summary>
    public static void Replay(TeamDetails target, IEnumerable<TeamEdit> edits)
    {
        foreach (var edit in edits)
        {
            var table = target.TableOf(edit.Table);
            var field = table.Fields.FirstOrDefault(f => f.Label == edit.Field) ?? throw new InvalidDataException($"The {table.Label} table has no {edit.Field} field.");
            if (edit.Row >= table.Count)
                throw new InvalidDataException($"The {table.Label} table has no row {edit.Row}.");
            target.Apply(edit.Table, edit.Row, field, edit.Value);
            target._edits.Add(edit);
        }
    }

    /// <summary>Repairs the checksums and returns the database bytes, or throws if they do not verify.</summary>
    public byte[] Save()
    {
        _file.RepairCrcs();
        if (_file.VerifyCrcs().FirstOrDefault(c => !c.Matches) is { } bad)
            throw new InvalidDataException($"The database checksum {bad.Name} did not verify after repair.");
        return (byte[])_file.Data.Clone();
    }

    /// <summary>Reads a value as typed: RRGGBB for colours, 0 or 1 (or yes/no) for flags, a whole number, or text.</summary>
    public static object Parse(TeamField field, string text) => field.Kind switch
    {
        TeamFieldKind.Text => text,
        TeamFieldKind.GoalHorn => (long)(GoalHorns.Parse(text) ?? throw new FormatException($"{text} is not a goal horn. Use its number or name, e.g. 100 or Wolf.")).Value,
        TeamFieldKind.Colour => GearColours.TryParseHex(text, out var rgb) ? rgb : throw new FormatException($"{text} is not a colour. Use six hex digits, e.g. 0072CE."),
        TeamFieldKind.Flag => text.Trim().ToLowerInvariant() switch
        {
            "1" or "yes" or "on" or "true" => 1,
            "0" or "no" or "off" or "false" => 0,
            _ => throw new FormatException($"{field.Label} is yes or no."),
        },
        _ => long.TryParse(text.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var number)
            ? number : throw new FormatException($"{text} is not a whole number."),
    };

    /// <summary>The value as Parse reads it back.</summary>
    public static string Format(TeamField field) => field.Kind == TeamFieldKind.Colour ? GearColours.Hex(Convert.ToInt32(field.Value)) : field.Value?.ToString() ?? "";

    // ---- Helpers

    private TdbTable TableOf(TeamTable table) => table switch
    {
        TeamTable.Team => _teams,
        TeamTable.Arena => _arenas,
        _ => _palette,
    };

    private static int? RowWithKey(TdbTable table, int key)
    {
        var index = Field(table, "index");
        for (var r = 0; r < table.Count; r++)
            if (Number(table, r, index) == key)
                return r;
        return null;
    }

    private static TdbTable Table(TdbFile file, string name) =>
        file.Tables.FirstOrDefault(t => t.Label == name) ?? throw new InvalidDataException($"The database has no {name} table.");

    private static TdbField Field(TdbTable table, string name) =>
        table.Fields.FirstOrDefault(f => f.Label == name) ?? throw new InvalidDataException($"The {table.Label} table has no {name} field.");

    private static int Number(TdbTable table, int row, TdbField field) => Convert.ToInt32(table.Read(row, field));
}

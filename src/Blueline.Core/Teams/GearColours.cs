using Blueline.Formats.Tdb;

namespace Blueline.Core.Teams;

/// <summary>The two parts of a skater's gear whose colours the team gear table holds per kit.</summary>
public enum GearPart { Gloves = 5, Helmet = 8 }

/// <summary>One row of the team gear table: the colours of one part for one kit, or the fallback for kits with no row of their own.</summary>
public sealed record GearRow(int Row, int Index, int Team, GearPart Part, int Era, int Variant, int Brand, IReadOnlyList<int> Zones)
{
    public int Main => Zones[0];
    public bool IsCurrentEra => Era == GearColours.CurrentEra;
    public bool IsFallback => Variant == 0;
}

/// <summary>One of a team's current (adidas) jerseys, from the jersey table.</summary>
public sealed record GearKit(int JerseyIndex, int Variant)
{
    /// <summary>The gear table's variant for this kit. Jersey 0 is Home and 1 is Away, but gear 1 is Home and 2 is Away;
    /// from 3 on they match. Jersey variant 2 has no gear number, since gear 2 is Away.</summary>
    public int? GearVariant => Variant switch { 0 => 1, 1 => 2, >= 3 => Variant, _ => null };

    public string Label => Variant switch { 0 => "Home", 1 => "Away", _ => $"Alternate (variant {Variant})" };

    public override string ToString() => Label;
}

public enum GearLink { Own, Fallback, None }

/// <summary>Which gear row a kit reads for one part, and why.</summary>
public sealed record GearResolution(GearKit Kit, GearPart Part, GearRow? Row, GearLink Link);

/// <summary>
/// Glove and helmet colours per kit, edited in the stock team gear table of the game database. Seen in game (docs/RESEARCH.md
/// section 6.10): gloves are equipment 5 and the helmet is 8; the gear team is the team's index plus one for NHL clubs; era 3
/// is the current adidas jersey style 0; a kit with no row of its own reads the variant 0 row of its era. A roster save the
/// player has loaded holds its own copy and wins over this table.
/// </summary>
public sealed class GearColours
{
    public const string TableName = "stockteamequipmenttable";
    public const string JerseyTableName = "stockteamjerseys";

    /// <summary>The gear era of the current adidas kits (jersey style 0). How older eras map to older styles is not known.</summary>
    public const int CurrentEra = 3;

    public const int ZoneCount = 15;

    private readonly TdbFile _file;
    private readonly TdbTable _table;
    private readonly TdbTable _jerseys;
    private readonly TdbField _index, _team, _equipment, _era, _variant, _brand;
    private readonly TdbField[] _zones;
    private readonly TdbField _jerseyTeam, _jerseyStyle, _jerseyVariant, _jerseyIndex;

    private GearColours(TdbFile file)
    {
        _file = file;
        _table = Table(file, TableName);
        _jerseys = Table(file, JerseyTableName);
        TdbField F(TdbTable table, string name) => table.Fields.FirstOrDefault(f => f.Label == name)
            ?? throw new InvalidDataException($"The {table.Label} table has no {name} field.");
        (_index, _team, _equipment, _era, _variant, _brand) =
            (F(_table, "index"), F(_table, "team"), F(_table, "equipment"), F(_table, "era"), F(_table, "variant"), F(_table, "brand"));
        _zones = Enumerable.Range(1, ZoneCount).Select(z => F(_table, "zone" + z)).ToArray();
        (_jerseyTeam, _jerseyStyle, _jerseyVariant, _jerseyIndex) =
            (F(_jerseys, "teamid"), F(_jerseys, "style"), F(_jerseys, "variant"), F(_jerseys, "jerseyindex"));
    }

    /// <summary>Reads the tables of a database that already has its field names applied.</summary>
    public static GearColours Open(TdbFile file) => new(file);

    public TdbFile File => _file;

    /// <summary>Rows the table can still take before it would have to grow.</summary>
    public int SpareRows => _table.Allocated - _table.Count;

    private static TdbTable Table(TdbFile file, string name) =>
        file.Tables.FirstOrDefault(t => t.Label == name) ?? throw new InvalidDataException($"The database has no {name} table.");

    /// <summary>The gear table's number for a team, or null where the link is not known (only NHL clubs are checked).</summary>
    public static int? GearTeamOf(TeamInfo team) => team.LeagueId == 0 ? team.Index + 1 : null;

    public IReadOnlyList<GearKit> KitsOf(TeamInfo team)
    {
        var kits = new List<GearKit>();
        for (var r = 0; r < _jerseys.Count; r++)
            if (Number(_jerseys, r, _jerseyTeam) == team.Index && Number(_jerseys, r, _jerseyStyle) == 0)
                kits.Add(new GearKit(Number(_jerseys, r, _jerseyIndex), Number(_jerseys, r, _jerseyVariant)));
        return kits.OrderBy(k => k.Variant).ToList();
    }

    /// <summary>
    /// The jersey-table kit a set of uniform sheets is. Adidas home and away are variants 0 and 1. The alternates ("two",
    /// "three") are taken as the team's other adidas variants in order, which is a guess from naming. Older brands have no
    /// known gear era, so they give null.
    /// </summary>
    public GearKit? KitFor(TeamInfo team, UniformKit kit)
    {
        if (kit.Brand != "adidas")
            return null;
        var kits = KitsOf(team);
        var alternates = kits.Where(k => k.Variant >= 2).ToList();
        return kit.Kit switch
        {
            "home" => kits.FirstOrDefault(k => k.Variant == 0),
            "away" => kits.FirstOrDefault(k => k.Variant == 1),
            "two" => alternates.ElementAtOrDefault(0),
            "three" => alternates.ElementAtOrDefault(1),
            _ => null,
        };
    }

    public IReadOnlyList<GearRow> RowsOf(int gearTeam)
    {
        var rows = new List<GearRow>();
        for (var r = 0; r < _table.Count; r++)
            if (Number(_table, r, _team) == gearTeam && Enum.IsDefined((GearPart)Number(_table, r, _equipment)))
                rows.Add(ReadRow(r));
        return rows.OrderBy(r => r.Part).ThenByDescending(r => r.IsCurrentEra).ThenBy(r => r.Era).ThenBy(r => r.Variant).ToList();
    }

    public GearRow ReadRow(int row) => new(row, Number(_table, row, _index), Number(_table, row, _team), (GearPart)Number(_table, row, _equipment),
        Number(_table, row, _era), Number(_table, row, _variant), Number(_table, row, _brand), _zones.Select(z => Number(_table, row, z)).ToArray());

    public GearResolution Resolve(int gearTeam, GearKit kit, GearPart part)
    {
        var rows = RowsOf(gearTeam).Where(r => r.Part == part && r.IsCurrentEra).ToList();
        if (kit.GearVariant is { } variant && rows.FirstOrDefault(r => r.Variant == variant) is { } own)
            return new GearResolution(kit, part, own, GearLink.Own);
        if (rows.FirstOrDefault(r => r.IsFallback) is { } fallback)
            return new GearResolution(kit, part, fallback, GearLink.Fallback);
        return new GearResolution(kit, part, null, GearLink.None);
    }

    /// <summary>Every current kit that reads this row, so an edit can say what else it changes.</summary>
    public IReadOnlyList<GearKit> KitsUsing(TeamInfo team, GearRow row) =>
        GearTeamOf(team) is { } gearTeam ? KitsOf(team).Where(k => Resolve(gearTeam, k, row.Part).Row?.Row == row.Row).ToList() : [];

    /// <summary>Changes the row's main colour wherever it appears, keeping its accent zones.</summary>
    public GearRow SetMain(GearRow row, int rgb)
    {
        var main = Number(_table, row.Row, _zones[0]);
        foreach (var zone in _zones)
            if (Number(_table, row.Row, zone) == main)
                _table.Write(row.Row, zone, Rgb(rgb));
        return ReadRow(row.Row);
    }

    /// <summary>Sets one zone, 1 to 15.</summary>
    public GearRow SetZone(GearRow row, int zone, int rgb)
    {
        if (zone is < 1 or > ZoneCount)
            throw new ArgumentOutOfRangeException(nameof(zone));
        _table.Write(row.Row, _zones[zone - 1], Rgb(rgb));
        return ReadRow(row.Row);
    }

    /// <summary>A current-era row of this part that no current kit reads, which can be pointed at a kit instead.</summary>
    public GearRow? UnusedRow(TeamInfo team, GearPart part)
    {
        if (GearTeamOf(team) is not { } gearTeam)
            return null;
        var wanted = KitsOf(team).Select(k => k.GearVariant).OfType<int>().ToHashSet();
        return RowsOf(gearTeam).FirstOrDefault(r => r.Part == part && r.IsCurrentEra && !r.IsFallback && !wanted.Contains(r.Variant));
    }

    /// <summary>
    /// Gives a kit that shares the fallback row (or has no row) a row of its own, starting from the colours it shows now.
    /// An unused row of the same part is pointed at the kit when there is one (seen working in game); otherwise a new row is
    /// added in the table's spare space (not yet seen in game). Returns the kit's row and whether it was added.
    /// </summary>
    public (GearRow Row, bool Added) GiveOwnRow(TeamInfo team, GearKit kit, GearPart part)
    {
        if (GearTeamOf(team) is not { } gearTeam)
            throw new InvalidOperationException($"{team.Name} is not linked to the gear table.");
        if (kit.GearVariant is not { } variant)
            throw new InvalidOperationException($"{team.Name} {kit.Label} has no gear number, so it cannot have a row of its own.");
        var current = Resolve(gearTeam, kit, part);
        if (current.Link == GearLink.Own)
            return (current.Row!, false);
        var source = current.Row ?? RowsOf(gearTeam).FirstOrDefault(r => r.Part == part && r.IsCurrentEra)
            ?? RowsOf(gearTeam).FirstOrDefault(r => r.Part == part)
            ?? throw new InvalidOperationException($"{team.Name} has no {part.ToString().ToLowerInvariant()} rows to start from.");

        int row;
        var added = false;
        if (UnusedRow(team, part) is { } unused)
            row = unused.Row;
        else
        {
            row = _table.AddRow(source.Row);
            var next = Enumerable.Range(0, _table.Count - 1).Max(r => Number(_table, r, _index)) + 1;
            _table.Write(row, _index, next);
            added = true;
        }
        _table.Write(row, _era, CurrentEra);
        _table.Write(row, _variant, variant);
        _table.Write(row, _brand, source.Brand);
        for (var z = 0; z < ZoneCount; z++)
            _table.Write(row, _zones[z], source.Zones[z]);
        return (ReadRow(row), added);
    }

    /// <summary>Repairs the checksums and returns the database bytes, or throws if they do not verify.</summary>
    public byte[] Save()
    {
        _file.RepairCrcs();
        if (_file.VerifyCrcs().FirstOrDefault(c => !c.Matches) is { } bad)
            throw new InvalidDataException($"The database checksum {bad.Name} did not verify after repair.");
        return (byte[])_file.Data.Clone();
    }

    public static string Hex(int rgb) => (rgb & 0xFFFFFF).ToString("X6");

    public static bool TryParseHex(string text, out int rgb)
    {
        text = text.Trim().TrimStart('#');
        rgb = 0;
        return text.Length == 6 && int.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out rgb);
    }

    private static long Rgb(int rgb) => rgb & 0xFFFFFF;

    private static int Number(TdbTable table, int row, TdbField field) => Convert.ToInt32(table.Read(row, field));
}

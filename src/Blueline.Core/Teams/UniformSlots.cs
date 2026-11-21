using System.Text;
using System.Text.RegularExpressions;
using Blueline.Formats.Tdb;

namespace Blueline.Core.Teams;

/// <summary>
/// One kit slot of a team: a row of the jersey table. Its files are named <c>&lt;brand&gt;_&lt;slug&gt;_&lt;word&gt;</c>, with the
/// slug taken from the row's own art id, so a slot can wear another art id's kit (docs/UNIFORM_SLOTS.md).
/// </summary>
public sealed record UniformSlot(int Row, int TeamId, int ArtId, int Style, int Variant, int JerseyIndex, int Order,
    bool IsDefault, int LocString, bool IsLight, string? Slug)
{
    public string Brand => UniformSlots.BrandOf(Style);

    public string? Word => UniformSlots.WordOf(Variant);

    /// <summary>The name the kit's files and vault texture packs use, or null when the art id's slug is not known.</summary>
    public string? KitName => Slug != null && Word != null ? $"{Brand}_{Slug}_{Word}" : null;

    public string Label => UniformSlots.LabelOf(LocString);

    public string BrandLabel => UniformSlots.BrandLabelOf(Style);

    public override string ToString() => $"{Label} ({BrandLabel}, {Word ?? Variant.ToString()})";
}

/// <summary>Why a kit name is free for a new slot.</summary>
public enum FreeKitSource { OwnUnused, Unused, HiddenTeam }

/// <summary>A kit name the game's vault knows that no slot reaches, so a new slot can take it.</summary>
public sealed record FreeKit(int Style, string Slug, int ArtId, int Variant, FreeKitSource Source, string? WornBy = null)
{
    public string Brand => UniformSlots.BrandOf(Style);

    public string Word => UniformSlots.WordOf(Variant)!;

    public string Name => $"{Brand}_{Slug}_{Word}";

    public override string ToString() => Source switch
    {
        FreeKitSource.OwnUnused => $"{Name} (the team's own unused kit)",
        FreeKitSource.HiddenTeam => $"{Name} (worn only by {WornBy}, which is hidden)",
        _ => $"{Name} (unused)",
    };
}

/// <summary>
/// The jersey table's kit slots per team: list, relabel, reorder, remove, and add a slot on a free kit name. A new slot
/// gets a jersey row and, for adidas kits of NHL clubs, its own glove and helmet rows. Seen in game 2026-11-20: a row
/// for a kit name the vault knows is listed and worn; a name the vault does not know is listed but its jersey is not
/// drawn, so slots are only added on known names. A loaded roster save holds its own copy of the table and wins.
/// </summary>
public sealed class UniformSlots
{
    public const string TableName = "stockteamjerseys";

    /// <summary>The attribute vault whose string pool lists every kit's texture packs.</summary>
    public const string VaultPath = "nhl/data/common/database/attrib/gccollections.bin";

    /// <summary>jerseyindex, orderid and variant are 5-bit fields.</summary>
    public const int MaxSlotsPerTeam = 32;

    /// <summary>Art id 900 has no team of its own name: its kits are "pre_order" (the menu has jersey_2_900_0..12).</summary>
    public static readonly IReadOnlyDictionary<int, string> KnownArtSlugs = new Dictionary<int, string> { [900] = "pre_order" };

    private static readonly string[] Words = ["home", "away", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve"];
    private static readonly string[] Brands = ["nineties", "rbk", "adidas"];

    /// <summary>The jersey type names the menu shows for <c>locstring</c>.</summary>
    public static readonly IReadOnlyList<string> Labels = ["Home", "Away", "Third", "Retro home", "Retro away", "Retro third", "Alternate", "Type 7", "Top scorer home", "Top scorer away"];

    public static string BrandOf(int style) => style is >= 0 and < 3 ? Brands[style] : $"style{style}";

    public static int? StyleOf(string brand) => Array.IndexOf(Brands, brand.ToLowerInvariant()) is var i and >= 0 ? i : null;

    public static string BrandLabelOf(int style) => style switch { 0 => "Nineties", 1 => "Reebok", 2 => "Adidas", _ => $"Style {style}" };

    public static string? WordOf(int variant) => variant >= 0 && variant < Words.Length ? Words[variant] : null;

    public static int? VariantOf(string word) => Array.IndexOf(Words, word) is var i and >= 0 ? i : null;

    public static string LabelOf(int locString) => locString >= 0 && locString < Labels.Count ? Labels[locString] : $"Type {locString}";

    private readonly TdbTable _table;
    private readonly TdbField _team, _art, _style, _variant, _feVariant, _leadingScorer, _index, _order, _default, _loc, _light;
    private readonly Func<int, string?> _slugOfArt;
    private readonly Func<int, string?> _teamName;
    private readonly HashSet<string> _known;

    private UniformSlots(TdbFile file, Func<int, string?> slugOfArt, IEnumerable<string> knownKits, Func<int, string?> teamName)
    {
        File = file;
        _table = file.Tables.FirstOrDefault(t => t.Label == TableName) ?? throw new InvalidDataException($"The database has no {TableName} table.");
        TdbField F(string name) => _table.Fields.FirstOrDefault(f => f.Label == name) ?? throw new InvalidDataException($"The {TableName} table has no {name} field.");
        (_team, _art, _style, _variant, _feVariant, _leadingScorer) = (F("teamid"), F("teamartid"), F("style"), F("variant"), F("fevariant"), F("variantleadingscorer"));
        (_index, _order, _default, _loc, _light) = (F("jerseyindex"), F("orderid"), F("isdefault"), F("locstring"), F("islightcolor"));
        _slugOfArt = art => KnownArtSlugs.TryGetValue(art, out var known) ? known : slugOfArt(art);
        _teamName = teamName;
        _known = [.. knownKits];
    }

    /// <summary>
    /// Opens the jersey table of a database that has its field names applied. <paramref name="slugOfArt"/> gives the file
    /// slug for an art id (from the team catalog); <paramref name="knownKits"/> are the vault's kit names (<see cref="KnownKits"/>).
    /// </summary>
    public static UniformSlots Open(TdbFile file, Func<int, string?> slugOfArt, IEnumerable<string> knownKits, Func<int, string?>? teamName = null) =>
        new(file, slugOfArt, knownKits, teamName ?? (_ => null));

    /// <summary>Opens the slots with the catalog's slugs and the vault's kit names.</summary>
    public static UniformSlots Open(TdbFile file, TeamCatalog catalog, byte[]? vault)
    {
        var slugs = new Dictionary<int, string>();
        foreach (var team in catalog.Teams)
            if (team.Slug != null)
                slugs.TryAdd(team.ArtId, team.Slug);
        return Open(file, art => slugs.GetValueOrDefault(art), vault == null ? [] : KnownKits(vault), index => catalog.Teams.FirstOrDefault(t => t.Index == index)?.Name);
    }

    public TdbFile File { get; }

    public int SpareRows => _table.Allocated - _table.Count;

    private static readonly Regex Pack = new(@"jersey_(?<kit>(?:adidas|rbk|nineties)_[a-z0-9_]+)_texpack", RegexOptions.Compiled);

    /// <summary>Every jersey kit name with a texture pack in the vault (<c>adidas_toronto_five</c> ...).</summary>
    public static IReadOnlySet<string> KnownKits(byte[] vault)
    {
        var text = Encoding.Latin1.GetString(vault);
        return Pack.Matches(text).Select(m => m.Groups["kit"].Value).ToHashSet(StringComparer.Ordinal);
    }

    private int N(int row, TdbField field) => Convert.ToInt32(_table.Read(row, field));

    private UniformSlot Read(int row)
    {
        var art = N(row, _art);
        return new UniformSlot(row, N(row, _team), art, N(row, _style), N(row, _variant), N(row, _index), N(row, _order),
            N(row, _default) != 0, N(row, _loc), N(row, _light) != 0, _slugOfArt(art));
    }

    private IEnumerable<int> RowsOf(int team) => Enumerable.Range(0, _table.Count).Where(r => N(r, _team) == team);

    /// <summary>The team's slots in jersey menu order.</summary>
    public IReadOnlyList<UniformSlot> SlotsOf(TeamInfo team) => SlotsOf(team.Index);

    public IReadOnlyList<UniformSlot> SlotsOf(int team) => [.. RowsOf(team).Select(Read).OrderBy(s => s.Order).ThenBy(s => s.JerseyIndex)];

    /// <summary>True when the vault has texture packs for the slot's kit, so the game can draw it.</summary>
    public bool IsDrawable(UniformSlot slot) => slot.KitName is { } name && _known.Contains(name);

    private void Renumber(int team, IReadOnlyList<int> rowsInOrder)
    {
        for (var i = 0; i < rowsInOrder.Count; i++)
            _table.Write(rowsInOrder[i], _order, i);
    }

    /// <summary>Moves a slot to a menu position (0 first); the others keep their order around it.</summary>
    public UniformSlot Move(UniformSlot slot, int position)
    {
        var rows = SlotsOf(slot.TeamId).Select(s => s.Row).Where(r => r != slot.Row).ToList();
        rows.Insert(Math.Clamp(position, 0, rows.Count), slot.Row);
        Renumber(slot.TeamId, rows);
        return Read(slot.Row);
    }

    /// <summary>Changes the menu label (jersey type), light flag and Home/Away default of a slot.</summary>
    public UniformSlot Set(UniformSlot slot, int? locString = null, bool? light = null, bool? isDefault = null)
    {
        if (locString is { } loc)
            _table.Write(slot.Row, _loc, loc);
        if (light is { } l)
            _table.Write(slot.Row, _light, l ? 1 : 0);
        if (isDefault is { } d)
            _table.Write(slot.Row, _default, d ? 1 : 0);
        return Read(slot.Row);
    }

    /// <summary>
    /// Removes a slot. The team's other kits close the gap in their jersey numbers and menu order. Not yet seen in game.
    /// </summary>
    public void Remove(UniformSlot slot)
    {
        var team = slot.TeamId;
        _table.RemoveRow(slot.Row);
        foreach (var row in RowsOf(team).Where(r => N(r, _index) > slot.JerseyIndex).ToList())
            _table.Write(row, _index, N(row, _index) - 1);
        Renumber(team, [.. SlotsOf(team).Select(s => s.Row)]);
    }

    /// <summary>Kit names every row reaches, with the teams that list them.</summary>
    private Dictionary<string, HashSet<int>> Users()
    {
        var users = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        for (var r = 0; r < _table.Count; r++)
            if (Read(r).KitName is { } name)
                (users.TryGetValue(name, out var set) ? set : users[name] = []).Add(N(r, _team));
        return users;
    }

    /// <summary>The Hockey Ultimate Team side wears the "pre_order" kits, and Puck Peak hides HUT, so its kits count as free.</summary>
    private bool IsHiddenTeam(int team) => _teamName(team) is "Hockey Ultimate Team";

    /// <summary>
    /// Kit names a new slot can take, best first: the team's own kits that have art and packs but no slot, then names no
    /// slot reaches, then names only a hidden side wears. Only names whose art id is known are offered, so the slot can
    /// point at them. Each name can be taken once.
    /// </summary>
    public IReadOnlyList<FreeKit> FreeKits(TeamInfo team, int? style = null)
    {
        var users = Users();
        var arts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var art in Enumerable.Range(0, _table.Count).Select(r => N(r, _art)).Append(team.ArtId).Distinct())
            if (_slugOfArt(art) is { } slug)
                arts.TryAdd(slug, art);
        foreach (var (art, slug) in KnownArtSlugs)
            arts[slug] = art;
        var ownSlug = _slugOfArt(team.ArtId);
        var free = new List<FreeKit>();
        foreach (var name in _known.Order(StringComparer.Ordinal))
        {
            var brand = name[..name.IndexOf('_')];
            var rest = name[(brand.Length + 1)..];
            var cut = rest.LastIndexOf('_');
            if (cut < 0 || StyleOf(brand) is not { } s || (style != null && s != style) || VariantOf(rest[(cut + 1)..]) is not { } variant)
                continue;
            var slug = rest[..cut];
            if (!arts.TryGetValue(slug, out var art))
                continue;
            if (users.TryGetValue(name, out var wearers))
            {
                if (wearers.All(IsHiddenTeam) && !wearers.Contains(team.Index))
                    free.Add(new FreeKit(s, slug, art, variant, FreeKitSource.HiddenTeam, _teamName(wearers.First())));
                continue;
            }
            free.Add(new FreeKit(s, slug, art, variant, slug == ownSlug ? FreeKitSource.OwnUnused : FreeKitSource.Unused));
        }
        return [.. free.OrderBy(f => f.Source).ThenByDescending(f => f.Style).ThenBy(f => f.Slug == "pre_order" ? 0 : 1).ThenBy(f => f.Slug).ThenBy(f => f.Variant)];
    }

    /// <summary>
    /// Adds a slot for a free kit name at a menu position (default: after Home and Away). Its other fields are copied from
    /// the team's slot of the same brand (its Home when there is one). For adidas kits of NHL clubs, the gear table gets
    /// glove and helmet rows for it, copied from the rows it read before (the team's fallback rows). Returns the new slot.
    /// </summary>
    public UniformSlot Add(TeamInfo team, FreeKit kit, int locString = 6, int? position = null, GearColours? gear = null)
    {
        var slots = SlotsOf(team);
        if (slots.Count >= MaxSlotsPerTeam)
            throw new InvalidOperationException($"{team.Name} already has {MaxSlotsPerTeam} slots, the most the jersey table can number.");
        if (SpareRows == 0)
            throw new InvalidOperationException($"The {TableName} table has no spare rows.");
        if (slots.Any(s => s.Style == kit.Style && s.Variant == kit.Variant))
            throw new InvalidOperationException($"{team.Name} already has a {BrandLabelOf(kit.Style)} slot with variant {kit.Variant} ({WordOf(kit.Variant)}); a team's slots must differ in brand or variant.");
        if (Users().TryGetValue(kit.Name, out var wearers) && !wearers.All(IsHiddenTeam))
            throw new InvalidOperationException($"{kit.Name} is already worn by another slot.");

        var template = slots.FirstOrDefault(s => s.Style == kit.Style && s.Variant == 0) ?? slots.FirstOrDefault(s => s.Style == kit.Style)
            ?? slots.FirstOrDefault() ?? throw new InvalidOperationException($"{team.Name} has no slots to copy from.");
        var row = _table.AddRow(template.Row);
        _table.Write(row, _team, team.Index);
        _table.Write(row, _art, kit.ArtId);
        _table.Write(row, _style, kit.Style);
        foreach (var field in new[] { _variant, _feVariant, _leadingScorer })
            _table.Write(row, field, kit.Variant);
        _table.Write(row, _index, slots.Select(s => s.JerseyIndex).DefaultIfEmpty(-1).Max() + 1);
        _table.Write(row, _default, 0);
        _table.Write(row, _loc, locString);
        var added = Move(Read(row), position ?? Math.Min(2, slots.Count));

        if (gear != null && kit.Style == GearColours.AdidasStyle && GearColours.GearTeamOf(team) != null
            && gear.KitsOf(team).FirstOrDefault(k => k.Variant == kit.Variant) is { } gearKit)
            foreach (var part in Enum.GetValues<GearPart>())
                gear.GiveOwnRow(team, gearKit, part);
        return added;
    }

    /// <summary>Repairs the checksums and returns the database bytes.</summary>
    public byte[] Save()
    {
        File.RepairCrcs();
        if (File.VerifyCrcs().FirstOrDefault(c => !c.Matches) is { } bad)
            throw new InvalidDataException($"The database checksum {bad.Name} did not verify after repair.");
        return (byte[])File.Data.Clone();
    }
}

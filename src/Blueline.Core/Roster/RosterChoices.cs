using System.Text;
using Blueline.Formats.Tdb;

namespace Blueline.Core.Roster;

/// <summary>One value a field can take and its name, as the game's create-a-player menus show it.</summary>
public sealed record RosterChoice(long Value, string Name);

/// <summary>
/// The named values of the roster fields that take a fixed set: some written out here from the game's create_player.json
/// Choices, the rest read from creation_zone_csv files (names resolved through the loc dictionary when it is given).
/// See docs/ROSTER_EDITOR.md.
/// </summary>
public sealed class RosterChoices
{
    public const string CsvFolder = "nhl/data/common/gamemodes/creation_zone_csv/";

    private readonly Dictionary<string, IReadOnlyList<RosterChoice>> _sets = new(StringComparer.Ordinal);

    public const string Position = "position", SkaterStyle = "skaterstyle", GoalieStyle = "goaliestyle", Hand = "hand", Fighting = "fighting",
        Captain = "captain", ContractType = "contracttype", Origin = "origin", Trait = "trait", GoalieTrait = "goalietrait", Celebration = "celebration",
        SkatingStyle = "skatingstyle", Stance = "stance", HairColour = "haircolour", HairStyle = "hairstyle", EyeColour = "eyecolour",
        Helmet = "helmet", Gloves = "gloves", Stick = "stick", Skates = "skates", Visor = "visor", VisorTint = "visortint", Team = "team",
        PlayByPlay = "playbyplay", ShoulderPads = "shoulderpads", JerseyTuck = "jerseytuck", Pants = "pants";

    public RosterChoices()
    {
        Put(Position, [(0, "Centre"), (1, "Left wing"), (2, "Right wing"), (3, "Defence"), (4, "Goalie")]);
        Put(SkaterStyle, [(1, "Defensive defenceman"), (2, "Offensive defenceman"), (3, "Enforcer defenceman"), (4, "Two-way defenceman"), (5, "Grinder"),
            (6, "Playmaker"), (7, "Sniper"), (8, "Power forward"), (9, "Two-way forward"), (10, "Enforcer")]);
        Put(GoalieStyle, [(0, "Stand-up"), (1, "Hybrid"), (2, "Butterfly")]);
        Put(Hand, [(0, "Left"), (1, "Right")]);
        Put(Fighting, [(0, "Never"), (1, "Rarely"), (2, "Sometimes"), (3, "Often")]);
        Put(Captain, [(0, "None"), (1, "Captain"), (2, "Alternate")]);
        Put(ContractType, [(0, "One-way"), (1, "Two-way")]);
    }

    public IReadOnlyList<RosterChoice>? Get(string? name) => name != null && _sets.TryGetValue(name, out var set) ? set : null;

    public string? NameOf(string? set, long value) => Get(set)?.FirstOrDefault(c => c.Value == value)?.Name;

    public void Put(string name, IEnumerable<(long Value, string Name)> values) =>
        _sets[name] = values.Select(v => new RosterChoice(v.Value, v.Name)).ToList();

    /// <summary>Reads the CSV-backed lists from the game. <paramref name="read"/> returns a game file's bytes or null.</summary>
    public static RosterChoices Load(Func<string, byte[]?> read, LocDictionary? loc = null, IEnumerable<(long Value, string Name)>? teams = null)
    {
        var choices = new RosterChoices();
        string Name(string text)
        {
            if (!text.StartsWith('$'))
                return text;
            var key = text[1..];
            if (loc != null && loc.TryGet(key, out var row) && !string.IsNullOrWhiteSpace(row.Text))
                return row.Text!;
            // $bauernexus2n, $TXT_VISOR_AVIATOR: readable enough without the dictionary.
            key = key.StartsWith("TXT_", StringComparison.Ordinal) ? key[4..] : key;
            return string.Join(' ', key.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(w => w.Length > 1 ? char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant() : w));
        }
        void FromCsv(string set, string file, string valueColumn, string nameColumn, Func<Dictionary<string, string>, string>? label = null)
        {
            if (read(CsvFolder + file) is not { } bytes)
                return;
            var rows = CsvText.Parse(Encoding.UTF8.GetString(bytes).TrimStart('﻿'));
            if (rows.Count < 2)
                return;
            var header = rows[0].Select(h => h.Trim()).ToList();
            var values = new List<(long, string)>();
            var seen = new HashSet<long>();
            foreach (var cells in rows.Skip(1))
            {
                var map = header.Select((h, i) => (h, v: i < cells.Count ? cells[i].Trim() : "")).ToDictionary(p => p.h, p => p.v, StringComparer.OrdinalIgnoreCase);
                if (!map.TryGetValue(valueColumn, out var raw) || !long.TryParse(raw, out var value) || !seen.Add(value))
                    continue;
                var name = label?.Invoke(map) ?? Name(map.GetValueOrDefault(nameColumn, raw));
                values.Add((value, name));
            }
            if (values.Count > 0)
                choices.Put(set, values);
        }

        FromCsv(Origin, "origins.csv", "index", "name");
        FromCsv(Celebration, "gamecelebrations.csv", "animationid", "name");
        FromCsv(SkatingStyle, "skaterskatingstyles.csv", "animationid", "name", m => $"Style {long.Parse(m["animationid"]) + 1}");
        FromCsv(Stance, "goaliestances.csv", "animationid", "name", m => $"Stance {long.Parse(m["animationid"]) + 1}");
        FromCsv(HairColour, "appearancehaircolors.csv", "assetid", "name");
        FromCsv(HairStyle, "appearancehairstyles.csv", "assetid", "name", m => $"Hair style {m.GetValueOrDefault("nameindex", m["assetid"])}");
        FromCsv(EyeColour, "appearanceeyecolors.csv", "assetid", "colorname");
        FromCsv(Helmet, "skaterhelmetstyle.csv", "assetid", "itemname");
        FromCsv(Gloves, "skaterglovestyle.csv", "assetid", "itemname");
        FromCsv(Stick, "stickdisplayinfo.csv", "stickassetid", "stickname");
        FromCsv(Skates, "skatedisplayinfo.csv", "skateassetid", "skatename");
        FromCsv(Visor, "skatervisortypes.csv", "assetid", "itemname");
        FromCsv(VisorTint, "skatervisortint.csv", "assetid", "name");
        // Shoulder pads are left as numbers: the CSV's asset and render ids disagree and which one the field holds is unknown.
        FromCsv(JerseyTuck, "skaterjerseytuck.csv", "assetid", "name");
        FromCsv(Pants, "playerpantlength.csv", "assetid", "name");

        // Traits: one id per row of playertraits.csv (several rows per trait, one per archetype).
        if (read(CsvFolder + "playertraits.csv") is { } traits)
        {
            var rows = CsvText.Parse(Encoding.UTF8.GetString(traits).TrimStart('﻿'));
            var header = rows[0];
            int id = header.IndexOf("traitid"), name = header.IndexOf("trait");
            var list = new List<(long, string)> { (0, "None") };
            if (id >= 0 && name >= 0)
                foreach (var cells in rows.Skip(1).Where(c => c.Count > Math.Max(id, name)))
                    if (long.TryParse(cells[id], out var v) && list.All(l => l.Item1 != v))
                        list.Add((v, Title(cells[name])));
            choices.Put(Trait, list.OrderBy(l => l.Item1));
        }
        if (teams != null)
            choices.Put(Team, teams);
        return choices;
    }

    /// <summary>"BIG SHOOTER" as "Big shooter".</summary>
    private static string Title(string upper)
    {
        var lower = upper.Trim().ToLowerInvariant();
        return lower.Length == 0 ? lower : char.ToUpperInvariant(lower[0]) + lower[1..];
    }
}

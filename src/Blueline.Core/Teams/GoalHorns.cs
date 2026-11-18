using System.Text.RegularExpressions;

namespace Blueline.Core.Teams;

public sealed record GoalHorn(int Value, string Name, string Kind)
{
    public override string ToString() => Value == 0 ? Name : $"{Value}. {Name}";
}

/// <summary>
/// The goal horns NHL 19 knows. An arena's <c>goalhorn</c> field (exhibitionarena, and the roster save's copy, which
/// wins) holds the horn's place in the GoalHornID enum of <c>goalhornpatch.ptc</c> plus one; 0 is no horn. Every
/// stock AHL arena holds 41, the Generic horn. The audio of all of them is one bank, <see cref="BankPath"/>.
/// Inferred from the stock values (ANA 1 ... VGK 31, AHL 41) and the enum's order; see docs/RESEARCH.md section 6.1.
/// </summary>
public static partial class GoalHorns
{
    public const string BankPath = "nhl/data/kettle/audio/sfx/hornbank.sbr";
    public const string Field = "goalhorn";
    public const int Generic = 41;

    /// <summary>The GoalHornID enum in order, as the patch spells it.</summary>
    private static readonly string[] EnumNames =
    [
        "Anaheim", "Winnipeg", "Boston", "Buffalo", "Calgary", "Carolina", "Chicago", "Colorado", "Columbus", "Dallas",
        "Detroit", "Edmonton", "Florida", "Los Angeles", "Minnesota", "Montreal", "Nashville", "New Jersey", "NY Islanders",
        "NY Rangers", "Ottawa", "Philadelphia", "Phoenix", "Pittsburgh", "St. Louis", "San Jose", "Tampa Bay", "Toronto",
        "Vancouver", "Washington", "Vegas",
        "Future_NHL_team_01", "Future_NHL_team_02", "Future_NHL_team_03", "Future_NHL_team_04", "Future_NHL_team_05",
        "Future_NHL_team_06", "Future_NHL_team_07", "Future_NHL_team_08", "Future_NHL_team_09",
        "Generic",
        "AirHorn1", "AirHorn2", "AirRaid", "Ambulance", "AngryCat", "Bear", "Bus", "Car1", "Car2", "Car3", "Car4", "Car5",
        "Car6", "Car7", "Car8", "Dog", "Elephant", "Firetruck1", "Firetruck2", "Guitar", "Hawk", "Nightingale", "SqueakyToy",
        "Klaxon", "PartyHorn1", "PartyHorn2", "Kazoo", "BulbHorn", "ComicBuzzer", "Ducks", "SlideWhistle", "Ship1", "Ship2",
        "Siren1", "Siren2", "SteamWhistle", "Theremin", "Tiger", "Timpani", "Train1", "Train2", "Train3", "Train4", "Train5",
        "Train6", "Trombone1", "Trombone2", "Truck1", "Truck2", "Truck3", "Truck4", "Truck5", "Truck6", "Truck7", "Truck8",
        "Truck9", "UFO", "Vibraslap", "Wolf", "NHL94", "SUBMARINE_KLAXON", "BIKE_HORNS", "FIRETRUCK_SIREN",
        "TRAIN_HORN_TIBET_HORN", "GONG_CREATURE_ROAR", "NFS_SIREN_02", "NFS_SIREN_05", "NFS_SIREN_06", "CRUISE_SHIP_HORN",
        "FOG_HORN_01", "FOG_HORN_02", "TRUCK_HORN_WOBBLY", "AMBULANCE_SIREN_THUNDER", "SEMI_TRUCK_HORN_MONKEYS",
        "SHIP_HORN_BLASTING", "BUGLE_FANFARE", "TRAIN_HORN_TWEETS_AND_LONG", "FIREWORKS", "HORN_LEOPARD", "HORNS_GORILLA",
        "HORN_SQUEEKY_TOY", "SPACESHIP_HORN_ALARM", "HORN_LASER_SWORD", "TRUCK_HORN_MONSTER_TRUCKING", "HORN_JET_FLYBY",
    ];

    public static readonly IReadOnlyList<GoalHorn> All = Build();

    /// <summary>
    /// The hornbank.sbr sample each novelty horn (42 to 126) plays, in horn order. The bank's own list of sounds (the
    /// DSET's id and offset tables) runs in GoalHornID order from its 32nd entry, one entry per novelty horn; this is
    /// that list resolved to sample numbers. Checked in game with the spoken test bank: Car 1 (49) is 63, Car 5 (53) is 59.
    /// </summary>
    private static readonly int[] NoveltySamples =
    [
        67, 39, 21, 35, 69, 24, 38, 63, 62, 61, 60, 59, 58, 57, 56, 14, 22, 40, 41, 15, 12, 11, 29, 31, 64, 65, 28, 27, 26,
        32, 34, 37, 36, 30, 33, 25, 19, 23, 10, 47, 46, 45, 44, 43, 42, 17, 16, 55, 54, 53, 52, 51, 50, 70, 49, 48, 18, 20,
        13, 66, 73, 74, 75, 76, 77, 78, 79, 80, 81, 82, 83, 84, 85, 86, 87, 88, 89, 90, 91, 92, 93, 94, 95, 96, 97,
    ];

    /// <summary>
    /// NHL clubs (1 to 31) and Generic (41), read the same way from the list's first 32 entries. Not heard in game:
    /// the clubs may layer more than one sound, and Generic sitting at entry 32 is a guess.
    /// </summary>
    private static readonly Dictionary<long, int> LikelySamples = new()
    {
        [1] = 383, [2] = 396, [3] = 0, [4] = 3, [5] = 68, [6] = 397, [7] = 385, [8] = 386, [9] = 387, [10] = 72,
        [11] = 4, [12] = 71, [13] = 394, [14] = 5, [15] = 388, [16] = 395, [17] = 6, [18] = 403, [19] = 389, [20] = 7,
        [21] = 9, [22] = 8, [23] = 384, [24] = 1, [25] = 390, [26] = 398, [27] = 391, [28] = 2, [29] = 399, [30] = 393,
        [31] = 392, [Generic] = 404,
    };

    /// <summary>The hornbank.sbr sample horn <paramref name="horn"/> plays, where it is known or likely.</summary>
    public static int? SampleOf(long horn) =>
        horn is >= Generic + 1 and <= Generic + 85 ? NoveltySamples[horn - Generic - 1]
        : LikelySamples.TryGetValue(horn, out var sample) ? sample : null;

    /// <summary>True for the novelty horns, whose samples follow the pattern checked in game.</summary>
    public static bool IsSampleConfirmed(long horn) => horn is >= Generic + 1 and <= Generic + 85;

    public static GoalHorn? Find(long value) => value >= 0 && value < All.Count ? All[(int)value] : null;

    public static string Describe(long value) => Find(value)?.ToString() ?? $"{value}. (not a known horn)";

    /// <summary>A horn by number or by name, as typed on the command line.</summary>
    public static GoalHorn? Parse(string text)
    {
        if (int.TryParse(text, out var value))
            return Find(value);
        var key = Squash(text);
        return All.FirstOrDefault(h => Squash(h.Name) == key);
    }

    private static List<GoalHorn> Build()
    {
        var list = new List<GoalHorn> { new(0, "No horn", "None") };
        for (var i = 0; i < EnumNames.Length; i++)
        {
            var kind = i switch { < 31 => "NHL club", < 40 => "Spare club slot", 40 => "Generic", _ => "Novelty" };
            var name = EnumNames[i];
            name = name.StartsWith("Future_NHL_team_", StringComparison.Ordinal) ? $"Spare club slot {name[^2..]}"
                : i > 40 ? Tidy(name) : name;
            list.Add(new GoalHorn(i + 1, name, kind));
        }
        return list;
    }

    private static string Tidy(string name)
    {
        if (name.Contains('_'))
            name = string.Join(' ', name.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(w => w.Length <= 3 && w.All(char.IsUpper) && w is "NFS" or "UFO" ? w : char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));
        else
            name = SplitWords().Replace(name, " $1");
        return name.Replace("Squeeky", "Squeaky", StringComparison.Ordinal);
    }

    private static string Squash(string text) => new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    [GeneratedRegex("(?<=[a-z])([A-Z0-9])")]
    private static partial Regex SplitWords();
}

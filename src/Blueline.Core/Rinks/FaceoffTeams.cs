using Blueline.Core.Teams;

namespace Blueline.Core.Rinks;

/// <summary>
/// The names TheFaceoff.net titles a team's rinks with ("Toronto Maple Leafs 2027"). Mods rename teams but keep art
/// ids, so the table's current name comes first, then fixed names for clubs whose slot was reused or renamed.
/// </summary>
public static class FaceoffTeams
{
    private static readonly Dictionary<int, string[]> ByArtId = new()
    {
        [22] = ["Utah Mammoth", "Utah Hockey Club", "Arizona Coyotes"], // the Arizona slot
        [10499] = ["Seattle Kraken"],
        [15] = ["Montreal Canadiens", "Montréal Canadiens"],
        [24] = ["St. Louis Blues", "St Louis Blues"],
        [32] = ["Vegas Golden Knights"],
        [18] = ["New York Islanders"],
        [19] = ["New York Rangers"],
    };

    // Clubs whose TheFaceoff.net title differs from the game's name, in any league.
    private static readonly Dictionary<string, string[]> ByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Worcester Railers"] = ["Worcester Railers HC"],
    };

    public static IReadOnlyList<string> NamesFor(TeamInfo team)
    {
        var names = new List<string>();
        void Add(string? name)
        {
            if (!string.IsNullOrWhiteSpace(name) && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
                names.Add(name.Trim());
        }

        Add(team.Name);
        if (!string.IsNullOrWhiteSpace(team.City) && !team.City.Contains('$') && !team.Name.StartsWith(team.City, StringComparison.OrdinalIgnoreCase))
            Add($"{team.City} {team.Name}");
        if (team.LeagueId == 0 && ByArtId.TryGetValue(team.ArtId, out var fixedNames))
            foreach (var name in fixedNames)
                Add(name);
        if (ByName.TryGetValue(team.Name, out var aliases))
            foreach (var name in aliases)
                Add(name);
        return names;
    }

    /// <summary>TheFaceoff.net's list name for a game league ("ice-nhl", "ice-ahl", ...), or null when it has none.</summary>
    public static string? LeagueFor(TeamInfo team) => team.League.ToUpperInvariant() switch
    {
        "NHL" => "nhl",
        "AHL" => "ahl",
        "ECHL" => "echl",
        "CHL" or "OHL" or "QMJHL" or "WHL" => "chl",
        _ => null,
    };
}

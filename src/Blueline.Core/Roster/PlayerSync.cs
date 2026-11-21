using Blueline.Core.Teams;
using Blueline.Formats.Tdb;

namespace Blueline.Core.Roster;

/// <summary>
/// Plans the player tables' part of a roster sync. The database and a roster save hold the same players (same game_id) at
/// different rows, and their membership indexes and roster keys do not always agree (docs/ROSTER_EDITOR.md section 1). So a
/// changed row is found in the save by what it is: the player's game_id for bio, ratings and gear, and game_id plus the team the
/// game's database has the membership on for a roster row. A moved membership is found under its old team and its team cell
/// is changed. Rows the project added are not copied yet.
/// </summary>
public static class PlayerSync
{
    /// <summary>Tables keyed by game_id: one row per player.</summary>
    public static readonly IReadOnlyList<string> PlayerTables =
    [
        RosterBook.BioTable, RosterBook.SkaterAiTable, RosterBook.GoalieAiTable, RosterBook.SkaterGearTable, RosterBook.GoalieGearTable,
        "exhibitionresortskaterextras", "exhibitionresortgoalieextras", "exhibitionresortplayerextras",
    ];

    /// <summary>Every table this sync handles, so the generic sync leaves them alone.</summary>
    public static readonly IReadOnlySet<string> Tables = new HashSet<string>(PlayerTables.Append(RosterBook.RosterTable).Append(RosterBook.MembershipTable), StringComparer.Ordinal);

    public static void Plan(TdbFile game, TdbFile project, TdbFile roster, List<RosterCellChange> changes, List<string> skipped)
    {
        foreach (var name in PlayerTables)
        {
            if (Tables3(game, project, roster, name) is not var (g, p, r))
                continue;
            var rows = ChangedRows(game, g, project, p, name, skipped);
            if (rows.Count == 0)
                continue;
            var key = Key(p, "game_id");
            if (key == null)
            {
                skipped.Add($"{name}: no game_id field, so its {rows.Count} changed row(s) were not copied.");
                continue;
            }
            var rosterRows = Unique(r, Key(r, "game_id")!);
            var missing = 0;
            foreach (var row in rows)
            {
                var gid = RosterBook.Raw(p, row, key);
                if (RosterBook.Raw(g, row, Key(g, "game_id")!) != gid)
                {
                    skipped.Add($"{name}: row {row} changed its game_id, so it was not copied.");
                    continue;
                }
                if (!rosterRows.TryGetValue(gid, out var target))
                {
                    missing++;
                    continue;
                }
                AddCells(name, gid, g, row, p, row, r, target, [], changes);
            }
            if (missing > 0)
                skipped.Add($"{name}: {missing} changed player(s) are not in the roster save, so their edits stay in the database only.");
        }

        PlanRoster(game, project, roster, changes, skipped);

        if (Tables3(game, project, roster, RosterBook.MembershipTable) is var (mg, mp, _) && ChangedRows(game, mg, project, mp, RosterBook.MembershipTable, skipped).Count > 0)
            skipped.Add($"{RosterBook.MembershipTable}: the project changed membership rows, which the sync does not copy.");
    }

    private static void PlanRoster(TdbFile game, TdbFile project, TdbFile roster, List<RosterCellChange> changes, List<string> skipped)
    {
        if (Tables3(game, project, roster, RosterBook.RosterTable) is not var (g, p, r))
            return;
        var rows = ChangedRows(game, g, project, p, RosterBook.RosterTable, skipped);
        if (rows.Count == 0)
            return;
        var gameOwner = Owners(game);
        var rosterIndexes = IndexesByPlayer(roster);
        var team = Key(r, "team")!;
        var playerIndex = Key(r, "playerindex")!;
        var rosterRowsByIndex = Enumerable.Range(0, r.Count).ToLookup(row => RosterBook.Raw(r, row, playerIndex));
        // Line slots are not copied cell by cell: the save keeps its own lines, so a slot is given to its holder and taken from
        // everyone else on that team in the save (PlanLines, below).
        var slots = r.Fields.Where(f => f.Depth == 1 && f.Label != "rosterstatus").Select(f => f.Label).ToHashSet(StringComparer.Ordinal);
        string[] links = ["key", "playerindex", .. slots];
        var moved = new List<(long Gid, int Team, int SaveRow)>();
        var cells = new Dictionary<(int, string), RosterCellChange>();
        foreach (var row in rows)
        {
            var index = RosterBook.Raw(g, row, Key(g, "playerindex")!);
            var baseTeam = RosterBook.Raw(g, row, Key(g, "team")!);
            if (RosterBook.Raw(p, row, Key(p, "playerindex")!) != index)
            {
                skipped.Add($"{RosterBook.RosterTable}: row {row} was pointed at another player, which the sync does not copy.");
                continue;
            }
            if (!gameOwner.TryGetValue(index, out var gid))
            {
                skipped.Add($"{RosterBook.RosterTable}: row {row} belongs to no player in the game's database; left alone.");
                continue;
            }
            var candidates = (rosterIndexes.GetValueOrDefault(gid) ?? []).SelectMany(i => rosterRowsByIndex[i]).Where(rr => RosterBook.Raw(r, rr, team) == baseTeam).ToList();
            if (candidates.Count != 1)
            {
                skipped.Add($"{RosterBook.RosterTable}: player {gid} has {(candidates.Count == 0 ? "no" : "more than one")} roster row on team {baseTeam} in the save, so that row's changes were not copied.");
                continue;
            }
            AddCells(RosterBook.RosterTable, gid, g, row, p, row, r, candidates[0], links, changes);
            var toTeam = RosterBook.Raw(p, row, Key(p, "team")!);
            if (toTeam != baseTeam)
                moved.Add((gid, (int)toTeam, candidates[0]));
        }
        PlanLines(game, project, roster, r, slots, moved, cells, skipped);
        changes.AddRange(cells.Values);
    }

    /// <summary>
    /// A moved player loses every slot and the captaincy the save gave him on his old team. Then, on each team whose lines the
    /// project changed, each changed slot goes to the project's holder and is cleared from every other player the save has there.
    /// </summary>
    private static void PlanLines(TdbFile game, TdbFile project, TdbFile roster, TdbTable r, IReadOnlySet<string> slots,
        List<(long Gid, int Team, int SaveRow)> moved, Dictionary<(int, string), RosterCellChange> cells, List<string> skipped)
    {
        void Put(long gid, int row, string label, long value)
        {
            var field = Key(r, label)!;
            var now = r.Read(row, field);
            object want = value;
            if (now is not null && now.GetType() != typeof(long))
                want = Convert.ChangeType(value, now.GetType());
            cells[(row, label)] = new RosterCellChange(RosterBook.RosterTable, gid, row, field, now, want, now);
        }
        foreach (var (gid, _, row) in moved)
            foreach (var label in slots.Append("captain"))
                if (Key(r, label) != null && RosterBook.Raw(r, row, Key(r, label)!) != 0)
                    Put(gid, row, label, 0);

        var gameBook = RosterBook.Open(game);
        var projectBook = RosterBook.Open(project);
        var saveBook = RosterBook.Open(roster);
        var teams = moved.Select(m => m.Team).Concat(gameBook.Teams.Keys).Distinct();
        foreach (var team in teams)
        {
            var before = gameBook.Lines(team);
            var after = projectBook.Lines(team);
            var changed = slots.Where(s => before.GetValueOrDefault(s) != after.GetValueOrDefault(s)).ToList();
            if (changed.Count == 0)
                continue;
            // Who the save has on this team once the moves are in.
            var onTeam = new Dictionary<long, int>();
            foreach (var gid in saveBook.PlayersOn(team))
                onTeam[gid] = saveBook.MembershipOn(gid, team)!.RosterRow;
            foreach (var (gid, to, row) in moved)
                if (to == team)
                    onTeam[gid] = row;
                else
                    onTeam.Remove(gid);
            foreach (var slot in changed)
            {
                var holder = after.TryGetValue(slot, out var h) ? h : (long?)null;
                if (holder == null)
                {
                    // Emptied: only the player the game had there loses it (a moved-out player already did).
                    if (before.TryGetValue(slot, out var old) && onTeam.TryGetValue(old, out var oldRow) && RosterBook.Raw(r, oldRow, Key(r, slot)!) != 0)
                        Put(old, oldRow, slot, 0);
                    continue;
                }
                if (holder is { } who && !onTeam.ContainsKey(who))
                {
                    skipped.Add($"{RosterBook.RosterTable}: player {who} is not on team {team} in the save, so {slot} was left alone.");
                    continue;
                }
                foreach (var (gid, row) in onTeam)
                {
                    var want = gid == holder ? 1 : 0;
                    if (RosterBook.Raw(r, row, Key(r, slot)!) != want || cells.ContainsKey((row, slot)))
                        Put(gid, row, slot, want);
                }
            }
        }
    }

    private static void AddCells(string table, long id, TdbTable g, int gameRow, TdbTable p, int projectRow, TdbTable r, int rosterRow, string[] skip,
        List<RosterCellChange> changes)
    {
        for (var f = 0; f < p.Fields.Count; f++)
        {
            var field = p.Fields[f];
            if (field.IsPoolPointer || skip.Contains(field.Label))
                continue;
            var before = g.Read(gameRow, g.Fields[f]);
            var after = p.Read(projectRow, field);
            if (!Equals(before, after))
                changes.Add(new RosterCellChange(table, id, rosterRow, r.Fields[f], before, after, r.Read(rosterRow, r.Fields[f])));
        }
    }

    private static List<int> ChangedRows(TdbFile gameFile, TdbTable g, TdbFile projectFile, TdbTable p, string name, List<string> skipped)
    {
        if (p.Count > g.Count)
            skipped.Add($"{name}: {p.Count - g.Count} row(s) the project added are not copied (adding players to a roster save is not done yet).");
        else if (p.Count < g.Count)
            skipped.Add($"{name}: the project has fewer rows than the game, which the sync does not handle.");
        var shared = Math.Min(g.Count, p.Count);
        var list = new List<int>();
        for (var row = 0; row < shared; row++)
            if (!gameFile.Data.AsSpan(g.RecordsAt + row * g.Stride, g.Stride).SequenceEqual(projectFile.Data.AsSpan(p.RecordsAt + row * p.Stride, p.Stride)))
                list.Add(row);
        return list;
    }

    private static (TdbTable, TdbTable, TdbTable)? Tables3(TdbFile game, TdbFile project, TdbFile roster, string name)
    {
        var g = game.Tables.FirstOrDefault(t => t.Label == name);
        var p = project.Tables.FirstOrDefault(t => t.Label == name);
        var r = roster.Tables.FirstOrDefault(t => t.Label == name);
        if (g == null || p == null || r == null)
            return null;
        bool Same(TdbTable a, TdbTable b) => a.Stride == b.Stride && a.Fields.Count == b.Fields.Count
            && a.Fields.Zip(b.Fields).All(z => z.First.ShortName == z.Second.ShortName && z.First.BitOffset == z.Second.BitOffset && z.First.Depth == z.Second.Depth);
        return Same(g, p) && Same(g, r) ? (g, p, r) : null;
    }

    private static TdbField? Key(TdbTable table, string name) => table.Fields.FirstOrDefault(f => f.Label == name);

    private static Dictionary<long, int> Unique(TdbTable table, TdbField key)
    {
        var map = new Dictionary<long, int>();
        var twice = new HashSet<long>();
        for (var r = 0; r < table.Count; r++)
            if (!map.TryAdd(RosterBook.Raw(table, r, key), r))
                twice.Add(RosterBook.Raw(table, r, key));
        foreach (var id in twice)
            map.Remove(id);
        return map;
    }

    /// <summary>Membership index → game_id.</summary>
    private static Dictionary<long, long> Owners(TdbFile file)
    {
        var t = file.Tables.First(x => x.Label == RosterBook.MembershipTable);
        var index = Key(t, "index")!;
        var player = Key(t, "playerid")!;
        var map = new Dictionary<long, long>();
        for (var r = 0; r < t.Count; r++)
            map.TryAdd(RosterBook.Raw(t, r, index), RosterBook.Raw(t, r, player));
        return map;
    }

    private static Dictionary<long, List<long>> IndexesByPlayer(TdbFile file)
    {
        var map = new Dictionary<long, List<long>>();
        foreach (var (index, gid) in Owners(file))
        {
            if (!map.TryGetValue(gid, out var list))
                map[gid] = list = [];
            list.Add(index);
        }
        return map;
    }
}

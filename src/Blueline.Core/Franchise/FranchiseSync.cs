using Blueline.Formats.Tdb;

namespace Blueline.Core.Franchise;

/// <summary>One league cell to change in a Franchise save.</summary>
public sealed record FranchiseCellChange(string Table, int Row, string What, TdbField Field, object Old, object New);

/// <summary>
/// Copies team details from a roster-style database (the game database, a project's database or a roster save) into a
/// Franchise save's league tables. A Franchise copies these at creation and never reads them again, so an existing
/// franchise keeps 2018 affiliations and arena names unless they are written into it.
/// <para>
/// League rows are matched to <c>exhibitionteams</c> rows by art id and league (the lowest matching index, since
/// alumni and circuit teams share art ids). The league rows are numbered differently (AHL 31 to 61 in the league, 35 to
/// 65 in exhibition), so <c>ahlaffiliate</c>, which stores the farm team's row + 1, is translated. Arena fields are
/// copied from the matched team's <c>exhibitionarena</c> row to its <c>leaguearena</c> row.
/// </para>
/// <para>
/// <b>Experimental.</b> Seen in game 2026-11-18: a save with the whole plan applied (144 cells) crashed on load, while an
/// unedited repack loads. Other blocks of the save probably have to agree with these tables. Do not write it to a real save.
/// <see cref="PlanRoster"/> (for new franchises, through the roster save) is the proven route.
/// </para>
/// </summary>
public static class FranchiseSync
{
    public static readonly string[] TeamTextFields = ["teamname", "cityname", "abbrname", "nickname"];
    public static readonly string[] ArenaFields = ["arenaname", "maxattendance", "yearopened"];

    /// <summary>The NHL (0) and AHL (1) clubs are the ones a Franchise plays with.</summary>
    private static readonly HashSet<long> Leagues = [0, 1];

    public static IReadOnlyList<FranchiseCellChange> Plan(TdbFile source, TdbFile franchise)
    {
        var exTeams = Table(source, "exhibitionteams");
        var exArenas = Table(source, "exhibitionarena");
        var lgTeams = Table(franchise, "leagueteams");
        var lgArenas = Table(franchise, "leaguearena");

        // exhibition index -> exhibition row, and (artid, league) -> lowest exhibition index
        var exIndex = Field(exTeams, "index");
        var exRowByIndex = new Dictionary<long, int>();
        var exByArt = new Dictionary<(long, long), (long Index, int Row)>();
        for (var r = 0; r < exTeams.Count; r++)
        {
            var index = Convert.ToInt64(exTeams.Read(r, exIndex));
            exRowByIndex[index] = r;
            var key = (Num(exTeams, r, "artid"), Num(exTeams, r, "league"));
            if (!exByArt.TryGetValue(key, out var have) || index < have.Index)
                exByArt[key] = (index, r);
        }
        var exArenaRow = RowsByIndex(exArenas);
        var lgArenaRow = RowsByIndex(lgArenas);

        // league row -> exhibition row, and exhibition index -> league row
        var match = new Dictionary<int, int>();
        var leagueRowOfExIndex = new Dictionary<long, int>();
        for (var r = 0; r < lgTeams.Count; r++)
        {
            var league = Num(lgTeams, r, "league");
            if (!Leagues.Contains(league) || Num(lgTeams, r, "teamtype") != 0)
                continue;
            if (!exByArt.TryGetValue((Num(lgTeams, r, "artid"), league), out var ex))
                continue;
            // All-star teams are league 0 too; they have no affiliate and no real arena, so leave them.
            if (Text(lgTeams, r, "teamname").Contains("All-Star", StringComparison.OrdinalIgnoreCase))
                continue;
            match[r] = ex.Row;
            leagueRowOfExIndex[ex.Index] = r;
        }

        var changes = new List<FranchiseCellChange>();
        foreach (var (lg, ex) in match.OrderBy(m => m.Key))
        {
            var what = Text(lgTeams, lg, "abbrname");
            foreach (var name in TeamTextFields)
                Copy(changes, exTeams, ex, lgTeams, lg, name, what);

            if (Num(lgTeams, lg, "league") == 0)
            {
                var stored = Num(exTeams, ex, "ahlaffiliate");
                long translated = 0;
                if (stored > 0 && leagueRowOfExIndex.TryGetValue(stored - 1, out var farmRow))
                    translated = farmRow + 1;
                if (stored == 0 || translated > 0)
                    Set(changes, lgTeams, lg, "ahlaffiliate", translated, what);
            }

            if (exArenaRow.TryGetValue(Num(exTeams, ex, "arenaid"), out var exArena)
                && lgArenaRow.TryGetValue(Num(lgTeams, lg, "arenaid"), out var lgArena))
                foreach (var name in ArenaFields)
                    Copy(changes, exArenas, exArena, lgArenas, lgArena, name, what + " arena");
        }
        return changes;
    }

    /// <summary>
    /// The NHL and AHL club names and AHL affiliates of a project's database, copied straight into a roster save (a new
    /// Franchise takes its affiliates from the roster save). <see cref="Teams.RosterSync"/> only copies what the project
    /// changed from the game's database, and Puck Peak's database already has the 2025-26 affiliates and names, so a roster
    /// made before it keeps the 2018 ones. Seen in game 2026-11-18: a new franchise showed St. Louis with slot 55 (San
    /// Antonio in 2018) as its farm team. Both tables use exhibition numbering, so rows match by index and values copy as
    /// they are.
    /// </summary>
    public static Teams.RosterSyncPlan PlanRoster(TdbFile project, TdbFile roster)
    {
        var from = Table(project, "exhibitionteams");
        var to = Table(roster, "exhibitionteams");
        var toRows = RowsByIndex(to);
        var fromArenas = Table(project, "exhibitionarena");
        var toArenas = Table(roster, "exhibitionarena");
        var fromArenaRows = RowsByIndex(fromArenas);
        var toArenaRows = RowsByIndex(toArenas);
        var changes = new List<Teams.RosterCellChange>();
        var skipped = new List<string>();
        var arenasDone = new HashSet<int>();
        for (var r = 0; r < from.Count; r++)
        {
            if (!Leagues.Contains(Num(from, r, "league")))
                continue;
            var index = Num(from, r, "index");
            if (!toRows.TryGetValue(index, out var row) || Num(to, row, "league") != Num(from, r, "league"))
            {
                skipped.Add($"exhibitionteams: the roster has no matching club at index {index}.");
                continue;
            }
            foreach (var name in TeamTextFields.Append("ahlaffiliate"))
                Copy(from, r, to, row, index, name);

            // The club's arena, as each side's own arenaid points to it.
            if (fromArenaRows.TryGetValue(Num(from, r, "arenaid"), out var fromArena)
                && toArenaRows.TryGetValue(Num(to, row, "arenaid"), out var toArena) && arenasDone.Add(toArena))
                foreach (var name in ArenaFields)
                    Copy(fromArenas, fromArena, toArenas, toArena, Num(toArenas, toArena, "index"), name);
        }
        return new Teams.RosterSyncPlan(changes, [], skipped);

        void Copy(TdbTable fromTable, int fromRow, TdbTable toTable, int toRow, long index, string name)
        {
            var source = fromTable.Fields.FirstOrDefault(f => f.Label == name);
            var target = toTable.Fields.FirstOrDefault(f => f.Label == name);
            if (source == null || target == null)
                return;
            var value = fromTable.Read(fromRow, source);
            var current = toTable.Read(toRow, target);
            if (value is string text)
            {
                text = Clean(text);
                if (text.Contains('�') || System.Text.Encoding.UTF8.GetByteCount(text) >= target.Depth / 8)
                {
                    skipped.Add($"{toTable.Label} #{index} {name}: \"{text}\" was not copied (does not fit or did not decode cleanly).");
                    return;
                }
                changes.Add(new Teams.RosterCellChange(toTable.Label, index, toRow, target, null, text, Clean((string)current)));
            }
            else
                changes.Add(new Teams.RosterCellChange(toTable.Label, index, toRow, target, null, Convert.ToInt64(value), Convert.ToInt64(current)));
        }
    }

    /// <summary>Writes the changes into the league database and repairs its checksums.</summary>
    public static void Apply(TdbFile franchise, IEnumerable<FranchiseCellChange> changes)
    {
        foreach (var change in changes)
        {
            var table = Table(franchise, change.Table);
            if (change.New is string text)
                table.Write(change.Row, change.Field, text);
            else
                table.Write(change.Row, change.Field, Convert.ToInt64(change.New));
        }
        franchise.RepairCrcs();
    }

    public static string Describe(IReadOnlyList<FranchiseCellChange> changes) =>
        string.Concat(changes.Select(c => $"{c.What,-12} {c.Table}.{c.Field.Label}: {Show(c.Old)} -> {Show(c.New)}{Environment.NewLine}"));

    private static string Show(object value) => value is string text ? $"\"{text}\"" : value.ToString() ?? "";

    private static void Copy(List<FranchiseCellChange> changes, TdbTable from, int fromRow, TdbTable to, int toRow, string name, string what)
    {
        var source = from.Fields.FirstOrDefault(f => f.Label == name);
        if (source == null)
            return;
        var value = from.Read(fromRow, source);
        Set(changes, to, toRow, name, value is string s ? Clean(s) : value, what);
    }

    private static void Set(List<FranchiseCellChange> changes, TdbTable table, int row, string name, object value, string what)
    {
        var field = table.Fields.FirstOrDefault(f => f.Label == name);
        if (field == null)
            return;
        var old = table.Read(row, field);
        var oldValue = old is string s ? Clean(s) : old;
        if (value is string text)
        {
            // Strings that would not fit are left alone rather than cut short.
            if (field.IsInlineString && System.Text.Encoding.UTF8.GetByteCount(text) >= field.Depth / 8)
                return;
            // Text that did not decode cleanly would not write back byte for byte.
            if (text.Contains('�'))
                return;
            if (Equals(oldValue, text))
                return;
        }
        else if (Convert.ToInt64(old) == Convert.ToInt64(value))
            return;
        changes.Add(new FranchiseCellChange(table.Label, row, what, field, oldValue, value));
    }

    private static string Clean(string text) => text.TrimEnd('\0');

    private static TdbTable Table(TdbFile file, string name) =>
        file.Tables.FirstOrDefault(t => t.Label == name) ?? throw new InvalidDataException($"The database has no {name} table.");

    private static TdbField Field(TdbTable table, string name) =>
        table.Fields.FirstOrDefault(f => f.Label == name) ?? throw new InvalidDataException($"{table.Label} has no {name} field.");

    private static long Num(TdbTable table, int row, string name) => Convert.ToInt64(table.Read(row, Field(table, name)));

    private static string Text(TdbTable table, int row, string name) => Clean(Convert.ToString(table.Read(row, Field(table, name))) ?? "");

    private static Dictionary<long, int> RowsByIndex(TdbTable table)
    {
        var index = Field(table, "index");
        var rows = new Dictionary<long, int>();
        for (var r = 0; r < table.Count; r++)
            rows.TryAdd(Convert.ToInt64(table.Read(r, index)), r);
        return rows;
    }
}

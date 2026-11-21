using System.Text;
using Blueline.Formats.Tdb;

namespace Blueline.Core.Teams;

/// <summary>
/// One database cell the project changed, and what the roster save holds there now. <c>Base</c> is the game database's
/// value, or null when the game has no such row.
/// </summary>
public sealed record RosterCellChange(string Table, long Index, int RosterRow, TdbField Field, object? Base, object Project, object Roster)
{
    /// <summary>The roster already holds the project's value.</summary>
    public bool Done => Equals(Roster, Project);

    /// <summary>The roster holds neither the game's value nor the project's, so writing replaces a change the roster made itself.</summary>
    public bool Overwrites => !Done && !Equals(Roster, Base);

    public static string Show(TdbField field, object? value) =>
        value is null ? "(no row)" : value is string text ? $"\"{text}\"" : field.Label.EndsWith("color", StringComparison.OrdinalIgnoreCase) ? Convert.ToInt64(value).ToString("X6") : value.ToString() ?? "";
}

/// <summary>A row the project added that the roster does not have yet. <c>Values</c> covers every field but string-pool pointers.</summary>
public sealed record RosterRowAdd(string Table, long Index, string What, IReadOnlyList<(TdbField Field, object Value)> Values);

public sealed record RosterSyncPlan(IReadOnlyList<RosterCellChange> Changes, IReadOnlyList<RosterRowAdd> Adds, IReadOnlyList<string> Skipped)
{
    public IReadOnlyList<RosterCellChange> Pending => Changes.Where(c => !c.Done).ToList();

    /// <summary>How many writes are left: pending cells plus new rows.</summary>
    public int ToWrite => Pending.Count + Adds.Count;

    /// <summary>Everything the project changed that the sync can copy, whether or not the roster has it already.</summary>
    public int Total => Changes.Count + Adds.Count;

    /// <summary>"3 cells and 1 new row", for messages.</summary>
    public string WhatIsLeft
    {
        get
        {
            var cells = Pending.Count;
            var parts = new List<string>();
            if (cells > 0 || Adds.Count == 0)
                parts.Add($"{cells} cell{(cells == 1 ? "" : "s")}");
            if (Adds.Count > 0)
                parts.Add($"{Adds.Count} new row{(Adds.Count == 1 ? "" : "s")}");
            return string.Join(" and ", parts);
        }
    }
}

/// <summary>
/// Copies the project's own database edits into a roster save, which the game reads instead of the database once a roster is
/// active. Only cells where the project differs from the game's database are written; everything else in the roster (the
/// Overtime or Puck Peak roster's own changes) stays. Rows are matched by their "index" field, so the roster may order or
/// number its rows differently. Tables in <see cref="RowKeys"/> (the glove and helmet colours) are matched by what a row is
/// instead (team, gear piece, era and kit), and rows the project added there are added to the roster too. Rows added to
/// other tables are not copied, and tables without a unique index are skipped; both are listed in
/// <see cref="RosterSyncPlan.Skipped"/>.
/// </summary>
public static class RosterSync
{
    public const string TitleId = "CUSA11126";
    public const string CustomRoster = "RosterCustom0";

    /// <summary>
    /// Tables whose rows are matched by these fields rather than by their index. A gear row's index is only a number, while
    /// team, equipment, era and variant say which kit it colours. So the roster's row for the same kit is found even if the
    /// roster numbers its rows differently, and a kit the roster has no row for gets a new one.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> RowKeys = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        [GearColours.TableName] = ["team", "equipment", "era", "variant"],
        // One kit slot per team, art id, brand and variant (docs/UNIFORM_SLOTS.md), so a new slot is added to the roster too.
        [UniformSlots.TableName] = ["teamid", "teamartid", "style", "variant"],
    };

    /// <summary>The game's save folder under a shadPS4 user folder.</summary>
    public static string SaveDataFolder(string emulatorUserFolder) => Path.Combine(emulatorUserFolder, "home", "1000", "savedata", TitleId);

    /// <summary>Where backups go: beside the save folder, so the game does not see them.</summary>
    /// <summary>The custom roster save (RosterCustom0) of an emulator, or null when there is none.</summary>
    public static string? CustomRosterFolder(string emulatorUserFolder) =>
        Path.Combine(SaveDataFolder(emulatorUserFolder), CustomRoster) is var folder && RosterSave.IsRosterSave(folder) ? folder : null;

    public static string BackupFolder(string emulatorUserFolder) => SaveDataFolder(emulatorUserFolder) + "-backups";

    /// <summary>Plans from raw bytes: the game's database (without this project), the project's, and a roster save.</summary>
    public static RosterSyncPlan Plan(byte[] game, byte[] project, RosterSave save, string metaXml) =>
        Plan(Open(game, metaXml), Open(project, metaXml), Open(save.Database, metaXml));

    private static TdbFile Open(byte[] data, string metaXml)
    {
        var file = TdbFile.Parse((byte[])data.Clone());
        file.ApplyMeta(metaXml);
        return file;
    }

    /// <summary>All three databases must have the meta's names applied.</summary>
    public static RosterSyncPlan Plan(TdbFile game, TdbFile project, TdbFile roster)
    {
        var changes = new List<RosterCellChange>();
        var adds = new List<RosterRowAdd>();
        var skipped = new List<string>();
        var gameTables = game.Tables.ToDictionary(t => t.ShortName, StringComparer.Ordinal);
        var projectTables = project.Tables.ToDictionary(t => t.ShortName, StringComparer.Ordinal);

        foreach (var rosterTable in roster.Tables)
        {
            if (!gameTables.TryGetValue(rosterTable.ShortName, out var baseTable) || !projectTables.TryGetValue(rosterTable.ShortName, out var projectTable))
                continue;
            var name = rosterTable.Label;
            // Player tables are matched by player and team, not by index (Roster.PlayerSync, after this loop).
            if (Roster.PlayerSync.Tables.Contains(name))
                continue;
            if (!SameLayout(baseTable, projectTable) || !SameLayout(baseTable, rosterTable))
            {
                if (Differs(game, baseTable, project, projectTable))
                    skipped.Add($"{name}: the project's table is laid out differently from the roster's, so it was left alone.");
                continue;
            }

            var shared = Math.Min(baseTable.Count, projectTable.Count);
            var rows = Enumerable.Range(0, shared).Where(r => !Record(game, baseTable, r).SequenceEqual(Record(project, projectTable, r))).ToList();
            if (RowKeys.TryGetValue(name, out var keyNames) && keyNames.All(k => projectTable.Fields.Any(f => f.Label == k)))
            {
                if (projectTable.Count < baseTable.Count)
                    skipped.Add($"{name}: the project has fewer rows than the game, which the sync does not handle.");
                else
                    PlanByKey(name, keyNames, baseTable, projectTable, rosterTable,
                        [.. rows, .. Enumerable.Range(baseTable.Count, projectTable.Count - baseTable.Count)], changes, adds, skipped);
                continue;
            }
            if (projectTable.Count > baseTable.Count)
                skipped.Add($"{name}: {projectTable.Count - baseTable.Count} row(s) the project added are not copied (adding rows to a roster save is not done yet).");
            else if (projectTable.Count < baseTable.Count)
                skipped.Add($"{name}: the project has fewer rows than the game, which the sync does not handle.");
            if (rows.Count == 0)
                continue;

            var key = projectTable.Fields.FirstOrDefault(f => f.Label == "index" && !f.IsInlineString);
            var rosterKey = key == null ? null : rosterTable.Fields.First(f => f.ShortName == key.ShortName);
            var rosterRows = rosterKey == null ? null : UniqueIndex(rosterTable, rosterKey);
            if (key == null || rosterRows == null || UniqueIndex(projectTable, key) == null)
            {
                skipped.Add($"{name}: {rows.Count} changed row(s) not copied, because the table has no unique index to match rows by.");
                continue;
            }

            foreach (var row in rows)
            {
                var index = Convert.ToInt64(projectTable.Read(row, key));
                if (Convert.ToInt64(baseTable.Read(row, key)) != index)
                {
                    skipped.Add($"{name}: row {row} changed its index, so it was not copied.");
                    continue;
                }
                if (!rosterRows.TryGetValue(index, out var rosterRow))
                {
                    skipped.Add($"{name}: the roster has no row with index {index}, so that row's changes were not copied.");
                    continue;
                }
                for (var f = 0; f < projectTable.Fields.Count; f++)
                {
                    var field = projectTable.Fields[f];
                    if (field.IsPoolPointer)
                        continue;
                    var before = baseTable.Read(row, baseTable.Fields[f]);
                    var after = projectTable.Read(row, field);
                    if (!Equals(before, after))
                        changes.Add(new RosterCellChange(name, index, rosterRow, rosterTable.Fields[f], before, after, rosterTable.Read(rosterRow, rosterTable.Fields[f])));
                }
            }
        }
        if (game.Tables.Any(t => t.Label == Roster.RosterBook.BioTable))
            Roster.PlayerSync.Plan(game, project, roster, changes, skipped);
        return new RosterSyncPlan(changes, adds, skipped);
    }

    /// <summary>
    /// Plans a table whose rows are matched by <paramref name="keyNames"/>. For each row the project changed or added: when
    /// the roster has a row with the same key, the cells the project changed (against the game's row with that key, or all of
    /// them when the game has none) are written there. Otherwise the project's row is added. A re-pointed row (its key
    /// changed) is therefore added under its new key, and the roster's row under the old key is left as it is.
    /// </summary>
    private static void PlanByKey(string name, string[] keyNames, TdbTable baseTable, TdbTable projectTable, TdbTable rosterTable,
        IReadOnlyList<int> rows, List<RosterCellChange> changes, List<RosterRowAdd> adds, List<string> skipped)
    {
        var fields = projectTable.Fields.ToList();
        var keyAt = keyNames.Select(k => fields.FindIndex(f => f.Label == k)).ToArray();
        var indexAt = fields.FindIndex(f => f.Label == "index" && !f.IsInlineString);
        string Key(TdbTable table, int row) => string.Join("/", keyAt.Select(f => Convert.ToInt64(table.Read(row, table.Fields[f]))));
        ILookup<string, int> ByKey(TdbTable table) => Enumerable.Range(0, table.Count).ToLookup(r => Key(table, r));
        long IndexOf(TdbTable table, int row) => indexAt < 0 ? row : Convert.ToInt64(table.Read(row, table.Fields[indexAt]));

        var baseByKey = ByKey(baseTable);
        var projectByKey = ByKey(projectTable);
        var rosterByKey = ByKey(rosterTable);
        var rosterIndexes = Enumerable.Range(0, rosterTable.Count).Select(r => IndexOf(rosterTable, r)).ToHashSet();
        var nextIndex = rosterIndexes.DefaultIfEmpty(-1).Max() + 1;
        var spare = rosterTable.Allocated - rosterTable.Count;

        foreach (var row in rows)
        {
            var key = Key(projectTable, row);
            var what = string.Join(", ", keyNames.Zip(key.Split('/')).Select(p => $"{p.First} {p.Second}"));
            var inBase = baseByKey[key].ToList();
            var inRoster = rosterByKey[key].ToList();
            if (inBase.Count > 1 || inRoster.Count > 1 || projectByKey[key].Count() > 1)
            {
                skipped.Add($"{name}: more than one row is {what}, so it is not clear which to copy; left alone.");
                continue;
            }
            int? baseRow = inBase.Count == 1 ? inBase[0] : null;

            if (inRoster.Count == 1)
            {
                var rosterRow = inRoster[0];
                for (var f = 0; f < fields.Count; f++)
                {
                    if (fields[f].IsPoolPointer || f == indexAt)
                        continue;
                    var after = projectTable.Read(row, fields[f]);
                    var before = baseRow is { } b ? baseTable.Read(b, baseTable.Fields[f]) : null;
                    if (baseRow != null && Equals(before, after))
                        continue;
                    changes.Add(new RosterCellChange(name, IndexOf(rosterTable, rosterRow), rosterRow, rosterTable.Fields[f], before, after,
                        rosterTable.Read(rosterRow, rosterTable.Fields[f])));
                }
                continue;
            }

            if (spare-- <= 0)
            {
                skipped.Add($"{name}: the roster has no spare rows left, so the new row for {what} was not added.");
                continue;
            }
            var index = IndexOf(projectTable, row);
            if (indexAt >= 0 && !rosterIndexes.Add(index))
                rosterIndexes.Add(index = nextIndex);
            nextIndex = Math.Max(nextIndex, index + 1);
            var values = new List<(TdbField, object)>();
            for (var f = 0; f < fields.Count; f++)
                if (!fields[f].IsPoolPointer)
                    values.Add((rosterTable.Fields[f], f == indexAt ? index : projectTable.Read(row, fields[f])));
            adds.Add(new RosterRowAdd(name, index, what, values));
        }
    }

    /// <summary>Writes the plan's pending cells into the roster and repairs its checksums. Returns how many were written.</summary>
    public static int Apply(TdbFile roster, RosterSyncPlan plan)
    {
        var tables = roster.Tables.ToDictionary(t => t.Label, StringComparer.Ordinal);
        var written = 0;
        foreach (var change in plan.Pending)
        {
            var table = tables[change.Table];
            if (change.Project is string text)
                table.Write(change.RosterRow, change.Field, text);
            else
                table.Write(change.RosterRow, change.Field, Convert.ToInt64(change.Project));
            written++;
        }
        foreach (var add in plan.Adds)
        {
            var table = tables[add.Table];
            if (table.Count == 0)
                throw new InvalidOperationException($"{add.Table} has no row to start a new one from.");
            var row = table.AddRow(table.Count - 1);
            foreach (var (field, value) in add.Values)
                if (value is string text)
                    table.Write(row, field, text);
                else
                    table.Write(row, field, Convert.ToInt64(value));
            written++;
        }
        roster.RepairCrcs();
        return written;
    }

    /// <summary>
    /// Syncs a roster save folder in place: a timestamped copy goes to the backup folder first, the new save is built in a
    /// temporary folder and checked by reading it back, and only then swapped in. Returns the backup's path, or null when
    /// there was nothing to write.
    /// </summary>
    public static string? Write(RosterSave save, RosterSyncPlan plan, string metaXml, string backupFolder)
    {
        if (plan.ToWrite == 0)
            return null;
        var roster = Open(save.Database, metaXml);
        Apply(roster, plan);

        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(save.Folder));
        var backup = Path.Combine(backupFolder, $"{name}-{DateTime.Now:yyyyMMdd-HHmmss}");
        RosterSave.CopyFolder(save.Folder, backup);

        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(save.Folder))!;
        var temporary = Path.Combine(parent, $"{name}.blueline-new");
        if (Directory.Exists(temporary))
            Directory.Delete(temporary, true);
        save.WriteTo(temporary, roster.Data, metaXml);

        var check = RosterSave.Read(temporary);
        if (!check.Database.AsSpan().SequenceEqual(roster.Data))
            throw new InvalidDataException("The new roster save did not read back the same; the original was left alone.");
        var checkFile = TdbFile.Parse((byte[])check.Database.Clone());
        if (check.CrcValue != RosterCrc.Compute(checkFile, metaXml))
            throw new InvalidDataException("The new roster save's CRCValue did not verify; the original was left alone.");

        var old = Path.Combine(parent, $"{name}.blueline-old");
        if (Directory.Exists(old))
            Directory.Delete(old, true);
        Directory.Move(save.Folder, old);
        Directory.Move(temporary, save.Folder);
        Directory.Delete(old, true);
        return backup;
    }

    /// <summary>A short report of a plan, one line per cell.</summary>
    public static string Describe(RosterSyncPlan plan)
    {
        var text = new StringBuilder();
        foreach (var c in plan.Changes)
            text.AppendLine($"{c.Table} #{c.Index} {c.Field.Label}: {RosterCellChange.Show(c.Field, c.Base)} -> {RosterCellChange.Show(c.Field, c.Project)}"
                + (c.Done ? " (already in the roster)" : c.Overwrites ? $" (replaces the roster's own {RosterCellChange.Show(c.Field, c.Roster)})" : ""));
        foreach (var a in plan.Adds)
            text.AppendLine($"{a.Table}: new row #{a.Index} for {a.What}");
        foreach (var s in plan.Skipped)
            text.AppendLine("Skipped: " + s);
        return text.ToString();
    }

    private static bool SameLayout(TdbTable a, TdbTable b) =>
        a.Stride == b.Stride && a.Fields.Count == b.Fields.Count
        && a.Fields.Zip(b.Fields).All(p => p.First.ShortName == p.Second.ShortName && p.First.BitOffset == p.Second.BitOffset && p.First.Depth == p.Second.Depth && p.First.Kind == p.Second.Kind);

    private static bool Differs(TdbFile a, TdbTable ta, TdbFile b, TdbTable tb) =>
        ta.Count != tb.Count || !a.Data.AsSpan(ta.RecordsAt, ta.Count * ta.Stride).SequenceEqual(b.Data.AsSpan(tb.RecordsAt, tb.Count * tb.Stride));

    private static ReadOnlySpan<byte> Record(TdbFile file, TdbTable table, int row) => file.Data.AsSpan(table.RecordsAt + row * table.Stride, table.Stride);

    private static Dictionary<long, int>? UniqueIndex(TdbTable table, TdbField key)
    {
        var rows = new Dictionary<long, int>();
        for (var r = 0; r < table.Count; r++)
            if (!rows.TryAdd(Convert.ToInt64(table.Read(r, key)), r))
                return null;
        return rows;
    }
}

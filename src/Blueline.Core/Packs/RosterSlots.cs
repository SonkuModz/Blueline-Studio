using System.Text;
using System.Text.RegularExpressions;
using Blueline.Formats.Sony;
using Blueline.Formats.Tdb;

namespace Blueline.Core.Packs;

public sealed record RosterSlot(string Name, string Folder, string Title);

/// <summary>A roster a pack put into the player's saves: where, what it replaced, and what it was.</summary>
public sealed record InstalledRoster(string SaveData, string Slot, string? Backup, IReadOnlyDictionary<string, string> Files);

public enum RosterRemoval { Removed, Restored, ChangedSinceInstall, AlreadyGone }

/// <summary>
/// The custom roster slots in a savedata/CUSA11126 folder. Packs never write RosterCustom0: Puck Peak and Overtime Roster own it,
/// and roster sync writes there. The slot's name lives in three places: NAME, the first 32 bytes of INDEX, and SAVEDATA_DIRECTORY in
/// sce_sys/param.sfo. All three are rewritten when a save moves to another slot.
/// </summary>
public static partial class RosterSlots
{
    public const string Reserved = "RosterCustom0";
    private const int IndexNameLength = 32;

    [GeneratedRegex("^RosterCustom[1-9]$")]
    private static partial Regex PackSlot();

    public static bool IsPackSlot(string slot) => PackSlot().IsMatch(slot);

    public static IReadOnlyList<RosterSlot> List(string saveData) =>
        !Directory.Exists(saveData) ? [] :
        Directory.GetDirectories(saveData, "RosterCustom*").Where(RosterSave.IsRosterSave)
            .Select(f => new RosterSlot(Path.GetFileName(f), f, Title(f))).OrderBy(s => s.Name, StringComparer.Ordinal).ToList();

    public static string Title(string folder)
    {
        var sfo = Path.Combine(folder, "sce_sys", "param.sfo");
        try
        {
            if (File.Exists(sfo) && ParamSfo.Parse(File.ReadAllBytes(sfo))["SUBTITLE"] is { Length: > 0 } title)
                return title;
        }
        catch (InvalidDataException)
        {
        }
        return Path.GetFileName(folder);
    }

    /// <summary>The first custom slot from 1 up with nothing in it, or null when all nine are taken.</summary>
    public static string? FirstFree(string saveData) =>
        Enumerable.Range(1, 9).Select(i => "RosterCustom" + i).FirstOrDefault(s => !Directory.Exists(Path.Combine(saveData, s)));

    public static string BackupFolder(string saveData) => Path.TrimEndingDirectorySeparator(saveData) + "-backups";

    /// <summary>
    /// Puts a roster save into a slot. A save already there is copied to the backup folder first. The new save is built in a
    /// temporary folder, read back, and only then swapped in.
    /// </summary>
    public static InstalledRoster Install(string saveData, string slot, string sourceFolder, string tag)
    {
        if (!IsPackSlot(slot))
            throw new InvalidOperationException(slot == Reserved
                ? "RosterCustom0 belongs to Puck Peak's roster, so a pack never writes it."
                : $"{slot} is not a custom roster slot a pack can use (RosterCustom1 to RosterCustom9).");
        if (Emulator.ShadInstall.AnyRunning())
            throw new InvalidOperationException("shadPS4 is running. Close the game before installing a roster.");
        Directory.CreateDirectory(saveData);
        var target = Path.Combine(saveData, slot);

        string? backup = null;
        if (Directory.Exists(target))
        {
            backup = Path.Combine(BackupFolder(saveData), $"{slot}-{DateTime.Now:yyyyMMdd-HHmmss}-before-{tag}");
            RosterSave.CopyFolder(target, backup);
        }

        var temporary = Path.Combine(saveData, $"{slot}.blueline-new");
        if (Directory.Exists(temporary))
            Directory.Delete(temporary, true);
        RosterSave.CopyFolder(sourceFolder, temporary);
        Rename(temporary, slot);
        RosterSave.Read(temporary);

        if (Directory.Exists(target))
        {
            var old = Path.Combine(saveData, $"{slot}.blueline-old");
            if (Directory.Exists(old))
                Directory.Delete(old, true);
            Directory.Move(target, old);
            Directory.Move(temporary, target);
            Directory.Delete(old, true);
        }
        else
            Directory.Move(temporary, target);
        return new InstalledRoster(saveData, slot, backup, HashFolder(target));
    }

    /// <summary>
    /// Takes a pack's roster back out. The save is moved to the backup folder, never deleted, and the save that was in the slot
    /// before, if any, is copied back. When the player has saved over it since install, nothing happens unless forced.
    /// </summary>
    public static RosterRemoval Remove(InstalledRoster roster, string tag, bool force = false)
    {
        var target = Path.Combine(roster.SaveData, roster.Slot);
        if (!Directory.Exists(target))
        {
            if (roster.Backup != null && Directory.Exists(roster.Backup))
            {
                RosterSave.CopyFolder(roster.Backup, target);
                return RosterRemoval.Restored;
            }
            return RosterRemoval.AlreadyGone;
        }
        var now = HashFolder(target);
        var unchanged = now.Count == roster.Files.Count && now.All(f => roster.Files.TryGetValue(f.Key, out var h) && h == f.Value);
        if (!unchanged && !force)
            return RosterRemoval.ChangedSinceInstall;
        if (Emulator.ShadInstall.AnyRunning())
            throw new InvalidOperationException("shadPS4 is running. Close the game before removing a roster.");

        var keep = Path.Combine(BackupFolder(roster.SaveData), $"{roster.Slot}-{DateTime.Now:yyyyMMdd-HHmmss}-removed-{tag}");
        RosterSave.CopyFolder(target, keep);
        Directory.Delete(target, true);
        if (roster.Backup != null && Directory.Exists(roster.Backup))
        {
            RosterSave.CopyFolder(roster.Backup, target);
            return RosterRemoval.Restored;
        }
        return RosterRemoval.Removed;
    }

    /// <summary>Writes the slot name into NAME, INDEX and param.sfo.</summary>
    public static void Rename(string folder, string slot)
    {
        var name = Encoding.ASCII.GetBytes(slot);
        if (name.Length >= IndexNameLength)
            throw new ArgumentException($"{slot} is too long for a save slot name.");
        File.WriteAllBytes(Path.Combine(folder, "NAME"), name);

        var indexPath = Path.Combine(folder, "INDEX");
        var index = File.ReadAllBytes(indexPath);
        index.AsSpan(0, IndexNameLength).Clear();
        name.CopyTo(index, 0);
        File.WriteAllBytes(indexPath, index);

        var sfoPath = Path.Combine(folder, "sce_sys", "param.sfo");
        if (File.Exists(sfoPath))
        {
            var sfo = File.ReadAllBytes(sfoPath);
            ParamSfo.SetText(sfo, "SAVEDATA_DIRECTORY", slot);
            File.WriteAllBytes(sfoPath, sfo);
        }
    }

    public static Dictionary<string, string> HashFolder(string folder) =>
        Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(folder, f).Replace('\\', '/'), f => ModPack.Sha256(File.ReadAllBytes(f)), StringComparer.Ordinal);
}

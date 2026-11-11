namespace Blueline.Core;

/// <summary>A validated NHL 19 install folder: the one that holds eboot.bin and databigs.</summary>
public sealed class GameInstall
{
    private GameInstall(string root, bool useModsFolder)
    {
        Root = root;
        var mods = root + "-mods";
        if (useModsFolder && Directory.Exists(Path.Combine(mods, "databigs")))
            ModsFolder = mods;
    }

    public string Root { get; }
    public string Databigs => Path.Combine(Root, "databigs");

    /// <summary>
    /// The folder beside the install named "CUSA11126-mods", when there is one. shadPS4 lays it over the game, so
    /// tools that install there, such as Puck Peak, change the game without touching the install.
    /// </summary>
    public string? ModsFolder { get; }

    private string? ModsDatabigs => ModsFolder == null ? null : Path.Combine(ModsFolder, "databigs");

    private static IEnumerable<string> ArchivesIn(string? folder) =>
        folder != null && Directory.Exists(folder) ? Directory.GetFiles(folder, "*.big").Select(p => Path.GetFileName(p)!) : [];

    /// <summary>Archives only the mods folder has.</summary>
    public IReadOnlyList<string> ArchivesAddedByMods =>
        ArchivesIn(ModsDatabigs).Except(ArchivesIn(Databigs), StringComparer.OrdinalIgnoreCase).Where(n => !IsModArchive(n)).OrderBy(n => n, StringComparer.Ordinal).ToList();

    /// <summary>Archives in the install that the mods folder has its own copy of.</summary>
    public IReadOnlyList<string> ArchivesReplacedByMods =>
        ArchivesIn(ModsDatabigs).Intersect(ArchivesIn(Databigs), StringComparer.OrdinalIgnoreCase).Where(n => !IsModArchive(n)).OrderBy(n => n, StringComparer.Ordinal).ToList();

    /// <summary>Where an archive is really read from: the mods folder's copy wins.</summary>
    public string ArchivePath(string name)
    {
        if (ModsDatabigs is { } mods && File.Exists(Path.Combine(mods, name)))
            return Path.Combine(mods, name);
        return Path.Combine(Databigs, name);
    }

    /// <summary>Accepts the install folder, or a parent that holds a CUSA11126 folder.</summary>
    /// <param name="useModsFolder">False reads the install alone, ignoring any folder of mods beside it.</param>
    public static GameInstall Open(string folder, bool useModsFolder = true)
    {
        var candidate = Path.GetFullPath(folder);
        if (!File.Exists(Path.Combine(candidate, "eboot.bin")))
        {
            var nested = Path.Combine(candidate, "CUSA11126");
            if (File.Exists(Path.Combine(nested, "eboot.bin")))
                candidate = nested;
            else
                throw new InvalidDataException($"No eboot.bin in {candidate}. Choose the folder that holds eboot.bin and databigs.");
        }
        if (!File.Exists(Path.Combine(candidate, "databigs", "boot.big")))
            throw new InvalidDataException($"{candidate} has no databigs/boot.big, so it is not a complete NHL 19 install.");
        return new GameInstall(candidate, useModsFolder);
    }

    /// <summary>The archives Blueline installs. They are left out of the stock view of the game.</summary>
    public static bool IsModArchive(string fileName) =>
        fileName.StartsWith("patch_inxp_99", StringComparison.OrdinalIgnoreCase) ||
        fileName.StartsWith("patch_game_99", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Stock archives in the order the game mounts them, as read from eboot.bin: boot, inxp*, patch_inxp*,
    /// then game*, patch_game*, each group sorted by name. Later archives win. overlays_2x are opened by
    /// name; where they sit relative to the others was not established, so they are placed before inxp and
    /// the build warns about any file that comes from them.
    /// </summary>
    public IReadOnlyList<string> StockMountOrder() =>
        MountOrder(ArchivesIn(Databigs).Concat(ArchivesIn(ModsDatabigs)).Distinct(StringComparer.OrdinalIgnoreCase).Where(n => !IsModArchive(n)));

    /// <summary>Any set of archive names, sorted the way the game mounts them.</summary>
    public static IReadOnlyList<string> MountOrder(IEnumerable<string> archiveNames)
    {
        var names = archiveNames.ToList();
        var ordered = new List<string>();
        void Take(string prefix)
        {
            var group = names.Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n, StringComparer.Ordinal).ToList();
            ordered.AddRange(group);
            names.RemoveAll(group.Contains);
        }
        Take("boot");
        Take("overlays");
        Take("inxp");
        Take("patch_inxp");
        Take("game");
        Take("patch_game");
        ordered.AddRange(names.OrderBy(n => n, StringComparer.Ordinal));
        return ordered;
    }
}

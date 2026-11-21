using System.Text;
using System.Text.Json;
using Blueline.Formats.Eb;

namespace Blueline.Core.Packs;

/// <summary>A pack in the player's game. The list order is the priority: the first pack wins a file over the ones below it.</summary>
public sealed record InstalledPack(string Id, string Slug, string Name, string Author, string Version, bool Enabled,
    DateTimeOffset InstalledAt, string? Deployed, InstalledRoster? Roster);

public enum CheckLevel { Ok, Warning, Blocked }

public sealed record PackCheckLine(CheckLevel Level, string Title, string Detail);

/// <summary>A file two or more packs change. <see cref="Winner"/> is the pack whose copy the game gets.</summary>
public sealed record PackConflict(string Path, string Winner, IReadOnlyList<string> Losers);

/// <summary>A Blueline project's or unknown mod archive that holds some of the pack's files. The manager cannot rebuild it.</summary>
public sealed record OtherOverlap(string Archive, IReadOnlyList<string> Paths);

public sealed record PackCheck(PackManifest Manifest, IReadOnlyList<PackCheckLine> Lines, IReadOnlyList<PackConflict> Conflicts,
    IReadOnlyList<OtherOverlap> Others, IReadOnlyList<string> StockMismatches, IReadOnlyList<string> OverModsFolder, bool IsUpdate)
{
    public bool Blocked => Lines.Any(l => l.Level == CheckLevel.Blocked);
}

public sealed record PackInstallOptions
{
    /// <summary>The emulator's savedata/CUSA11126 folder. Needed only to install the pack's roster.</summary>
    public string? SaveData { get; init; }
    public bool InstallRoster { get; init; }
    /// <summary>A custom slot from RosterCustom1 up. Null picks the slot an earlier version used, else the first free one.</summary>
    public string? Slot { get; init; }
    /// <summary>Other mod archives to delete so the pack wins their files. Anything not named here keeps its files.</summary>
    public IReadOnlyList<string> RemoveOthers { get; init; } = [];
    /// <summary>Where in the priority list a new pack goes; 0 is the top. Null puts it on top. Updates keep their place.</summary>
    public int? Position { get; init; }
}

public sealed record PackInstallResult(InstalledPack Pack, IReadOnlyList<string> Archives, IReadOnlyList<string> Warnings);

public sealed record PackUninstallResult(bool Done, IReadOnlyList<string> Problems, RosterRemoval? Roster);

/// <summary>
/// Installs mod packs into a game the way Blueline installs projects: new patch_*_99_pk-* archives built on this machine, never an
/// edit to a stock file. No path is ever in two pack archives: the higher pack wins and the lower one is rebuilt without it, and a
/// pack leaves out files another mod archive holds unless the player removes that archive. State lives beside the game in
/// CUSA11126-packs (packs.json and a store holding each pack as a Blueline project), which the emulator never reads.
/// </summary>
public sealed class PackManager
{
    private sealed record State(int Format, List<InstalledPack> Packs);

    private readonly GameInstall _install;
    private State _state;

    public PackManager(GameInstall install, string? folder = null)
    {
        _install = install;
        Folder = folder ?? install.Root + "-packs";
        var file = Path.Combine(Folder, "packs.json");
        _state = File.Exists(file)
            ? JsonSerializer.Deserialize<State>(File.ReadAllText(file), ModPack.Json) ?? new State(1, [])
            : new State(1, []);
    }

    public string Folder { get; }
    public IReadOnlyList<InstalledPack> Packs => _state.Packs;

    public string StoreFolder(string id) => Path.Combine(Folder, "store", id);

    public PackManifest StoredManifest(string id) =>
        JsonSerializer.Deserialize<PackManifest>(File.ReadAllText(Path.Combine(StoreFolder(id), "manifest.json")), ModPack.Json)!;

    public InstalledPack? Find(string idOrSlug) =>
        _state.Packs.FirstOrDefault(p => p.Id == idOrSlug || p.Slug.Equals(idOrSlug, StringComparison.OrdinalIgnoreCase));

    /// <summary>Archive names the manager owns, for every installed pack.</summary>
    public IReadOnlySet<string> OwnedArchives() =>
        _state.Packs.Where(p => Directory.Exists(StoreFolder(p.Id)))
            .SelectMany(p => ModProject.Open(StoreFolder(p.Id)).Installed.Select(i => i.FileName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    // ---- checking

    public PackCheck Check(string packPath, VirtualFileSystem vfs) => Check(ModPack.ReadManifest(packPath), vfs);

    public PackCheck Check(PackManifest manifest, VirtualFileSystem vfs, int? position = null)
    {
        var lines = new List<PackCheckLine>();
        var game = PackExporter.ReadGame(_install);
        if (!game.TitleId.Equals(manifest.Game.TitleId, StringComparison.OrdinalIgnoreCase))
            lines.Add(new(CheckLevel.Blocked, $"Made for {manifest.Game.TitleId}, your game is {game.TitleId}", "This pack is for a different game, so it cannot be installed."));
        else if (game.AppVersion != manifest.Game.AppVersion)
            lines.Add(new(CheckLevel.Warning, $"{game.TitleId} {game.AppVersion}, pack made on {manifest.Game.AppVersion}", "Files may not line up with your version of the game."));
        else
            lines.Add(new(CheckLevel.Ok, $"{game.TitleId} · {game.AppVersion}", "Matches the pack."));

        var mismatches = PackExporter.WithStock(vfs, stockVfs => manifest.Files
            .Where(f => (stockVfs.ReadStock(f.Path) is { } bytes ? ModPack.Sha256(bytes) : null) != f.Stock).Select(f => f.Path).ToList());
        var overMods = manifest.Files.Where(f => vfs.Layers(f.Path).Any(l => l.IsFromModsFolder)).Select(f => f.Path).ToList();
        var known = manifest.Files.Count(f => f.Stock != null);
        lines.Add(mismatches.Count == 0
            ? new(CheckLevel.Ok, $"{known} of {known} stock files match", $"{manifest.Files.Count - known} are new files.")
            : new(CheckLevel.Warning, $"{mismatches.Count} file(s) differ from the game the pack was made on",
                $"For example {mismatches[0]}. The pack still replaces them, but it was made against a different copy."));
        if (overMods.Count > 0)
        {
            var dbs = overMods.Where(p => p.EndsWith(".db", StringComparison.OrdinalIgnoreCase)).Select(p => p[(p.LastIndexOf('/') + 1)..]).ToList();
            lines.Add(new(CheckLevel.Warning, $"Mods folder overlap: {overMods.Count} file(s)",
                "The pack wins over CUSA11126-mods." + (dbs.Count > 0 ? $" Its edits in {string.Join(", ", dbs)} are lost." : "")));
        }

        var existing = _state.Packs.FirstOrDefault(p => p.Id == manifest.Id);
        var order = _state.Packs.Where(p => p.Id != manifest.Id && p.Enabled).ToList();
        var at = existing != null ? Math.Min(_state.Packs.IndexOf(existing), order.Count) : Math.Clamp(position ?? 0, 0, order.Count);
        var conflicts = new List<PackConflict>();
        var paths = manifest.Files.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        var holders = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var sequence = order.Select(p => (p.Name, Paths: StoredManifest(p.Id).Files.Select(f => f.Path).ToHashSet(StringComparer.Ordinal))).ToList();
        sequence.Insert(at, (manifest.Name, paths));
        foreach (var (name, set) in sequence)
            foreach (var path in set.Where(paths.Contains))
                (holders.TryGetValue(path, out var list) ? list : holders[path] = []).Add(name);
        foreach (var (path, list) in holders.Where(h => h.Value.Count > 1).OrderBy(h => h.Key, StringComparer.Ordinal))
            conflicts.Add(new PackConflict(path, list[0], list.Skip(1).ToList()));
        if (conflicts.Count > 0)
            lines.Add(new(CheckLevel.Warning, $"{conflicts.Count} file(s) shared with other packs", "The higher pack in the list wins each one."));

        var others = OtherArchiveOverlaps(paths);
        if (others.Count > 0)
            lines.Add(new(CheckLevel.Warning, $"{others.Sum(o => o.Paths.Count)} file(s) shared with {others.Count} other mod archive(s)",
                "Those files stay with the other mod unless you choose Pack wins, which removes that archive."));

        var limit = ArchiveCount(manifest, vfs, existing != null);
        lines.Add(limit.Worst > ModInstaller.MaxArchivesPerGlob
            ? new(CheckLevel.Blocked, $"Archives {limit.Worst} / {ModInstaller.MaxArchivesPerGlob}", "The game reads at most 50 archives of each kind.")
            : new(CheckLevel.Ok, $"Archives {limit.Worst} / {ModInstaller.MaxArchivesPerGlob}", ""));
        var free = new DriveInfo(Path.GetPathRoot(_install.Databigs)!).AvailableFreeSpace;
        var need = manifest.Bytes * 2 + (1L << 30);
        lines.Add(free < need
            ? new(CheckLevel.Blocked, $"{free >> 20:N0} MB free", $"This install needs about {need >> 20:N0} MB, with a 1 GB margin.")
            : new(CheckLevel.Ok, $"{free / (1024.0 * 1024 * 1024):0.0} GB free", ""));

        return new PackCheck(manifest, lines, conflicts, others, mismatches, overMods, existing != null);
    }

    private (int Worst, int Inxp, int Game) ArchiveCount(PackManifest manifest, VirtualFileSystem vfs, bool isUpdate)
    {
        int Count(string family) =>
            Directory.GetFiles(_install.Databigs, family + "*.big").Length + _install.ArchivesAddedByMods.Count(n => n.StartsWith(family, StringComparison.OrdinalIgnoreCase));
        var game = manifest.Files.Where(f => vfs.Find(f.Path)?.Family == ArchiveFamily.Game).ToList();
        var inxp = manifest.Files.Except(game).ToList();
        int Needed(List<PackFile> files) => files.Count == 0 ? 0 :
            files.Count(f => f.Path.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
            + (files.Any(f => f.Path.EndsWith(".cube", StringComparison.OrdinalIgnoreCase)) ? 1 : 0)
            + (files.Any(f => !f.Path.EndsWith(".db", StringComparison.OrdinalIgnoreCase) && !f.Path.EndsWith(".cube", StringComparison.OrdinalIgnoreCase)) ? 1 : 0);
        var i = Count("patch_inxp") + (isUpdate ? 0 : Needed(inxp));
        var g = Count("patch_game") + (isUpdate ? 0 : Needed(game));
        return (Math.Max(i, g), i, g);
    }

    /// <summary>Mod archives the manager did not make that hold any of these paths.</summary>
    public IReadOnlyList<OtherOverlap> OtherArchiveOverlaps(IReadOnlySet<string> paths)
    {
        var owned = OwnedArchives();
        var found = new List<OtherOverlap>();
        foreach (var name in ModInstaller.InstalledMods(_install).Where(n => !owned.Contains(n)))
        {
            try
            {
                using var archive = EbArchive.Open(Path.Combine(_install.Databigs, name));
                var shared = archive.Entries.Select(e => e.Path).Where(paths.Contains).Distinct().OrderBy(p => p, StringComparer.Ordinal).ToList();
                if (shared.Count > 0)
                    found.Add(new OtherOverlap(name, shared));
            }
            catch (Exception e) when (e is IOException or EbFormatException or InvalidDataException)
            {
            }
        }
        return found;
    }

    // ---- installing

    public PackInstallResult Install(string packPath, VirtualFileSystem vfs, PackInstallOptions options,
        IProgress<(string Stage, int Done, int Total)>? progress = null)
    {
        var manifest = ModPack.ReadManifest(packPath);
        var check = Check(manifest, vfs, options.Position);
        if (check.Blocked)
            throw new InstallBlockedException(string.Join(" ", check.Lines.Where(l => l.Level == CheckLevel.Blocked).Select(l => $"{l.Title}. {l.Detail}")));
        if (_state.Packs.FirstOrDefault(p => p.Slug == manifest.Slug && p.Id != manifest.Id) is { } clash)
            throw new InstallBlockedException($"Another installed pack, {clash.Name}, already uses the name {manifest.Slug}.");
        if (options.InstallRoster && manifest.Roster != null && options.SaveData == null)
            throw new ArgumentException("Installing the pack's roster needs the emulator's savedata folder.");

        var existing = _state.Packs.FirstOrDefault(p => p.Id == manifest.Id);
        var store = StoreFolder(manifest.Id);
        var installed = Directory.Exists(store) ? ModProject.Open(store).Installed.ToList() : [];
        Directory.CreateDirectory(Path.Combine(Folder, "store"));
        ModPack.Unpack(packPath, store, progress);
        var project = ModProject.Open(store);
        project.Installed.AddRange(installed);
        project.Save();

        foreach (var other in options.RemoveOthers)
        {
            if (OwnedArchives().Contains(other))
                throw new InstallBlockedException($"{other} belongs to another pack. Change the load order instead.");
            ModInstaller.RemoveOther(_install, other);
        }

        var roster = existing?.Roster;
        if (options.InstallRoster && manifest.Roster != null)
        {
            var slot = options.Slot ?? existing?.Roster?.Slot ?? RosterSlots.FirstFree(options.SaveData!)
                ?? throw new InstallBlockedException("All nine custom roster slots from RosterCustom1 are taken. Pick one to replace.");
            var placed = RosterSlots.Install(options.SaveData!, slot, Path.Combine(store, ModPack.RosterFolder), manifest.Slug);
            // An update into the same slot keeps the backup from before the first version, so uninstall goes back to that.
            roster = existing?.Roster is { } earlier && earlier.Slot == slot && earlier.SaveData == placed.SaveData
                ? placed with { Backup = earlier.Backup } : placed;
        }

        var entry = new InstalledPack(manifest.Id, manifest.Slug, manifest.Name, manifest.Author, manifest.Version, existing?.Enabled ?? true,
            DateTimeOffset.Now, null, roster);
        if (existing != null)
            _state.Packs[_state.Packs.IndexOf(existing)] = entry;
        else
            _state.Packs.Insert(Math.Clamp(options.Position ?? 0, 0, _state.Packs.Count), entry);
        Save();

        var warnings = Deploy(vfs, progress);
        var mine = ModProject.Open(store).Installed.Select(i => i.FileName).ToList();
        return new PackInstallResult(Find(manifest.Id)!, mine, warnings);
    }

    public IReadOnlyList<string> SetEnabled(string id, bool enabled, VirtualFileSystem vfs)
    {
        var pack = Find(id) ?? throw new ArgumentException($"No installed pack is called {id}.");
        _state.Packs[_state.Packs.IndexOf(pack)] = pack with { Enabled = enabled };
        Save();
        return Deploy(vfs);
    }

    /// <summary>Moves a pack to a place in the priority list (0 is the top) and rebuilds what changed.</summary>
    public IReadOnlyList<string> Move(string id, int position, VirtualFileSystem vfs)
    {
        var pack = Find(id) ?? throw new ArgumentException($"No installed pack is called {id}.");
        _state.Packs.Remove(pack);
        _state.Packs.Insert(Math.Clamp(position, 0, _state.Packs.Count), pack);
        Save();
        return Deploy(vfs);
    }

    /// <summary>
    /// Removes a pack's archives and store copy, and takes its roster back out. When the roster was saved over in game since install,
    /// nothing is done and <see cref="RosterRemoval.ChangedSinceInstall"/> comes back, so the player can choose: force it, or keep it.
    /// </summary>
    public PackUninstallResult Uninstall(string id, VirtualFileSystem vfs, bool forceRoster = false, bool keepRoster = false)
    {
        var pack = Find(id) ?? throw new ArgumentException($"No installed pack is called {id}.");
        RosterRemoval? removal = null;
        if (pack.Roster != null && !keepRoster)
        {
            removal = RosterSlots.Remove(pack.Roster, pack.Slug, forceRoster);
            if (removal == RosterRemoval.ChangedSinceInstall)
                return new PackUninstallResult(false, [$"The roster in {pack.Roster.Slot} was saved over in game since install."], removal);
            _state.Packs[_state.Packs.IndexOf(pack)] = pack = pack with { Roster = null };
            Save();
        }

        var store = StoreFolder(pack.Id);
        var problems = Directory.Exists(store) ? ModInstaller.Uninstall(ModProject.Open(store), _install) : [];
        if (problems.Count > 0)
            return new PackUninstallResult(false, problems, removal);

        if (Directory.Exists(store))
            Directory.Delete(store, true);
        _state.Packs.Remove(pack);
        Save();
        var warnings = Deploy(vfs);
        return new PackUninstallResult(true, warnings, removal);
    }

    /// <summary>
    /// Works out which pack wins each file and rebuilds any pack whose share changed. Disabled packs have no archives. Files another
    /// mod archive holds are left to it. Returns the builder's warnings.
    /// </summary>
    public IReadOnlyList<string> Deploy(VirtualFileSystem vfs, IProgress<(string Stage, int Done, int Total)>? progress = null)
    {
        var warnings = new List<string>();
        var all = _state.Packs.Where(p => p.Enabled).SelectMany(p => StoredManifest(p.Id).Files.Select(f => f.Path)).ToHashSet(StringComparer.Ordinal);
        var theirs = OtherArchiveOverlaps(all).SelectMany(o => o.Paths).ToHashSet(StringComparer.Ordinal);
        var claimed = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < _state.Packs.Count; i++)
        {
            var pack = _state.Packs[i];
            var store = StoreFolder(pack.Id);
            var project = ModProject.Open(store);
            var share = pack.Enabled
                ? project.Changes.Where(c => !claimed.Contains(c.Path) && !theirs.Contains(c.Path)).ToList()
                : [];
            claimed.UnionWith(share.Select(c => c.Path));
            var key = share.Count == 0 ? "" : ModPack.Sha256(Encoding.UTF8.GetBytes(string.Join('\n', share.OrderBy(c => c.Path, StringComparer.Ordinal).Select(c => c.Path + ":" + c.Blob))));
            var present = project.Installed.All(a => File.Exists(Path.Combine(_install.Databigs, a.FileName)));
            if (key == pack.Deployed && present)
                continue;

            if (share.Count == 0)
            {
                var problems = ModInstaller.Uninstall(project, _install);
                warnings.AddRange(problems);
            }
            else
            {
                var buildFolder = Path.Combine(store, "build");
                var build = OverlayBuilder.Build(project, vfs, buildFolder, progress, only: share.Select(c => c.Path).ToHashSet(StringComparer.Ordinal));
                warnings.AddRange(build.Warnings);
                ModInstaller.Install(project, _install, build);
                Directory.Delete(buildFolder, true);
            }
            _state.Packs[i] = pack with { Deployed = key };
            Save();
        }
        return warnings;
    }

    private void Save()
    {
        if (_state.Packs.Count == 0)
        {
            // Back to stock: nothing of the manager's is left beside the game. Only what the manager made is deleted.
            var store = Path.Combine(Folder, "store");
            if (Directory.Exists(store) && !Directory.EnumerateFileSystemEntries(store).Any())
                Directory.Delete(store);
            File.Delete(Path.Combine(Folder, "packs.json"));
            if (Directory.Exists(Folder) && !Directory.EnumerateFileSystemEntries(Folder).Any())
                Directory.Delete(Folder);
            return;
        }
        Directory.CreateDirectory(Folder);
        var file = Path.Combine(Folder, "packs.json");
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(_state, ModPack.Json));
        File.Move(file + ".tmp", file, overwrite: true);
    }
}

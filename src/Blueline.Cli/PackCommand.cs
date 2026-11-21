using Blueline.Core;
using Blueline.Core.Packs;
using Blueline.Formats.Eb;

/// <summary>
/// blueline pack-*: export a project as a .blpack, and install, order, enable, disable and uninstall packs in a game. Only
/// pack-install, pack-order, pack-enable, pack-disable and pack-uninstall write to the game folder, and only their own
/// patch_*_99_pk-* archives and CUSA11126-packs. A roster save is written only with --savedata, and never RosterCustom0.
/// </summary>
internal static class PackCommand
{
    public const string Usage = """
        blueline pack-export <project> <game> <out.blpack> [options]   Make a mod pack of a project's changes
            --version <v>  --description <text>  --name <text>  --author <text>
            --cover <png> --icon <png> --shot <png> (repeatable, up to 6)   pictures for the Mods page
            --roster <saveFolder>          include a roster save, e.g. ...\savedata\CUSA11126\RosterCustom1
        blueline pack-info <pack>                      What a pack holds
        blueline pack-check <pack> <game>              Game id and version, stock files, conflicts, other mods
        blueline pack-install <pack> <game> [--savedata <folder> [--slot RosterCustomN]] [--remove <archive>]...
                                                       Install or update; the roster only with --savedata (never RosterCustom0)
        blueline pack-list <game>                      Installed packs, top wins
        blueline pack-order <game> <slug> <position>   Move a pack (1 is the top)
        blueline pack-enable <game> <slug>  |  pack-disable <game> <slug>
        blueline pack-uninstall <game> <slug> [--force-roster | --keep-roster]
        blueline pack-slots <savedata>                 Roster slots and their in-game names
        """;

    public static int Run(string[] a)
    {
        try
        {
            return Dispatch(a);
        }
        catch (Exception e) when (e is PackFormatException or InstallBlockedException or BuildFailedException or IOException
            or InvalidDataException or InvalidOperationException or ArgumentException or EbFormatException)
        {
            Console.Error.WriteLine($"Error: {e.Message}");
            return 1;
        }
    }

    private static string? Option(string[] a, string name) => Array.IndexOf(a, name) is var i and >= 0 && i + 1 < a.Length ? a[i + 1] : null;

    private static IEnumerable<string> Options(string[] a, string name)
    {
        for (var i = 0; i < a.Length - 1; i++)
            if (a[i] == name)
                yield return a[i + 1];
    }

    private static VirtualFileSystem Mount(string game) => VirtualFileSystem.Mount(GameInstall.Open(game));

    private static int Dispatch(string[] a)
    {
        switch (a[0])
        {
            case "pack-export" when a.Length >= 4:
                {
                    var project = ModProject.Open(a[1]);
                    using var vfs = Mount(a[2]);
                    PackPicture? Picture(string? file) => file == null ? null : new PackPicture(File.ReadAllBytes(file));
                    var options = new PackExportOptions
                    {
                        Name = Option(a, "--name"),
                        Author = Option(a, "--author"),
                        Version = Option(a, "--version") ?? PackExporter.LoadInfo(project)?.Version ?? "1.0.0",
                        Description = Option(a, "--description") ?? PackExporter.LoadInfo(project)?.Description ?? project.Notes,
                        Cover = Picture(Option(a, "--cover")),
                        Icon = Picture(Option(a, "--icon")),
                        Shots = [.. Options(a, "--shot").Select(f => new PackPicture(File.ReadAllBytes(f)))],
                        RosterFolder = Option(a, "--roster"),
                    };
                    var result = PackExporter.Export(project, vfs, options, a[3]);
                    var m = result.Manifest;
                    Console.WriteLine($"Wrote {result.Path}  {result.PackedBytes:N0} bytes");
                    Console.WriteLine($"  {m.Name} {m.Version} ({m.Slug})  {m.Files.Count} files, {m.Files.Count(f => f.Stock == null)} new  game {m.Game.TitleId} {m.Game.AppVersion}");
                    if (m.Roster is { } roster)
                        Console.WriteLine($"  roster: \"{roster.Title}\" from {roster.From}, account id blanked");
                    foreach (var left in m.LeftOut)
                        Console.WriteLine("  left out: " + left);
                    return 0;
                }
            case "pack-info" when a.Length == 2:
                {
                    var m = ModPack.ReadManifest(a[1]);
                    Console.WriteLine($"{m.Name} {m.Version} by {m.Author}  ({m.Slug}, id {m.Id})");
                    Console.WriteLine($"  made with Blueline {m.Blueline} on {m.Game.TitleId} {m.Game.AppVersion}, {m.Created:yyyy-MM-dd}");
                    if (m.Description.Length > 0)
                        Console.WriteLine("  " + m.Description);
                    Console.WriteLine($"  art: {(m.Art.Cover != null ? "cover " : "")}{(m.Art.Icon != null ? "icon " : "")}{m.Art.Shots.Count} screenshot(s)");
                    Console.WriteLine($"  {m.Files.Count} files, {m.Bytes:N0} bytes");
                    foreach (var f in m.Files)
                        Console.WriteLine($"    {(f.Stock == null ? "new " : "    ")}{f.Path}  {f.Size:N0}");
                    if (m.Roster is { } roster)
                        Console.WriteLine($"  roster: \"{roster.Title}\" ({roster.Files.Count} files)");
                    foreach (var left in m.LeftOut)
                        Console.WriteLine("  left out: " + left);
                    return 0;
                }
            case "pack-check" when a.Length == 3:
                {
                    using var vfs = Mount(a[2]);
                    var check = new PackManager(GameInstall.Open(a[2])).Check(a[1], vfs);
                    Print(check);
                    return check.Blocked ? 2 : 0;
                }
            case "pack-install" when a.Length >= 3:
                {
                    var install = GameInstall.Open(a[2]);
                    using var vfs = Mount(a[2]);
                    var manager = new PackManager(install);
                    var check = manager.Check(a[1], vfs);
                    Print(check);
                    var saveData = Option(a, "--savedata");
                    var result = manager.Install(a[1], vfs, new PackInstallOptions
                    {
                        SaveData = saveData,
                        InstallRoster = saveData != null && check.Manifest.Roster != null,
                        Slot = Option(a, "--slot"),
                        RemoveOthers = [.. Options(a, "--remove")],
                    });
                    foreach (var archive in result.Archives)
                        Console.WriteLine("Installed " + archive);
                    foreach (var warning in result.Warnings)
                        Console.WriteLine("  warning: " + warning);
                    if (result.Pack.Roster is { } r)
                        Console.WriteLine($"Roster in {r.Slot}" + (r.Backup != null ? $"; the save that was there is in {r.Backup}" : ""));
                    else if (check.Manifest.Roster != null)
                        Console.WriteLine("The pack has a roster; it was not installed (pass --savedata to install it).");
                    return 0;
                }
            case "pack-list" when a.Length == 2:
                {
                    var manager = new PackManager(GameInstall.Open(a[1]));
                    if (manager.Packs.Count == 0)
                        Console.WriteLine("No packs installed.");
                    for (var i = 0; i < manager.Packs.Count; i++)
                    {
                        var p = manager.Packs[i];
                        Console.WriteLine($"{i + 1}. {p.Name} {p.Version} ({p.Slug}){(p.Enabled ? "" : "  disabled")}{(p.Roster != null ? $"  roster in {p.Roster.Slot}" : "")}");
                    }
                    return 0;
                }
            case "pack-order" when a.Length == 4:
                {
                    using var vfs = Mount(a[1]);
                    new PackManager(GameInstall.Open(a[1])).Move(a[2], int.Parse(a[3]) - 1, vfs);
                    return Dispatch(["pack-list", a[1]]);
                }
            case "pack-enable" or "pack-disable" when a.Length == 3:
                {
                    using var vfs = Mount(a[1]);
                    new PackManager(GameInstall.Open(a[1])).SetEnabled(a[2], a[0] == "pack-enable", vfs);
                    return Dispatch(["pack-list", a[1]]);
                }
            case "pack-uninstall" when a.Length >= 3:
                {
                    using var vfs = Mount(a[1]);
                    var result = new PackManager(GameInstall.Open(a[1])).Uninstall(a[2], vfs, a.Contains("--force-roster"), a.Contains("--keep-roster"));
                    foreach (var problem in result.Problems)
                        Console.WriteLine((result.Done ? "  warning: " : "Not done: ") + problem);
                    if (result.Roster == RosterRemoval.ChangedSinceInstall)
                        Console.WriteLine("Pass --force-roster to remove it anyway (it is kept in the backup folder), or --keep-roster to leave it.");
                    if (result.Roster is RosterRemoval.Restored)
                        Console.WriteLine("The roster that was in the slot before is back.");
                    else if (result.Roster is RosterRemoval.Removed)
                        Console.WriteLine("The pack's roster was moved to the backup folder.");
                    if (result.Done)
                        Console.WriteLine("Uninstalled.");
                    return result.Done ? 0 : 2;
                }
            case "pack-slots" when a.Length == 2:
                foreach (var slot in RosterSlots.List(a[1]))
                    Console.WriteLine($"{slot.Name}  \"{slot.Title}\"{(slot.Name == RosterSlots.Reserved ? "  (Puck Peak's; packs never write it)" : "")}");
                Console.WriteLine("First free slot for a pack: " + (RosterSlots.FirstFree(a[1]) ?? "none"));
                return 0;
            default:
                Console.WriteLine(Usage);
                return 1;
        }
    }

    private static void Print(PackCheck check)
    {
        var m = check.Manifest;
        Console.WriteLine($"{m.Name} {m.Version} by {m.Author}{(check.IsUpdate ? "  (update of the installed version)" : "")}");
        foreach (var line in check.Lines)
            Console.WriteLine($"  [{(line.Level == CheckLevel.Ok ? " ok " : line.Level == CheckLevel.Warning ? "warn" : "STOP")}] {line.Title}{(line.Detail.Length > 0 ? ". " + line.Detail : "")}");
        foreach (var c in check.Conflicts.Take(20))
            Console.WriteLine($"    {c.Path}: {c.Winner} wins over {string.Join(", ", c.Losers)}");
        foreach (var o in check.Others)
            Console.WriteLine($"    {o.Archive} keeps {o.Paths.Count} file(s), e.g. {o.Paths[0]} (--remove {o.Archive} to let the pack win)");
    }
}

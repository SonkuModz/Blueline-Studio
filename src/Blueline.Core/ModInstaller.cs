using System.Security.Cryptography;
using Blueline.Formats.Eb;

namespace Blueline.Core;

public sealed class InstallBlockedException(string message) : Exception(message);

/// <summary>Another installed mod archive that holds some of the same files as a build.</summary>
public sealed record InstallOverlap(string FileName, int Shared, IReadOnlyList<string> Examples);

/// <summary>
/// Copies built overlays into databigs and removes only what it installed. It never opens an original
/// archive for writing, so uninstalling restores the stock game exactly.
/// </summary>
public static class ModInstaller
{
    /// <summary>The eboot fills a fixed table of 50 path buffers per glob and no bounds check was found.</summary>
    public const int MaxArchivesPerGlob = 50;

    public static string? InstallBlocker(GameInstall install, BuildResult build)
    {
        foreach (var family in new[] { "patch_inxp", "patch_game" })
        {
            var existing = Directory.GetFiles(install.Databigs, family + "*.big").Select(Path.GetFileName)
                .Concat(install.ArchivesAddedByMods.Where(n => n.StartsWith(family, StringComparison.OrdinalIgnoreCase)))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var incoming = build.Archives.Count(a => a.FileName.StartsWith(family, StringComparison.OrdinalIgnoreCase) && !existing.Contains(a.FileName));
            if (existing.Count + incoming > MaxArchivesPerGlob)
                return $"{family}: {existing.Count + incoming} archives would exceed the game's limit of {MaxArchivesPerGlob}.";
        }
        var space = new DriveInfo(Path.GetPathRoot(install.Databigs)!).AvailableFreeSpace;
        var needed = build.Archives.Sum(a => a.Size);
        return space < needed + (1L << 30) ? $"The drive has {space >> 20} MB free. This install needs {needed >> 20} MB plus a 1 GB margin." : null;
    }

    public static void Install(ModProject project, GameInstall install, BuildResult build)
    {
        if (InstallBlocker(install, build) is { } blocker)
            throw new InstallBlockedException(blocker);

        foreach (var archive in build.Archives)
        {
            if (!GameInstall.IsModArchive(archive.FileName))
                throw new InstallBlockedException($"{archive.FileName} is not a mod archive name, so it was not written.");

            var target = Path.Combine(install.Databigs, archive.FileName);
            if (File.Exists(target) && project.Installed.All(i => !i.FileName.Equals(archive.FileName, StringComparison.OrdinalIgnoreCase)))
                throw new InstallBlockedException($"{archive.FileName} already exists and was not installed by this project.");

            File.Copy(archive.Path, target + ".tmp", overwrite: true);
            File.Move(target + ".tmp", target, overwrite: true);
            project.Installed.RemoveAll(i => i.FileName.Equals(archive.FileName, StringComparison.OrdinalIgnoreCase));
            project.Installed.Add(new InstalledArchive(archive.FileName, archive.Size, archive.Sha256));
            project.Save();
        }

        // A family this build no longer produces must not leave its old archive in place.
        foreach (var stale in project.Installed.Where(i => build.Archives.All(a => !a.FileName.Equals(i.FileName, StringComparison.OrdinalIgnoreCase))).ToList())
            RemoveTracked(project, install, stale);
    }

    /// <summary>Removes this project's archives. A file that changed since install is left in place and reported.</summary>
    public static IReadOnlyList<string> Uninstall(ModProject project, GameInstall install)
    {
        var problems = new List<string>();
        foreach (var archive in project.Installed.ToList())
            if (RemoveTracked(project, install, archive) is { } problem)
                problems.Add(problem);
        return problems;
    }

    public static bool IsInstalled(ModProject project, GameInstall install) =>
        project.Installed.Any(i => File.Exists(Path.Combine(install.Databigs, i.FileName)));

    /// <summary>
    /// Other installed mod archives that hold any of the files this build holds. When two overlays hold the same file the
    /// game can draw it wrong (seen in game as black ad boards), so only one should be installed.
    /// </summary>
    public static IReadOnlyList<InstallOverlap> Overlaps(ModProject project, GameInstall install, BuildResult build)
    {
        var mine = new HashSet<string>(StringComparer.Ordinal);
        foreach (var built in build.Archives)
            using (var archive = EbArchive.Open(built.Path))
                foreach (var entry in archive.Entries)
                    mine.Add(entry.Path);
        var own = project.Installed.Select(i => i.FileName).Concat(build.Archives.Select(a => a.FileName)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var found = new List<InstallOverlap>();
        foreach (var name in InstalledMods(install).Where(n => !own.Contains(n)))
        {
            List<string> shared;
            try
            {
                using var archive = EbArchive.Open(Path.Combine(install.Databigs, name));
                shared = [.. archive.Entries.Select(e => e.Path).Where(mine.Contains).Distinct().OrderBy(p => p, StringComparer.Ordinal)];
            }
            catch (Exception e) when (e is IOException or EbFormatException or InvalidDataException)
            {
                continue;
            }
            if (shared.Count > 0)
                found.Add(new InstallOverlap(name, shared.Count, [.. shared.Take(3)]));
        }
        return found;
    }

    /// <summary>Deletes another mod's archive from the game folder. Only mod archive names are touched; its project can install it again.</summary>
    public static void RemoveOther(GameInstall install, string fileName)
    {
        if (!GameInstall.IsModArchive(fileName) || Path.GetFileName(fileName) != fileName)
            throw new InstallBlockedException($"{fileName} is not a mod archive name, so it was left in place.");
        File.Delete(Path.Combine(install.Databigs, fileName));
    }

    /// <summary>Archive names from all installed mods, for the install summary.</summary>
    public static IReadOnlyList<string> InstalledMods(GameInstall install) =>
        Directory.GetFiles(install.Databigs, "*.big").Select(p => Path.GetFileName(p)!).Where(GameInstall.IsModArchive).OrderBy(n => n, StringComparer.Ordinal).ToList();

    private static string? RemoveTracked(ModProject project, GameInstall install, InstalledArchive archive)
    {
        if (!GameInstall.IsModArchive(archive.FileName))
            return $"{archive.FileName} is not a mod archive name, so it was left in place.";

        var target = Path.Combine(install.Databigs, archive.FileName);
        if (File.Exists(target))
        {
            using (var stream = File.OpenRead(target))
                if (!Convert.ToHexStringLower(SHA256.HashData(stream)).Equals(archive.Sha256, StringComparison.OrdinalIgnoreCase))
                    return $"{archive.FileName} changed since install, so it was left in place.";
            File.Delete(target);
        }
        project.Installed.Remove(archive);
        project.Save();
        return null;
    }
}

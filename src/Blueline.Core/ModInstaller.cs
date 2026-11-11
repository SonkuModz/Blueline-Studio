using System.Security.Cryptography;

namespace Blueline.Core;

public sealed class InstallBlockedException(string message) : Exception(message);

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

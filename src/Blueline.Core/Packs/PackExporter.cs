using System.Text.Json;
using Blueline.Formats.Sony;
using Blueline.Formats.Tdb;
using Blueline.Formats.Textures;

namespace Blueline.Core.Packs;

/// <summary>A picture for the pack, and where the crop should centre (0 to 1 across and down).</summary>
public sealed record PackPicture(byte[] Png, double FocusX = 0.5, double FocusY = 0.5);

public sealed record PackExportOptions
{
    public string? Name { get; init; }
    public string? Author { get; init; }
    public string Version { get; init; } = "1.0.0";
    public string Description { get; init; } = "";
    public PackPicture? Cover { get; init; }
    public PackPicture? Icon { get; init; }
    public IReadOnlyList<PackPicture> Shots { get; init; } = [];
    /// <summary>A roster save folder to include, such as RosterCustom1. Null leaves the roster out.</summary>
    public string? RosterFolder { get; init; }
}

/// <summary>What export would do: which files go in, which are dropped and why. Nothing is written.</summary>
public sealed record PackExportPlan(IReadOnlyList<PackFile> Files, IReadOnlyList<string> LeftOut, int SameAsStock, PackGame Game);

public sealed record PackExportResult(string Path, PackManifest Manifest, long PackedBytes);

/// <summary>What a project remembers between exports, in its folder as pack.json.</summary>
public sealed record ProjectPackInfo(string Id, string Version, string Description);

/// <summary>Turns a project into a .blpack. Stock hashes come from the install alone, never the mods folder.</summary>
public static class PackExporter
{
    private const string InfoFile = "pack.json";

    public static string BluelineVersion
    {
        get
        {
            var v = typeof(PackExporter).Assembly.GetName().Version ?? new Version(0, 0, 0);
            return $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
        }
    }

    public static ProjectPackInfo? LoadInfo(ModProject project)
    {
        var path = Path.Combine(project.Folder, InfoFile);
        return File.Exists(path) ? JsonSerializer.Deserialize<ProjectPackInfo>(File.ReadAllText(path), ModPack.Json) : null;
    }

    public static PackGame ReadGame(GameInstall install)
    {
        var sfo = Path.Combine(install.Root, "sce_sys", "param.sfo");
        if (!File.Exists(sfo))
            return new PackGame("CUSA11126", "unknown");
        var values = ParamSfo.Parse(File.ReadAllBytes(sfo));
        return new PackGame(values["TITLE_ID"] ?? "CUSA11126", values["APP_VER"] ?? "unknown");
    }

    /// <summary>
    /// Runs <paramref name="work"/> on the game without its mods folder. A mods folder that replaces an archive by name (Puck Peak's
    /// inxp_00.big, boot.big) hides the install's copy from a normal mount, so stock hashes must come from a mount of the install alone.
    /// </summary>
    public static T WithStock<T>(VirtualFileSystem vfs, Func<VirtualFileSystem, T> work)
    {
        if (vfs.Install.ModsFolder == null)
            return work(vfs);
        using var stock = VirtualFileSystem.Mount(GameInstall.Open(vfs.Install.Root, useModsFolder: false));
        return work(stock);
    }

    public static PackExportPlan Plan(ModProject project, VirtualFileSystem vfs, CancellationToken cancel = default) =>
        WithStock(vfs, stock => PlanOnStock(project, stock, cancel));

    private static PackExportPlan PlanOnStock(ModProject project, VirtualFileSystem vfs, CancellationToken cancel)
    {
        var files = new List<PackFile>();
        var leftOut = new List<string>();
        var same = 0;
        foreach (var change in project.Changes.OrderBy(c => c.Path, StringComparer.Ordinal))
        {
            cancel.ThrowIfCancellationRequested();
            var layers = vfs.Layers(change.Path);
            if (layers.Count > 0 && layers.All(l => l.Family == ArchiveFamily.Boot))
            {
                leftOut.Add($"{change.Path} (boot.big content cannot load from a patch archive)");
                continue;
            }
            if (vfs.Find(change.Path)?.IsLoose == true)
            {
                leftOut.Add($"{change.Path} (a loose file, which the builder cannot replace)");
                continue;
            }
            var stock = vfs.ReadStock(change.Path) is { } bytes ? ModPack.Sha256(bytes) : null;
            if (stock == change.Blob)
            {
                same++;
                continue;
            }
            files.Add(new PackFile(change.Path, change.Blob, change.Size, stock, change.Description));
        }
        return new PackExportPlan(files, leftOut, same, ReadGame(vfs.Install));
    }

    public static PackExportResult Export(ModProject project, VirtualFileSystem vfs, PackExportOptions options, string destination,
        IProgress<(string Stage, int Done, int Total)>? progress = null, CancellationToken cancel = default)
    {
        var plan = Plan(project, vfs, cancel);
        if (plan.Files.Count == 0 && options.RosterFolder == null)
            throw new InvalidOperationException("The project has no changes a pack can carry.");

        var info = LoadInfo(project) ?? new ProjectPackInfo(Guid.NewGuid().ToString(), options.Version, options.Description);
        var name = string.IsNullOrWhiteSpace(options.Name) ? project.Name : options.Name.Trim();

        var art = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        string? Picture(PackPicture? picture, string entry, (int Width, int Height) size)
        {
            if (picture == null)
                return null;
            art[entry] = Fit(picture, size.Width, size.Height);
            return entry;
        }
        if (options.Shots.Count > ModPack.MaxShots)
            throw new ArgumentException($"A pack holds at most {ModPack.MaxShots} screenshots.");
        var packArt = new PackArt(
            Picture(options.Cover, "art/cover.png", ModPack.CoverSize),
            Picture(options.Icon, "art/icon.png", ModPack.IconSize),
            [.. options.Shots.Select((s, i) => Picture(s, $"art/shot{i + 1}.png", ModPack.ShotSize)!)]);

        var saves = new List<PackSave>();
        var saveFiles = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (options.RosterFolder is { } rosterFolder)
        {
            var (files, title) = ReadRoster(rosterFolder);
            foreach (var (fileName, content) in files)
                saveFiles[fileName] = content;
            saves.Add(new PackSave("roster", Path.GetFileName(Path.TrimEndingDirectorySeparator(rosterFolder)), ModPack.RosterFolder, title,
                files.ToDictionary(f => f.Key, f => ModPack.Sha256(f.Value), StringComparer.Ordinal)));
        }

        var manifest = new PackManifest(ModPack.Format, info.Id, ModPack.MakeSlug(name), name,
            string.IsNullOrWhiteSpace(options.Author) ? project.Author : options.Author.Trim(), options.Version.Trim(), options.Description.Trim(),
            packArt, DateTimeOffset.Now, BluelineVersion, plan.Game, plan.Files, saves, plan.LeftOut);

        var byHash = project.Changes.GroupBy(c => c.Blob).ToDictionary(g => g.Key, g => g.First());
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        ModPack.Write(destination, manifest, hash => project.ReadBlob(byHash[hash]), art, saveFiles, progress);

        var saved = info with { Version = manifest.Version, Description = manifest.Description };
        File.WriteAllText(Path.Combine(project.Folder, InfoFile), JsonSerializer.Serialize(saved, ModPack.Json));
        return new PackExportResult(destination, manifest, new FileInfo(destination).Length);
    }

    /// <summary>A roster save's files by relative name, with the account id blanked, and its in-game title.</summary>
    public static (IReadOnlyDictionary<string, byte[]> Files, string Title) ReadRoster(string folder)
    {
        RosterSave.Read(folder); // proves DATA, INDEX, the size tail and the frame are sound
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
            files[Path.GetRelativePath(folder, file).Replace('\\', '/')] = File.ReadAllBytes(file);
        var title = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
        if (files.TryGetValue("sce_sys/param.sfo", out var sfo))
        {
            ParamSfo.Clear(sfo, "ACCOUNT_ID");
            title = ParamSfo.Parse(sfo)["SUBTITLE"] is { Length: > 0 } subtitle ? subtitle : title;
        }
        return (files, title);
    }

    /// <summary>Crops a picture to the target shape around its focus point and scales it to the exact size, as PNG.</summary>
    public static byte[] Fit(PackPicture picture, int width, int height)
    {
        var source = PngFile.Read(picture.Png);
        var target = (double)width / height;
        int cropWidth = source.Width, cropHeight = source.Height;
        if ((double)source.Width / source.Height > target)
            cropWidth = Math.Max(1, (int)Math.Round(source.Height * target));
        else
            cropHeight = Math.Max(1, (int)Math.Round(source.Width / target));
        var left = (int)Math.Round(Math.Clamp(picture.FocusX, 0, 1) * (source.Width - cropWidth));
        var top = (int)Math.Round(Math.Clamp(picture.FocusY, 0, 1) * (source.Height - cropHeight));

        var cropped = new byte[cropWidth * cropHeight * 4];
        for (var y = 0; y < cropHeight; y++)
            Buffer.BlockCopy(source.Pixels, ((top + y) * source.Width + left) * 4, cropped, y * cropWidth * 4, cropWidth * 4);
        var image = new RgbaImage(cropWidth, cropHeight, cropped);
        if (cropWidth != width || cropHeight != height)
            image = TexturePipeline.Resize(image, width, height);
        return PngFile.Write(image, System.IO.Compression.CompressionLevel.Optimal);
    }
}

using System.Collections.Concurrent;
using BCnEncoder.Encoder;
using Blueline.Formats.Textures;

namespace Blueline.Core;

public enum BatchOutcome { Imported, NoMatch, Ambiguous, Unsupported, Failed }

public sealed record BatchPlanItem(string Source, string? Target, BatchOutcome Outcome, string Detail);

/// <summary>
/// Imports a folder of images. A file matches by its relative path inside the folder when that path exists in
/// the game, otherwise by base name when exactly one texture has it. Ambiguous names are reported, never guessed.
/// </summary>
public static class BatchTextureImport
{
    private static readonly string[] Extensions = [".png", ".dds", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff"];

    public static IReadOnlyList<BatchPlanItem> Plan(string folder, VirtualFileSystem vfs)
    {
        var byBase = vfs.Files.Where(f => f.Extension is ".gnf" or ".dds" && !f.IsLoose)
            .GroupBy(f => Path.GetFileNameWithoutExtension(f.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var plan = new List<BatchPlanItem>();
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                     .Where(f => Extensions.Contains(Path.GetExtension(f).ToLowerInvariant())).OrderBy(f => f, StringComparer.Ordinal))
        {
            var relative = Path.ChangeExtension(Path.GetRelativePath(folder, file).Replace('\\', '/'), null);
            var exact = new[] { relative + ".gnf", relative + ".dds" }.Select(vfs.Find).FirstOrDefault(f => f != null);
            if (exact != null)
            {
                plan.Add(new BatchPlanItem(file, exact.Path, BatchOutcome.Imported, "Matched by path"));
                continue;
            }
            var stem = Path.GetFileNameWithoutExtension(file);
            if (!byBase.TryGetValue(stem, out var matches))
                plan.Add(new BatchPlanItem(file, null, BatchOutcome.NoMatch, "No texture has this name"));
            else if (matches.Count > 1)
                plan.Add(new BatchPlanItem(file, null, BatchOutcome.Ambiguous, $"{matches.Count} textures have this name; mirror the game path to choose one"));
            else
                plan.Add(new BatchPlanItem(file, matches[0].Path, BatchOutcome.Imported, "Matched by name"));
        }
        return plan;
    }

    /// <summary>Encodes in parallel, then applies everything to the project as one undo step.</summary>
    public static IReadOnlyList<BatchPlanItem> Run(string folder, VirtualFileSystem vfs, ModProject project, CompressionQuality quality,
        Func<string, RgbaImage>? loader = null, IProgress<(int Done, int Total)>? progress = null, CancellationToken cancel = default)
    {
        loader ??= ImageLoader.Load;
        var plan = Plan(folder, vfs);
        var results = new BatchPlanItem[plan.Count];
        var staged = new ConcurrentBag<(string, byte[], string)>();
        var done = 0;

        Parallel.For(0, plan.Count, new ParallelOptions { CancellationToken = cancel, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, i =>
        {
            var item = plan[i];
            results[i] = item.Target == null ? item : ImportOne(item, vfs, staged, quality, loader);
            progress?.Report((Interlocked.Increment(ref done), plan.Count));
        });

        project.SetMany(staged.ToList(), $"Batch import from {Path.GetFileName(folder.TrimEnd('/', '\\'))}");
        return results;
    }

    private static BatchPlanItem ImportOne(BatchPlanItem item, VirtualFileSystem vfs, ConcurrentBag<(string, byte[], string)> staged, CompressionQuality quality, Func<string, RgbaImage> loader)
    {
        try
        {
            var original = vfs.ReadContent(vfs.Find(item.Target!)!);
            if (TexturePipeline.ImportBlocker(original) is { } blocker)
                return item with { Outcome = BatchOutcome.Unsupported, Detail = blocker };

            var imported = TexturePipeline.Import(original, loader(item.Source), quality);
            staged.Add((item.Target!, imported.Gnf, $"Texture from {Path.GetFileName(item.Source)}"));
            return item with { Detail = imported.Resized ? "Imported, resized to match" : "Imported" };
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return item with { Outcome = BatchOutcome.Failed, Detail = e.Message };
        }
    }
}

/// <summary>Loads source images in the formats the core can read without a UI toolkit.</summary>
public static class ImageLoader
{
    public static RgbaImage Load(string path)
    {
        var data = File.ReadAllBytes(path);
        if (PngFile.IsPng(data))
            return PngFile.Read(data);
        if (DdsFile.IsDds(data))
        {
            var texture = GameTexture.TryOpen(data)!;
            if (texture.Blocker is { } blocker)
                throw new NotSupportedException(blocker);
            return texture.DecodeMip(0);
        }
        throw new NotSupportedException($"{Path.GetFileName(path)} is not a PNG or DDS file.");
    }
}

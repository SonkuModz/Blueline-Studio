namespace Blueline.Core.Packs;

/// <summary>Plain-language groups for a pack's files, for the chips on the Mods and Export pages.</summary>
public static class PackSummary
{
    public static string GroupOf(string path)
    {
        var p = path.ToLowerInvariant();
        if (p.EndsWith(".db")) return p.Contains("/loc/") ? "Text" : "Database";
        if (p.Contains("jersey") || p.Contains("uniform") || p.Contains("/kits/")) return "Jerseys";
        if (p.Contains("/ice") || p.Contains("rink") || p.Contains("centreice") || p.Contains("centerice")) return "Rinks";
        if (p.Contains("logo")) return "Logos";
        if (p.Contains("/bgs/") || p.Contains("background")) return "Menu art";
        if (p.EndsWith(".cube") || p.Contains("/postfx/") || p.Contains("lighting")) return "Look";
        return AssetCategories.Of(path) switch
        {
            AssetCategory.Texture => "Textures",
            AssetCategory.Audio => "Audio",
            AssetCategory.Video => "Video",
            AssetCategory.Model => "Models",
            AssetCategory.Interface => "Interface",
            AssetCategory.Text => "Text",
            AssetCategory.Database => "Database",
            _ => "Other",
        };
    }

    public static IReadOnlyList<(string Group, int Count)> Groups(IEnumerable<string> paths) =>
        paths.GroupBy(GroupOf).Select(g => (g.Key, g.Count())).OrderByDescending(g => g.Item2).ThenBy(g => g.Key, StringComparer.Ordinal).ToList();
}

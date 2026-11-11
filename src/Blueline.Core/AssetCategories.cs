namespace Blueline.Core;

public enum AssetCategory { Texture, Model, Audio, Database, Text, Video, Interface, Animation, Other }

/// <summary>
/// A quick grouping from the extension, used to filter 150,000 paths without reading any of them. The
/// detail panels identify real content from its bytes, since NHL 19's extensions are not always honest.
/// </summary>
public static class AssetCategories
{
    private static readonly Dictionary<string, AssetCategory> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".gnf"] = AssetCategory.Texture,
        [".dds"] = AssetCategory.Texture,
        [".png"] = AssetCategory.Texture,
        [".jpg"] = AssetCategory.Texture,
        [".jpeg"] = AssetCategory.Texture,
        [".mcd"] = AssetCategory.Model,
        [".sbr"] = AssetCategory.Audio,
        [".sbs"] = AssetCategory.Audio,
        [".db"] = AssetCategory.Database,
        [".xml"] = AssetCategory.Text,
        [".json"] = AssetCategory.Text,
        [".lua"] = AssetCategory.Text,
        [".csv"] = AssetCategory.Text,
        [".txt"] = AssetCategory.Text,
        [".ini"] = AssetCategory.Text,
        [".cfg"] = AssetCategory.Text,
        [".lreq"] = AssetCategory.Text,
        [".layout"] = AssetCategory.Text,
        [".menu"] = AssetCategory.Text,
        [".vvm"] = AssetCategory.Text,
        [".nav"] = AssetCategory.Text,
        [".mkv"] = AssetCategory.Video,
        [".vp6"] = AssetCategory.Video,
        [".gfx"] = AssetCategory.Interface,
        [".swf"] = AssetCategory.Interface,
        [".ttf"] = AssetCategory.Interface,
        [".cba"] = AssetCategory.Animation,
    };

    public static AssetCategory Of(string path) =>
        ByExtension.GetValueOrDefault(Path.GetExtension(path), AssetCategory.Other);
}

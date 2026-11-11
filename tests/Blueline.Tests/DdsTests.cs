using System.Buffers.Binary;
using Blueline.Core;
using Blueline.Formats.Textures;

namespace Blueline.Tests;

/// <summary>The DDS shapes the game ships: greyscale, float, cubemaps and volumes.</summary>
[TestClass]
public sealed class DdsTests
{
    private static VirtualFileSystem? _vfs;

    [ClassInitialize]
    public static void Mount(TestContext _)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null && _vfs == null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "NHL 19", "CUSA11126");
            if (File.Exists(Path.Combine(candidate, "eboot.bin")))
                _vfs = VirtualFileSystem.Mount(GameInstall.Open(candidate, useModsFolder: false));
        }
    }

    [ClassCleanup]
    public static void Unmount() => _vfs?.Dispose();

    private static byte[] Luminance(int width, int height, byte[] values)
    {
        var data = new byte[128 + values.Length];
        "DDS "u8.CopyTo(data);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(12), height);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(16), width);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(80), 0x20000);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(88), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(92), 0xFF);
        values.CopyTo(data, 128);
        return data;
    }

    [TestMethod]
    public void Luminance_decodes_to_grey()
    {
        var texture = GameTexture.TryOpen(Luminance(2, 1, [10, 200]))!;
        Assert.IsNull(texture.Blocker);
        CollectionAssert.AreEqual(new byte[] { 10, 10, 10, 255, 200, 200, 200, 255 }, texture.DecodeMip(0).Pixels);
    }

    [TestMethod]
    public void Luminance_import_keeps_a_grey_picture_exact_and_the_header()
    {
        var original = Luminance(2, 1, [0, 0]);
        var result = TexturePipeline.Import(original, new RgbaImage(2, 1, [37, 37, 37, 255, 251, 251, 251, 255]));
        Assert.AreEqual(original.Length, result.Gnf.Length);
        CollectionAssert.AreEqual(original[..128], result.Gnf[..128]);
        CollectionAssert.AreEqual(new byte[] { 37, 251 }, result.Gnf[128..]);
    }

    [TestMethod]
    public void Every_dds_in_the_game_decodes_every_layer_and_only_flat_ones_import()
    {
        if (_vfs == null)
            Assert.Inconclusive("The game is not here.");

        var checkedFiles = 0;
        foreach (var file in _vfs.Files.Where(f => f.Path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)))
        {
            var content = _vfs.ReadContent(file);
            var texture = GameTexture.TryOpen(content)!;
            Assert.IsNull(texture.Blocker, file.Path);
            for (var layer = 0; layer < texture.Faces; layer++)
                Assert.AreEqual(texture.Width * texture.Height * 4, texture.DecodeMip(0, layer).Pixels.Length, file.Path);
            if (texture.Faces > 1)
                Assert.IsNotNull(TexturePipeline.ImportBlocker(content), file.Path);
            checkedFiles++;
        }
        Assert.IsGreaterThan(30, checkedFiles);
    }

    [TestMethod]
    public void Cubemap_faces_differ_and_the_last_face_ends_the_file()
    {
        if (_vfs == null)
            Assert.Inconclusive("The game is not here.");

        var dds = DdsFile.Parse(_vfs.ReadContent(_vfs.Find("nhl/data/common/rendering/indoorsky.dds")!));
        Assert.AreEqual(6, dds.Layers);
        var last = dds.Mip(dds.MipCount - 1, 5);
        CollectionAssert.AreEqual(dds.Data[^last.Length..], last);
        CollectionAssert.AreNotEqual(dds.Mip(0, 0), dds.Mip(0, 1));
    }

    [TestMethod]
    public void Volume_last_slice_ends_the_file()
    {
        if (_vfs == null)
            Assert.Inconclusive("The game is not here.");

        var dds = DdsFile.Parse(_vfs.ReadContent(_vfs.Find("nhl/data/common/postfx/colourcube_gamma.dds")!));
        Assert.AreEqual(32, dds.Layers);
        var last = dds.Mip(0, 31);
        CollectionAssert.AreEqual(dds.Data[^last.Length..], last);
    }

    [TestMethod]
    public void Float_lookup_table_previews_and_refuses_import()
    {
        if (_vfs == null)
            Assert.Inconclusive("The game is not here.");

        var content = _vfs.ReadContent(_vfs.Find("nhl/data/common/rendering/brdfggxlut.dds")!);
        var red = GameTexture.TryOpen(content)!.DecodeMip(0).Pixels.Where((_, i) => i % 4 == 0).ToArray();
        Assert.IsGreaterThan(50, red.Max() - red.Min());
        Assert.IsNotNull(TexturePipeline.ImportBlocker(content));
    }
}

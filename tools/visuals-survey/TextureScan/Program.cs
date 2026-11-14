using Blueline.Core;
using Blueline.Formats.Textures;
using System.Text;

var game = args[0];
var outDir = args[1];
Directory.CreateDirectory(outDir);
var install = GameInstall.Open(game, useModsFolder: false);
using var vfs = VirtualFileSystem.Mount(install);
var files = vfs.Files.Where(f => !f.IsFromModsFolder).OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
Console.WriteLine($"stock files: {files.Count}");

using (var sp = new StreamWriter(Path.Combine(outDir, "stock_paths.tsv"), false, new UTF8Encoding(false)))
    foreach (var f in files) sp.WriteLine($"{f.SourceName}	{f.StoredSize}	{f.Path}");
using var gnf = new StreamWriter(Path.Combine(outDir, "gnf_info.tsv"), false, new UTF8Encoding(false));
gnf.WriteLine("path\tarchive\tbytes\twidth\theight\tdepth\tmips\tfaces\tcodec\tnumfmt\ttile\tdim");
int ok = 0, bad = 0, dds = 0;
foreach (var f in files)
{
    if (f.Extension != ".gnf") continue;
    try
    {
        var head = vfs.ReadHead(f, 0x100);
        if (head.Length >= 4 && head[0] == (byte)'D' && head[1] == (byte)'D' && head[2] == (byte)'S')
        {
            dds++;
            gnf.WriteLine($"{f.Path}\t{f.SourceName}\t{f.StoredSize}\t\t\t\t\t\tDDS\t\t\t");
            continue;
        }
        var i = GnfTexture.ReadInfo(head);
        gnf.WriteLine($"{f.Path}\t{f.SourceName}\t{f.StoredSize}\t{i.Width}\t{i.Height}\t{i.Depth}\t{i.MipCount}\t{i.Faces}\t{i.Codec}\t{i.NumberFormatName}\t{i.TileMode}\t{i.Dimension}");
        ok++;
    }
    catch (Exception e)
    {
        bad++;
        gnf.WriteLine($"{f.Path}\t{f.SourceName}\t{f.StoredSize}\t\t\t\t\t\tERR {e.Message.Replace('\t', ' ')}\t\t\t");
    }
}
Console.WriteLine($"gnf ok {ok}, dds-in-gnf {dds}, bad {bad}");

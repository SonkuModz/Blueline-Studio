using BCnEncoder.Encoder;
using Blueline.Core;
using Blueline.Core.Emulator;
using Blueline.Core.Teams;
using Blueline.Formats.Eb;
using Blueline.Formats.Tdb;

// Batch front end over the same core as the app. Reads the game and writes into project folders; only
// `install` and `uninstall` touch the game folder, and only this project's new overlay archives.
const string Usage = """
    blueline audit <game>                        Check every archive's structure and path hashes
    blueline find <game> <text>                  List paths containing text
    blueline extract <game> <path> <outfile>     Write a file's real content
    blueline new <project> <name>                Create a project
    blueline set <project> <path> <file>         Use a file's bytes for a game path
    blueline text <project> <game> <dict> <key> <text>   Change a string, e.g. dict nhl/data/kettle/loc/eng_us.db
    blueline textures <project> <game> <folder>  Import a folder of images
    blueline model-info <game> <path>            List a model's shapes with bounds, UV range and vertex format
    blueline merge <project> <game> <mod>       Add another project, .big or folder of .big files; conflicts stay as they are
    blueline sportsnet <project> <game> <logo.png>   Apply the Sportsnet look to the score clock (layout and SN logo)
    blueline flat-scorebug <project> <game> [asset ...]   Take the shading out of the score clock (textures only, nothing moves)
    blueline build <project> <game>              Build and verify overlay archives in <project>/build
    blueline install <project> <game>            Build, then add the overlay archives to the game folder
    blueline uninstall <project> <game>          Remove this project's overlay archives from the game folder
    blueline text-show <game> <dict> <key>       Show the string the game would load
    blueline texture-png <game> <path> <outfile> Write a game texture as PNG
    blueline jersey-maps <game> <jersey _cm path> <outfolder> [mask image]   Make normal and coeff maps as PNGs
    blueline db-tables <game> <database>      List a database's tables and fields
    blueline db-show <game> <database> <table> [rows]   Print a table as tab-separated text
    blueline team-list <game> [league]           List teams: league, art id, abbreviation, name, file name, assets, arena
    blueline team-assets <game> <team>           List everything filed under a team
    blueline team-logos <project> <game> <team> <image>   Put one PNG or DDS into all of a team's square logos
    blueline shad-config <config.toml|.json> [key=value ...]   Show or change shadPS4 settings in that file
    """;

try
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    return Run(args);
}
catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or BuildFailedException or KeyNotFoundException or ArgumentException or EbFormatException or MergeRefusedException)
{
    Console.Error.WriteLine($"Error: {e.Message}");
    return 1;
}

int Run(string[] a)
{
    switch (a.FirstOrDefault())
    {
        case "audit" when a.Length == 2:
            {
                using var vfs = Mount(a[1]);
                var clean = true;
                foreach (var archive in vfs.Archives)
                {
                    var audit = EbAudit.Check(archive);
                    Console.WriteLine($"{archive.Name,-26} {audit.Entries,7} entries  {(audit.Clean ? "ok" : "PROBLEMS")}");
                    foreach (var problem in audit.Problems)
                        Console.WriteLine("    " + problem);
                    clean &= audit.Clean;
                }
                Console.WriteLine($"{vfs.Files.Count:N0} merged paths");
                return clean ? 0 : 2;
            }
        case "find" when a.Length == 3:
            {
                using var vfs = Mount(a[1]);
                foreach (var file in vfs.Files.Where(f => f.Path.Contains(a[2], StringComparison.OrdinalIgnoreCase)).OrderBy(f => f.Path, StringComparer.Ordinal))
                    Console.WriteLine($"{file.SourceName,-24} {file.StoredSize,12:N0}  {file.Path}");
                return 0;
            }
        case "extract" when a.Length == 4:
            {
                using var vfs = Mount(a[1]);
                var file = vfs.Find(a[2]) ?? throw new KeyNotFoundException($"No such path: {a[2]}");
                File.WriteAllBytes(a[3], vfs.ReadContent(file));
                return 0;
            }
        case "new" when a.Length == 3:
            ModProject.Create(a[1], a[2]);
            return 0;
        case "set" when a.Length == 4:
            ModProject.Open(a[1]).Set(a[2], File.ReadAllBytes(a[3]), $"From {Path.GetFileName(a[3])}");
            return 0;
        case "text" when a.Length == 6:
            {
                var project = ModProject.Open(a[1]);
                using var vfs = Mount(a[2]);
                var workspace = new Workspace(new SessionLog());
                workspace.SetGame(vfs);
                workspace.SetProject(project);
                var dictionary = new LocDictionary(workspace.ReadCurrent(a[3]) ?? throw new KeyNotFoundException($"No such path: {a[3]}"));
                dictionary.SetText(a[4], a[5]);
                project.Set(a[3], dictionary.Save(), $"Text of {a[4]}");
                return 0;
            }
        case "textures" when a.Length == 4:
            {
                var project = ModProject.Open(a[1]);
                using var vfs = Mount(a[2]);
                var results = BatchTextureImport.Run(a[3], vfs, project, CompressionQuality.Balanced);
                foreach (var result in results)
                    Console.WriteLine($"{result.Outcome,-12} {Path.GetFileName(result.Source)} -> {result.Target ?? "-"}  {result.Detail}");
                return results.Any(r => r.Outcome == BatchOutcome.Failed) ? 2 : 0;
            }
        case "model-info" when a.Length == 3:
            {
                using var vfs = Mount(a[1]);
                var model = Blueline.Formats.Models.McdFile.Parse(vfs.ReadContent(vfs.Find(a[2]) ?? throw new KeyNotFoundException($"No such path: {a[2]}")));
                for (var f = 0; f < model.Formats.Count; f++)
                    Console.WriteLine($"format {f}: stride {model.Formats[f].Stride}  {model.Formats[f].Text}");
                for (var s = 0; s < model.Streams.Count; s++)
                    Console.WriteLine($"stream {s}: {model.Streams[s].Count} vertices, stride {model.Streams[s].Stride}, format {(model.Streams[s].Format == null ? "none" : model.Formats.ToList().IndexOf(model.Streams[s].Format!).ToString())}");
                foreach (var shape in model.Shapes)
                {
                    var stream = model.Streams[shape.Stream];
                    var p = model.ReadPositions(stream);
                    var uv = model.ReadUvs(shape);
                    var uvText = uv == null ? "no uv" : $"uv {uv.Min(t => t.X):F2}..{uv.Max(t => t.X):F2} {uv.Min(t => t.Y):F2}..{uv.Max(t => t.Y):F2}";
                    Console.WriteLine($"{shape.Name,-34} {shape.Triangles,7} tris  x {p.Min(v => v.X):F2}..{p.Max(v => v.X):F2} y {p.Min(v => v.Y):F2}..{p.Max(v => v.Y):F2} z {p.Min(v => v.Z):F2}..{p.Max(v => v.Z):F2}  {uvText}  [{stream.Format?.Text ?? $"unknown, stride {stream.Stride}"}]");
                }
                return 0;
            }
        case "merge" when a.Length == 4:
            {
                var project = ModProject.Open(a[1]);
                using var vfs = Mount(a[2]);
                var plan = ModMerge.Analyze(a[3], project, vfs);
                foreach (var item in plan.Items)
                    Console.WriteLine($"{item.Kind,-14} {item.Size,12:N0}  {item.Path}");
                foreach (var warning in plan.Warnings)
                    Console.WriteLine("warning: " + warning);
                var stored = ModMerge.Store(plan, plan.Default, project, vfs);
                project.AddStored(stored, $"Added {stored.Count} files from {plan.SourceName}");
                Console.WriteLine($"Added {stored.Count} files from {plan.SourceName}. The project now has {project.ChangeCount}.");
                return 0;
            }
        case "sportsnet" when a.Length == 4:
            {
                // The same edit as the Sportsnet look button: restyle the score clock layout and put the SN logo into its texture.
                var project = ModProject.Open(a[1]);
                using var vfs = Mount(a[2]);
                byte[]? Read(string p) => project.TryGet(p, out var change) ? project.ReadBlob(change) : vfs.Find(p) is { } f ? vfs.ReadContent(f) : null;
                var catalog = Blueline.Core.Overlays.OverlayCatalog.Build(Read, vfs.Files.Select(f => f.Path));
                const string layoutPath = Blueline.Core.Overlays.ScoreClockScene.LayoutPath;
                var document = Blueline.Core.Overlays.LayoutDocument.Parse(Read(layoutPath) ?? throw new KeyNotFoundException(layoutPath));
                Blueline.Core.Overlays.SportsnetBug.Apply(document);
                var logoPath = catalog.AssetPath(Blueline.Core.Overlays.SportsnetBug.LogoAsset) ?? throw new KeyNotFoundException("The texture for the SN logo was not found.");
                var logo = Blueline.Formats.Textures.TexturePipeline.Import(Read(logoPath) ?? throw new KeyNotFoundException(logoPath),
                    Blueline.Formats.Textures.PngFile.Read(File.ReadAllBytes(a[3])));
                project.SetMany([(layoutPath, document.ToBytes(), "Sportsnet look"), (logoPath, logo.Gnf, "Overlay texture: SN logo for the Sportsnet look")], "Sportsnet look");
                Console.WriteLine($"Changed {layoutPath} and {logoPath}");
                return 0;
            }
        case "flat-scorebug" when a.Length >= 3:
            {
                // Replaces the score clock's shading textures with flat ones. The layout is not touched.
                var project = ModProject.Open(a[1]);
                using var vfs = Mount(a[2]);
                byte[]? Read(string p) => project.TryGet(p, out var change) ? project.ReadBlob(change) : vfs.Find(p) is { } f ? vfs.ReadContent(f) : null;
                var catalog = Blueline.Core.Overlays.OverlayCatalog.Build(Read, vfs.Files.Select(f => f.Path));
                // With asset names after the game folder, only those are replaced (flat colour ones are only done this way).
                var only = a.Skip(3).ToList();
                var items = Blueline.Core.Overlays.FlatScorebug.Build(catalog, Read, only: only.Count > 0 ? only : null);
                project.SetMany(items, "Flat scorebug");
                foreach (var item in items)
                    Console.WriteLine(item.Path);
                Console.WriteLine($"{items.Count} textures changed");
                return 0;
            }
        case "build" when a.Length == 3:
            {
                var project = ModProject.Open(a[1]);
                using var vfs = Mount(a[2]);
                var build = OverlayBuilder.Build(project, vfs, project.BuildFolder);
                foreach (var archive in build.Archives)
                    Console.WriteLine($"{archive.Path}  {archive.Files} files  {archive.Size:N0} bytes  verified");
                foreach (var warning in build.Warnings)
                    Console.WriteLine("warning: " + warning);
                return 0;
            }
        case "install" when a.Length == 3:
            {
                var project = ModProject.Open(a[1]);
                using var vfs = Mount(a[2]);
                var build = OverlayBuilder.Build(project, vfs, project.BuildFolder);
                vfs.Dispose();
                ModInstaller.Install(project, GameInstall.Open(a[2]), build);
                foreach (var archive in build.Archives)
                    Console.WriteLine($"Installed {archive.FileName}");
                return 0;
            }
        case "uninstall" when a.Length == 3:
            {
                var problems = ModInstaller.Uninstall(ModProject.Open(a[1]), GameInstall.Open(a[2]));
                foreach (var problem in problems)
                    Console.WriteLine("left in place: " + problem);
                return problems.Count == 0 ? 0 : 2;
            }
        case "text-show" when a.Length == 4:
            {
                using var vfs = Mount(a[1]);
                var file = vfs.Find(a[2]) ?? throw new KeyNotFoundException($"No such path: {a[2]}");
                var row = new LocDictionary(vfs.ReadContent(file)).Rows.FirstOrDefault(r => r.Key == a[3])
                    ?? throw new KeyNotFoundException($"No such key: {a[3]}");
                Console.WriteLine($"{file.SourceName}: {row.Text}");
                return 0;
            }
        case "texture-png" when a.Length == 4:
            {
                using var vfs = Mount(a[1]);
                var file = vfs.Find(a[2]) ?? throw new KeyNotFoundException($"No such path: {a[2]}");
                var texture = Blueline.Formats.Textures.GameTexture.TryOpen(vfs.ReadContent(file)) ?? throw new InvalidDataException($"{a[2]} is not a texture this tool can read.");
                File.WriteAllBytes(a[3], Blueline.Formats.Textures.PngFile.Write(texture.DecodeMip(0)));
                Console.WriteLine(texture.Summary);
                return 0;
            }
        case "jersey-maps" when a.Length is 4 or 5:
            {
                using var vfs = Mount(a[1]);
                var sheets = JerseySheets.FromColour(a[2]) ?? throw new ArgumentException($"{a[2]} is not a jersey colour sheet (jersey_<brand>_<team>_<kit>_cm.gnf).");
                Directory.CreateDirectory(a[3]);
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var normalBase = JerseyMaps.PlainBase(vfs, sheets.Brand, "nm");
                var coeffBase = JerseyMaps.PlainBase(vfs, sheets.Brand, "coeff");
                Console.WriteLine($"Plain {sheets.BrandLabel} bases from {JerseyMaps.BaseSources(vfs, sheets.Brand, "nm").Count} sheets in {clock.Elapsed.TotalSeconds:F1}s");
                clock.Restart();
                var (w, h) = (normalBase.Width, normalBase.Height);
                var options = new JerseyMapOptions();
                var areas = a.Length == 5
                    ? JerseyMaps.FromPicture(ImageLoader.Load(a[4]), w, h)
                    : JerseyMaps.FromColour(ReadTexture(vfs, sheets.Colour), options.Tolerance, w, h);
                var normal = JerseyMaps.Normal(normalBase, areas, options);
                var coeff = JerseyMaps.Coeff(coeffBase, areas, options);
                Console.WriteLine($"Maps made in {clock.Elapsed.TotalSeconds:F1}s");
                foreach (var (name, image) in new[] { ("base_nm", normalBase), ("base_coeff", coeffBase), ("mask", JerseyMaps.MaskImage(JerseyMaps.HeightOf(areas, options.InnerDetail), w, h)), ("nm", normal), ("coeff", coeff) })
                    File.WriteAllBytes(Path.Combine(a[3], name + ".png"), Blueline.Formats.Textures.PngFile.Write(image));
                return 0;
            }
        case "db-tables" when a.Length == 3:
            {
                using var vfs = Mount(a[1]);
                var db = OpenDatabase(vfs, a[2]);
                foreach (var table in db.Tables.OrderBy(t => t.Label, StringComparer.OrdinalIgnoreCase))
                    Console.WriteLine($"{table.Label,-40} {table.Count,7} rows  {string.Join(' ', table.Fields.Select(f => f.Label))}");
                return 0;
            }
        case "db-show" when a.Length is 4 or 5:
            {
                using var vfs = Mount(a[1]);
                var db = OpenDatabase(vfs, a[2]);
                var table = db.Tables.FirstOrDefault(t => t.Label == a[3] || t.ShortName == a[3]) ?? throw new KeyNotFoundException($"No such table: {a[3]}");
                var limit = a.Length == 5 ? int.Parse(a[4]) : table.Count;
                var fields = table.Fields.Where(f => !f.IsPoolPointer).OrderBy(f => f.Label, StringComparer.OrdinalIgnoreCase).ToList();
                Console.WriteLine(string.Join('\t', fields.Select(f => f.Label)));
                for (var row = 0; row < Math.Min(limit, table.Count); row++)
                    Console.WriteLine(string.Join('\t', fields.Select(f => table.Read(row, f))));
                return 0;
            }
        case "team-list" when a.Length is 2 or 3:
            {
                using var vfs = Mount(a[1]);
                var catalog = OpenTeams(vfs);
                foreach (var team in catalog.Teams.Where(t => a.Length == 2 || t.League.Equals(a[2], StringComparison.OrdinalIgnoreCase)))
                    Console.WriteLine($"{team.League}\t{team.ArtId}\t{team.Abbreviation}\t{team.Name}\t{team.Slug ?? "-"}\t{catalog.ArtOf(team).Count}\t{team.ArenaName}");
                return 0;
            }
        case "team-assets" when a.Length == 3:
            {
                using var vfs = Mount(a[1]);
                var catalog = OpenTeams(vfs);
                var team = catalog.Find(a[2]) ?? throw new KeyNotFoundException($"No such team: {a[2]}");
                Console.WriteLine($"{team.Name} ({team.League}), art id {team.ArtId}, file name {team.Slug ?? "none"}");
                foreach (var art in catalog.ArtOf(team))
                    Console.WriteLine($"{TeamCatalog.CategoryNames[art.Category]}\t{art.Section}\t{(art.IsTechnical ? "map" : "")}\t{art.Path}");
                return 0;
            }
        case "team-logos" when a.Length == 5:
            {
                var project = ModProject.Open(a[1]);
                using var vfs = Mount(a[2]);
                var catalog = OpenTeams(vfs);
                var team = catalog.Find(a[3]) ?? throw new KeyNotFoundException($"No such team: {a[3]}");
                var result = TeamLogoImport.Run(catalog.ArtOf(team), ImageLoader.Load(a[4]), p => vfs.Find(p) is { } f ? vfs.ReadContent(f) : null, project, team.Name, CompressionQuality.Balanced);
                foreach (var item in result.Items)
                    Console.WriteLine($"{item.Outcome,-9} {item.Path}  {item.Detail}");
                Console.WriteLine($"{result.Imported} imported, {result.Skipped} skipped, {result.Failed} failed");
                return result.Failed > 0 ? 2 : 0;
            }
        case "shad-config" when a.Length >= 2:
            {
                var isJson = a[1].EndsWith(".json", StringComparison.OrdinalIgnoreCase);
                IConfigEditor config = isJson ? JsonConfigEditor.Load(a[1]) : TomlEditor.Load(a[1]);
                var settings = isJson ? ShadSettings.Json : ShadSettings.All;
                foreach (var pair in a.Skip(2))
                {
                    var split = pair.IndexOf('=');
                    var setting = split > 0 ? settings.FirstOrDefault(s => s.Key == pair[..split]) : null;
                    if (setting == null)
                        throw new ArgumentException($"Not a setting=value this tool offers: {pair}");
                    ShadSettings.Write(config, setting, pair[(split + 1)..]);
                }
                if (a.Length > 2)
                    config.Save(a[1]);
                foreach (var setting in settings)
                    Console.WriteLine($"{setting.Key,-32} {ShadSettings.Read(config, setting) ?? "(missing)"}");
                return 0;
            }
        default:
            Console.WriteLine(Usage);
            return a.Length == 0 ? 0 : 1;
    }
}

static VirtualFileSystem Mount(string folder) => VirtualFileSystem.Mount(GameInstall.Open(folder));

static Blueline.Formats.Textures.RgbaImage ReadTexture(VirtualFileSystem vfs, string path)
{
    var file = vfs.Find(path) ?? throw new KeyNotFoundException($"No such path: {path}");
    var texture = Blueline.Formats.Textures.GameTexture.TryOpen(vfs.ReadContent(file)) ?? throw new InvalidDataException($"{path} is not a texture this tool can read.");
    return texture.DecodeMip(0);
}

static TeamCatalog OpenTeams(VirtualFileSystem vfs) =>
    TeamCatalog.Load(p => vfs.Find(p) is { } f ? vfs.ReadContent(f) : null, vfs.Files.Select(f => f.Path).ToList());

static TdbFile OpenDatabase(VirtualFileSystem vfs, string path)
{
    var file = vfs.Find(path) ?? throw new KeyNotFoundException($"No such path: {path}");
    var db = TdbFile.Parse(vfs.ReadContent(file));
    var meta = vfs.Find(path[..path.LastIndexOf('/')] + "/nhl_game-meta.xml") ?? vfs.Find("nhl/data/common/gamemodes/database/t3db/nhl_game-meta.xml");
    if (meta != null)
        db.ApplyMeta(System.Text.Encoding.UTF8.GetString(vfs.ReadContent(meta)));
    return db;
}

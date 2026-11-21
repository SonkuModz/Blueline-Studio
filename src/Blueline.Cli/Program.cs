using BCnEncoder.Encoder;
using Blueline.Core;
using Blueline.Core.Emulator;
using Blueline.Core.Franchise;
using Blueline.Core.Roster;
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
    blueline sportsnet <project> <game> [logo.png]   Apply the Sportsnet look to the score clock (layout, and the SN logo when given); redoes it from the stock layout
    blueline flat-scorebug <project> <game> [asset ...]   Take the shading out of the score clock (textures only, nothing moves)
    blueline build <project> <game>              Build and verify overlay archives in <project>/build
    blueline boot-build <project> <game> <out boot.big>   Write a whole boot.big with the project's edits to it (layouts, swf), from the one the game reads
    blueline install <project> <game>           Build, then add the overlay archives to the game folder
    blueline uninstall <project> <game>          Remove this project's overlay archives from the game folder
    blueline text-show <game> <dict> <key>       Show the string the game would load
    blueline texture-png <game> <path> <outfile> Write a game texture as PNG
    blueline jersey-maps <game> <jersey _cm path> <outfolder> [mask image]   Make normal and coeff maps as PNGs
    blueline font-maps <game> <font _cm path> <outfolder>                Make a font's normal and sm maps as PNGs
    blueline font-maps-all <game> <project>                               Make maps for every font colour the project edits
    blueline jersey-refit <game> <from> <to> <colour png> <out png> [<normal png> <out png>]
                                                                          Fit a jersey sheet drawn for one brand (adidas, rbk, nineties) to another's
    blueline db-tables <game> <database>      List a database's tables and fields
    blueline db-show <game> <database> <table> [rows]   Print a table as tab-separated text
    blueline team-list <game> [league]           List teams: league, art id, abbreviation, name, file name, assets, arena
    blueline team-assets <game> <team>           List everything filed under a team
    blueline team-logos <project> <game> <team> <image>   Put one PNG or DDS into all of a team's square logos
    blueline puckpeak <game> <project>          Make a project from the Puck Peak Overtime mod beside the game (overtime-patch.json)
    blueline team <game> <team> [<project> key=value ...]   Show or change everything the database holds for a team, e.g. team.overlayprimarycolor=0C284D
    blueline gear <game> <team> [<project> edit ...]   Show or change a team's glove and helmet colours per kit.
        Edits: gloves:Home=RRGGBB (main colour of the row that kit reads), gloves:Home.z7=RRGGBB (one zone),
        own:helmet:Away (give the kit its own row), row:900=RRGGBB or row:900.z7=RRGGBB (by the row's index field)
    blueline slots <game> <team> [project]       List a team's uniform slots in menu order, and the free kit names a new slot can take
    blueline slot-set <project> <game> <team> <slot> key=value ...   Change a slot (by menu number, 1 first): label=Third (or a
                                                 number), name="Green Test Kit" (its own name in the jersey select), position=3, light=yes|no, default=yes|no
    blueline slot-add <project> <game> <team> <kit name | adidas | rbk | nineties> [jersey=<png>] [copy=<slot>] [menu=<png>] [label=Alternate] [name=<text>] [position=3]
                                                 Add a slot on a free kit name (a brand takes its first free one), writing every file
                                                 it has: the jersey sheet (normal and coeff made from it) and the rest copied from slot <copy>
    blueline slot-remove <project> <game> <team> <slot>   Remove a slot (by menu number)
    blueline players <game> [project | roster save] [team=EDM pos=C name=text min=80 league=0 active=yes]   List players
    blueline player <game> <name | game_id> [<project> key=value ...]   Show or change everything about a player. Keys: bio.lastname,
                                                 ai.c_speed, skatergear.stick, roster.<team>.captain, or jersey=97, archetype=Sniper,
                                                 ratings=+3, move=EDM or move=<from>:<to> (clears his lines there)
    blueline lines <game> <team> [<project> slot=player ...]   Show or set a team's lines, e.g. l1c=McDavid pp1rd=- (empty)
    blueline players-set <project> <game> [filters] -- key=value ...   Bulk edit every player the filters match, as one undo step
    blueline roster-sync <game> <project> <roster save folder> [--write]   List (or write, with --write) the project's database edits into a roster save such as RosterCustom0; a backup is kept
    blueline franchise-immersion <project> <game> [--write]            Put 2025-26 arenas, real NHL insiders and cleaned news text into a project
    blueline roster-league-sync <game> <project> <roster save> [--write]   Copy the project's NHL/AHL club names and AHL affiliates into a roster save (what a new Franchise starts from)
    blueline franchise-info <franchise save folder>                     Show a Franchise save (BeAGM0): file info, blocks and league tables
    blueline franchise-repack <franchise save folder> <out folder>      Write the save unchanged into a new folder (the first in-game test of the writer)
    blueline franchise-sync <game> <source> <franchise save> <out folder>   EXPERIMENTAL: the result crashed the game on load (2026-11-18). Copies team names, AHL affiliates and arenas into a copy of a Franchise save
    blueline shad-config <config.toml|.json> [key=value ...]   Show or change shadPS4 settings in that file
    blueline horns <game> [project]              List the goal horns, the teams whose arena plays each, and the horn bank's samples
    blueline horn-export <game> <project|-> <folder> [sample ...]   Write horn samples as WAV (all of them when none are named)
    blueline horn-set <project> <game> <sample> <audio file>   Put any audio FFmpeg reads into one horn sample's slot
    blueline horn-label <project> <game>         Test bank: every horn sample says its own number (Windows speech)
    blueline faceoff <game> <team> <cache folder>   Download a team's current rink layout from TheFaceoff.net (cached; nothing is written to the game)
    blueline full-ice-leagues <game> <cache folder> <out folder> <nhl,ahl> [--project <folder>] [--model <mcd>] [--default-rest]
                                                 Every team of those leagues from its newest TheFaceoff.net layout (fetched politely,
                                                 built in parallel); --default-rest gives every other slot one shared plain rink
    blueline full-ice <game> <team> <picture | cache folder | plain> <out folder> [--template] [--project <folder>] [--model <mcd>]
                                                 Make a team's full-ice texture (for the full-ice rink model) with a preview PNG:
                                                 from a picture, from TheFaceoff.net (a cache folder), or a plain rink with the
                                                 team's logo. --template stretches a picture drawn to the 2:1 template as it is.
                                                 --project writes it into a project; --model also adds the full-ice model there
    blueline trucolor <game> [league] [options]  Team colours from TruColor (trucolor.net): list leagues, preview a league, apply into a project
                                                 (run "blueline trucolor" alone for its options)
    blueline pack-export|pack-info|pack-check|pack-install|pack-list|pack-order|pack-enable|pack-disable|pack-uninstall|pack-slots
                                                 Mod packs (.blpack): run "blueline pack" alone for their options
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
                    Console.WriteLine($"{archive.Name,-26} {audit.Entries,7} entries  {(audit.GameReady ? "ok" : "PROBLEMS")}");
                    foreach (var problem in audit.Problems.Append(audit.GameProblem).OfType<string>())
                        Console.WriteLine("    " + problem);
                    clean &= audit.GameReady;
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
        case "texture-import" when a.Length == 5:
            {
                // A PNG over one of the game's textures, encoded in that texture's own format and size.
                var project = ModProject.Open(a[1]);
                using var vfs = Mount(a[2]);
                var original = vfs.Find(a[3]) is { } file ? vfs.ReadContent(file) : throw new KeyNotFoundException($"No such path: {a[3]}");
                var result = Blueline.Formats.Textures.TexturePipeline.Import(original, Blueline.Formats.Textures.PngFile.Read(File.ReadAllBytes(a[4])),
                    BCnEncoder.Encoder.CompressionQuality.BestQuality);
                project.Set(a[3], result.Gnf, $"Texture from {Path.GetFileName(a[4])}");
                Console.WriteLine($"{a[3]}  {result.Gnf.Length:N0} bytes{(result.Resized ? "  (resized to fit)" : "")}  {result.Elapsed.TotalSeconds:N1} s");
                return 0;
            }
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
                if (dictionary.TryGet(a[4], out _))
                    dictionary.SetText(a[4], a[5]);
                else
                {
                    dictionary.AddKey(a[4], a[5]);
                    Console.WriteLine($"Added the key {a[4]}; the game had no string with it.");
                }
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
                // A file on disk (say a model from another modder) is read as it is; otherwise the path is a game path.
                Blueline.Formats.Models.McdFile model;
                if (File.Exists(a[2]))
                    model = Blueline.Formats.Models.McdFile.Parse(File.ReadAllBytes(a[2]));
                else
                {
                    using var vfs = Mount(a[1]);
                    model = Blueline.Formats.Models.McdFile.Parse(vfs.ReadContent(vfs.Find(a[2]) ?? throw new KeyNotFoundException($"No such path: {a[2]}")));
                }
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
        case "sportsnet" when a.Length is 3 or 4:
            {
                // The same edit as the Sportsnet look button: restyle the score clock layout and put the SN logo into its texture.
                // The layout is always restyled from the game's own, so running it again redoes the look. Without a logo image the
                // project's logo texture is kept.
                var project = ModProject.Open(a[1]);
                using var vfs = Mount(a[2]);
                byte[]? Read(string p) => project.TryGet(p, out var change) ? project.ReadBlob(change) : vfs.Find(p) is { } f ? vfs.ReadContent(f) : null;
                var catalog = Blueline.Core.Overlays.OverlayCatalog.Build(Read, vfs.Files.Select(f => f.Path));
                const string layoutPath = Blueline.Core.Overlays.ScoreClockScene.LayoutPath;
                var document = Blueline.Core.Overlays.LayoutDocument.Parse(vfs.Find(layoutPath) is { } stock ? vfs.ReadContent(stock) : throw new KeyNotFoundException(layoutPath));
                Blueline.Core.Overlays.SportsnetBug.Apply(document);
                var edits = new List<(string, byte[], string)> { (layoutPath, document.ToBytes(), "Sportsnet look") };
                // The goal animation is code, in nhluidata.swf (which ships in a patch archive, unlike the layout).
                const string uiCode = Blueline.Core.Overlays.ClockScript.UiCode;
                var code = vfs.Find(uiCode) is { } swfFile ? vfs.ReadContent(swfFile) : throw new KeyNotFoundException(uiCode);
                edits.Add((uiCode, Blueline.Core.Overlays.ClockScript.CompileInto(code, "_createScriptAnimGoal", Blueline.Core.Overlays.SportsnetGoal.Script(Blueline.Core.Overlays.SportsnetGoal.GameStretch)), "Sportsnet goal animation"));
                if (a.Length == 4)
                {
                    var logoPath = catalog.AssetPath(Blueline.Core.Overlays.SportsnetBug.LogoAsset) ?? throw new KeyNotFoundException("The texture for the SN logo was not found.");
                    var logo = Blueline.Formats.Textures.TexturePipeline.Import(vfs.Find(logoPath) is { } f ? vfs.ReadContent(f) : throw new KeyNotFoundException(logoPath),
                        Blueline.Formats.Textures.PngFile.Read(File.ReadAllBytes(a[3])));
                    edits.Add((logoPath, logo.Gnf, "Overlay texture: SN logo for the Sportsnet look"));
                }
                project.SetMany(edits, "Sportsnet look");
                Console.WriteLine("Changed " + string.Join(" and ", edits.Select(e => e.Item1)));
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
        case "abc-dump" when a.Length == 5:
            {
                // Lists one method's instructions with names resolved: abc-dump <game> <swf path> <class> <method>
                using var vfs = Mount(a[1]);
                var swf = Blueline.Formats.Flash.SwfFile.Parse(vfs.ReadContent(vfs.Find(a[2]) ?? throw new KeyNotFoundException(a[2])));
                foreach (var tag in swf.Tags.Where(t => t.Code == Blueline.Formats.Flash.SwfTag.DoAbc))
                {
                    var abc = Blueline.Formats.Flash.AbcFile.Parse(tag.Body[Blueline.Formats.Flash.SwfFile.AbcStart(tag.Body).Start..]);
                    var cls = abc.Instances.FirstOrDefault(i => abc.FullNameOf(i.Name) == a[3]);
                    if (cls == null)
                        continue;
                    var body = abc.BodyOf(abc.MethodOf(cls, a[4]));
                    Console.WriteLine($"max_stack {body.MaxStack} locals {body.LocalCount} scope {body.InitScopeDepth}..{body.MaxScopeDepth} code {body.Code.Length} tail {body.Tail.Length}");
                    foreach (var ins in Blueline.Formats.Flash.AbcCode.Decode(body.Code))
                    {
                        var detail = ins.Name switch
                        {
                            "getlex" or "getproperty" or "setproperty" or "initproperty" or "findpropstrict" or "findproperty" or "coerce" or "astype" or "istype" =>
                                $"{abc.FullNameOf(ins.Operands[0])} [#{ins.Operands[0]} kind 0x{abc.Names[ins.Operands[0]].Kind:X2}]",
                            "callproperty" or "callpropvoid" or "constructprop" or "callproplex" =>
                                $"{abc.FullNameOf(ins.Operands[0])} [#{ins.Operands[0]} kind 0x{abc.Names[ins.Operands[0]].Kind:X2}] args {ins.Operands[1]}",
                            "pushstring" => $"\"{abc.Strings[ins.Operands[0]]}\" [#{ins.Operands[0]}]",
                            "pushdouble" => $"{abc.Doubles[ins.Operands[0]]} [#{ins.Operands[0]}]",
                            "pushint" => $"{abc.Ints[ins.Operands[0]]}",
                            _ => string.Join(" ", ins.Operands),
                        };
                        Console.WriteLine($"{ins.Offset,5}  {ins.Name,-16} {detail}");
                    }
                    return 0;
                }
                throw new KeyNotFoundException("No class " + a[3]);
            }
        case "goal-frames" when a.Length == 1:
            {
                // The Sportsnet goal script's values at each frame of goal.mp4 (f1 to f170), as JSON lines, for previews.
                var script = Blueline.Core.Overlays.SportsnetGoal.Script();
                double Field(string f) => f switch
                {
                    "_posWGoalMaskShow" => Blueline.Core.Overlays.SportsnetBug.GoalWidth,
                    "_posWContentsMaskShow" => Blueline.Core.Overlays.SportsnetBug.GoalWidth + 6,
                    _ => 0,
                };
                var nodes = new List<(string Key, Blueline.Core.Overlays.ClockNode Node, string Property, double Before)>
                {
                    ("goal", Blueline.Core.Overlays.SportsnetGoal.Goal, "width", 0),
                    ("white", Blueline.Core.Overlays.SportsnetGoal.Bar(Blueline.Core.Overlays.SportsnetGoal.White), "width", 0),
                    ("blue2", Blueline.Core.Overlays.SportsnetGoal.Bar(Blueline.Core.Overlays.SportsnetGoal.Blue2), "width", 0),
                    ("tint", Blueline.Core.Overlays.SportsnetGoal.Bar(Blueline.Core.Overlays.SportsnetGoal.Tint), "width", 0),
                    ("mask", Blueline.Core.Overlays.SportsnetGoal.Mask, "width", Blueline.Core.Overlays.SportsnetBug.GoalWidth + 6),
                    ("logoAlpha", Blueline.Core.Overlays.SportsnetGoal.Logo, "alpha", 0),
                    ("logoScale", Blueline.Core.Overlays.SportsnetGoal.Logo, "scaleX", 1),
                    ("logoX", Blueline.Core.Overlays.SportsnetGoal.Logo, "x", 0),
                    ("logoY", Blueline.Core.Overlays.SportsnetGoal.Logo, "y", 0),
                    ("streak", Blueline.Core.Overlays.SportsnetGoal.Bar(Blueline.Core.Overlays.SportsnetGoal.Streak), "alpha", 0),
                };
                for (var i = 0; i < 4; i++)
                {
                    nodes.Add(("x" + i, Blueline.Core.Overlays.SportsnetGoal.Letter(i), "x", 0));
                    nodes.Add(("a" + i, Blueline.Core.Overlays.SportsnetGoal.Letter(i), "alpha", 0));
                }
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
                {
                    unit = Blueline.Core.Overlays.SportsnetGoal.Unit, height = Blueline.Core.Overlays.SportsnetBug.Height, goalWidth = Blueline.Core.Overlays.SportsnetBug.GoalWidth,
                    logoX = Blueline.Core.Overlays.SportsnetGoal.LogoX, logoY = Blueline.Core.Overlays.SportsnetGoal.LogoY, logoArt = Blueline.Core.Overlays.SportsnetGoal.LogoArt,
                    streakBox = new[] { Blueline.Core.Overlays.SportsnetGoal.StreakBox.X, Blueline.Core.Overlays.SportsnetGoal.StreakBox.Y, Blueline.Core.Overlays.SportsnetGoal.StreakBox.Width, Blueline.Core.Overlays.SportsnetGoal.StreakBox.Height },
                    letterWidth = Blueline.Core.Overlays.SportsnetGoal.LetterWidth, letterSize = Blueline.Core.Overlays.SportsnetGoal.LetterSize, letterY = Blueline.Core.Overlays.SportsnetGoal.LetterY,
                    letterScaleX = Blueline.Core.Overlays.SportsnetGoal.LetterScaleX, letterScaleY = Blueline.Core.Overlays.SportsnetGoal.LetterScaleY,
                    capTop = Blueline.Core.Overlays.SportsnetGoal.CapTop, capHeight = Blueline.Core.Overlays.SportsnetGoal.CapHeight,
                }));
                for (var f = 1; f <= 170; f++)
                {
                    var frame = Blueline.Core.Overlays.SportsnetGoal.ScriptFrame(f);
                    var values = nodes.ToDictionary(n => n.Key, n => f < Blueline.Core.Overlays.SportsnetGoal.Start ? (n.Key == "mask" ? n.Before : n.Key == "goal" ? 0 : n.Before)
                        : Blueline.Core.Overlays.ClockScript.ValueAt(script, n.Node, n.Property, frame, n.Before, Field));
                    values["f"] = f;
                    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(values));
                }
                return 0;
            }
        case "swf-slow-clock" when a.Length == 3:
            {
                // An in-game test that an edited nhluidata.swf loads: the score clock's slide in takes 120 frames instead of 14.
                using var vfs = Mount(a[1]);
                var swf = Blueline.Formats.Flash.SwfFile.Parse(vfs.ReadContent(vfs.Find("nhl/ui/lib/nhluidata.swf")!));
                foreach (var tag in swf.Tags.Where(t => t.Code == Blueline.Formats.Flash.SwfTag.DoAbc))
                {
                    var start = Blueline.Formats.Flash.SwfFile.AbcStart(tag.Body).Start;
                    var abc = Blueline.Formats.Flash.AbcFile.Parse(tag.Body[start..]);
                    var clock = abc.Instances.FirstOrDefault(i => abc.FullNameOf(i.Name) == "nhl.overlays.scoreclock::ScoreClockOverlay");
                    if (clock == null)
                        continue;
                    var body = abc.BodyOf(abc.MethodOf(clock, "_createScriptAnimClock"));
                    var push = Blueline.Formats.Flash.AbcCode.Decode(body.Code).Single(i => i.Name == "pushbyte" && i.Operands[0] == 14);
                    body.Code[push.Offset + 1] = 120;
                    abc.Changed(body);
                    tag.Body = [.. tag.Body[..start], .. abc.ToBytes()];
                }
                File.WriteAllBytes(a[2], swf.ToBytes());
                Console.WriteLine($"Wrote {a[2]}");
                return 0;
            }
        case "boot-build" when a.Length == 4:
            {
                // boot.big cannot be overridden by a patch archive, so its edits ship as a whole new boot.big.
                var result = BootArchive.Build(ModProject.Open(a[1]), GameInstall.Open(a[2]), a[3]);
                Console.WriteLine($"From {result.Source}");
                foreach (var path in result.Replaced)
                    Console.WriteLine("  replaced " + path);
                Console.WriteLine($"Wrote {result.Destination}  {result.Files} files  {new FileInfo(result.Destination).Length:N0} bytes  verified");
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
        case "font-maps" when a.Length == 4:
            {
                using var vfs = Mount(a[1]);
                var sheets = FontSheets.FromColour(a[2]) ?? throw new ArgumentException($"{a[2]} is not a font colour sheet (font_<brand>_<team>_<kit>_cm.gnf).");
                Directory.CreateDirectory(a[3]);
                var options = new FontMapOptions();
                var layers = FontMaps.Layers(ReadTexture(vfs, sheets.Colour), options.Tolerance, 1024, 1024);
                foreach (var (name, image) in new[] { ("layers", FontMaps.LayersImage(layers)), ("nm", FontMaps.Normal(layers, options)), ("sm", FontMaps.Sm(layers, options)) })
                    File.WriteAllBytes(Path.Combine(a[3], name + ".png"), Blueline.Formats.Textures.PngFile.Write(image));
                Console.WriteLine($"Fill #{layers.FillColour.R:X2}{layers.FillColour.G:X2}{layers.FillColour.B:X2}, bottom row from y {layers.BottomRowStart}");
                return 0;
            }
        case "jersey-refit" when a.Length is 6 or 8:
            {
                using var vfs = Mount(a[1]);
                var map = JerseyRefit.Map(p => vfs.Find(p) is { } f ? vfs.ReadContent(f) : null, a[2], a[3]);
                File.WriteAllBytes(a[5], Blueline.Formats.Textures.PngFile.Write(JerseyRefit.Colour(map, ImageLoader.Load(a[4]))));
                if (a.Length == 8)
                    File.WriteAllBytes(a[7], Blueline.Formats.Textures.PngFile.Write(JerseyRefit.Normal(map, ImageLoader.Load(a[6]))));
                Console.WriteLine($"Fitted from the {a[2]} jersey to the {a[3]} jersey: {a[5]}" + (a.Length == 8 ? $", {a[7]}" : ""));
                return 0;
            }
        case "font-maps-all" when a.Length == 3:
            {
                var project = ModProject.Open(a[2]);
                using var vfs = Mount(a[1]);
                var fonts = project.Changes.Select(c => c.Path).Where(FontSheets.IsColourSheet).ToList();
                var items = new System.Collections.Concurrent.ConcurrentBag<(string, byte[], string)>();
                var done = 0;
                Parallel.ForEach(fonts, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, path =>
                {
                    var colour = Blueline.Formats.Textures.GameTexture.TryOpen(project.ReadBlob(project.Changes.First(c => c.Path == path)))!.DecodeMip(0);
                    foreach (var item in FontMaps.Encode(FontSheets.FromColour(path)!, colour, p => vfs.Find(p) is { } f ? vfs.ReadContent(f) : null,
                                 new FontMapOptions(), BCnEncoder.Encoder.CompressionQuality.Balanced))
                        items.Add(item);
                    var n = Interlocked.Increment(ref done);
                    if (n % 30 == 0 || n == fonts.Count)
                        Console.WriteLine($"{n}/{fonts.Count} fonts");
                });
                project.SetMany(items.ToList(), $"Font maps for {fonts.Count} fonts");
                Console.WriteLine($"Saved {items.Count} maps for {fonts.Count} fonts. The project now has {project.ChangeCount}.");
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
        case "puckpeak" when a.Length == 3:
            {
                var install = GameInstall.Open(a[1]);
                var mod = PuckPeakMod.Find(install) ?? throw new InvalidOperationException($"No {PuckPeakMod.ManifestName} next to {install.Root}.");
                var project = File.Exists(Path.Combine(a[2], "project.json")) ? ModProject.Open(a[2]) : ModProject.Create(a[2], "Puck Peak Overtime");
                Console.WriteLine($"Puck Peak Overtime, applied {mod.Applied}: {mod.FileCount:N0} files in {mod.Entries.Count} archives");
                var last = DateTime.MinValue;
                var result = mod.Copy(project, new Progress<(string Stage, int Done, int Total)>(p =>
                {
                    if ((DateTime.Now - last).TotalSeconds < 5 && p.Done != p.Total)
                        return;
                    last = DateTime.Now;
                    Console.WriteLine($"  {p.Done:N0} of {p.Total:N0}");
                }));
                project.AddStored(result.Changes, "Open Puck Peak Overtime");
                project.Save();
                Console.WriteLine($"{result.Files:N0} files, {result.Bytes / 1048576.0:N0} MB, saved to {project.Folder}");
                foreach (var warning in result.Warnings)
                    Console.WriteLine("Note: " + warning);
                return 0;
            }
        case "gear" when a.Length == 3 || a.Length >= 5:
            {
                using var vfs = Mount(a[1]);
                var catalog = OpenTeams(vfs);
                var team = catalog.Find(a[2]) ?? throw new KeyNotFoundException($"No such team: {a[2]}");
                var project = a.Length >= 5 ? ModProject.Open(a[3]) : null;
                var data = project != null && project.TryGet(TeamCatalog.DatabasePath, out var stored) ? project.ReadBlob(stored) : vfs.ReadContent(vfs.Find(TeamCatalog.DatabasePath)!);
                var file = TdbFile.Parse((byte[])data.Clone());
                file.ApplyMeta(System.Text.Encoding.UTF8.GetString(vfs.ReadContent(vfs.Find(TeamCatalog.MetaPath)!)));
                var gear = GearColours.Open(file);
                var gearTeam = GearColours.GearTeamOf(team) ?? throw new InvalidOperationException($"{team.Name} is not linked to the gear table (NHL clubs only).");
                var kits = gear.KitsOf(team);
                GearKit Kit(string label) => kits.FirstOrDefault(k => k.Label.Equals(label, StringComparison.OrdinalIgnoreCase) || k.Variant.ToString() == label)
                    ?? throw new KeyNotFoundException($"{team.Name} has no kit {label}; kits: {string.Join(", ", kits)}");
                GearPart Part(string name) => Enum.Parse<GearPart>(name, ignoreCase: true);
                GearRow ByIndex(int index) => gear.RowsOf(gearTeam).FirstOrDefault(r => r.Index == index) ?? throw new KeyNotFoundException($"No gear row {index} for {team.Name}");
                int Colour(string hex) => GearColours.TryParseHex(hex, out var rgb) ? rgb : throw new ArgumentException($"Not a colour: {hex}");
                foreach (var edit in a.Skip(4))
                {
                    if (edit.StartsWith("own:", StringComparison.OrdinalIgnoreCase))
                    {
                        var bits = edit.Split(':');
                        var (row, added) = gear.GiveOwnRow(team, Kit(bits[2]), Part(bits[1]));
                        Console.WriteLine($"{edit}: row {row.Index} ({(added ? "added" : "re-pointed")}), variant {row.Variant}");
                        continue;
                    }
                    var (lhs, value) = (edit[..edit.IndexOf('=')], Colour(edit[(edit.IndexOf('=') + 1)..]));
                    var zone = lhs.Contains(".z") ? int.Parse(lhs[(lhs.IndexOf(".z") + 2)..]) : 0;
                    var target = lhs.Contains(".z") ? lhs[..lhs.IndexOf(".z")] : lhs;
                    var targetRow = target.StartsWith("row:", StringComparison.OrdinalIgnoreCase)
                        ? ByIndex(int.Parse(target[4..]))
                        : gear.Resolve(gearTeam, Kit(target.Split(':')[1]), Part(target.Split(':')[0])).Row ?? throw new InvalidOperationException($"{target} reads no row");
                    var changed = zone > 0 ? gear.SetZone(targetRow, zone, value) : gear.SetMain(targetRow, value);
                    Console.WriteLine($"{edit}: row {changed.Index} " + string.Join(' ', changed.Zones.Select(GearColours.Hex)));
                }
                foreach (var kit in kits)
                    foreach (var part in Enum.GetValues<GearPart>())
                    {
                        var r = gear.Resolve(gearTeam, kit, part);
                        Console.WriteLine($"{kit.Label,-26} {part,-7} {r.Link,-8} {(r.Row == null ? "-" : $"row {r.Row.Index} era {r.Row.Era} variant {r.Row.Variant} main {GearColours.Hex(r.Row.Main)}")}");
                    }
                foreach (var row in gear.RowsOf(gearTeam))
                    Console.WriteLine($"row {row.Index,5} {row.Part,-7} era {row.Era} variant {row.Variant} brand {row.Brand}  {string.Join(' ', row.Zones.Select(GearColours.Hex))}");
                Console.WriteLine($"{gear.SpareRows:N0} spare rows");
                if (project != null && a.Length > 4)
                {
                    project.Set(TeamCatalog.DatabasePath, gear.Save(), $"{team.Name} glove and helmet colours");
                    project.Save();
                    Console.WriteLine($"Saved to {project.Folder}");
                }
                return 0;
            }
        case "slots" when a.Length is 3 or 4:
            {
                using var vfs = Mount(a[1]);
                var catalog = OpenTeams(vfs);
                var team = catalog.Find(a[2]) ?? throw new KeyNotFoundException($"No such team: {a[2]}");
                var slots = OpenSlots(vfs, catalog, a.Length == 4 ? ModProject.Open(a[3]) : null).Slots;
                var list = slots.SlotsOf(team);
                var nameProject = a.Length == 4 ? ModProject.Open(a[3]) : null;
                var nameTable = new LocDictionary(nameProject != null ? ReadCurrent(vfs, nameProject, SlotNames.DictionaryPath) : vfs.ReadContent(vfs.Find(SlotNames.DictionaryPath)!));
                Console.WriteLine($"{team.Name}: {list.Count} of {UniformSlots.MaxSlotsPerTeam} slots ({slots.SpareRows} spare rows in the table)");
                for (var i = 0; i < list.Count; i++)
                {
                    var s = list[i];
                    Console.WriteLine($"{i + 1,3}. {s.Label,-16} {s.BrandLabel,-8} {s.KitName ?? $"art {s.ArtId} variant {s.Variant}",-34} jersey {s.JerseyIndex,2}"
                        + (s.IsDefault ? (s.Variant == 0 ? "  default home" : "  default away") : "") + (s.IsLight ? "  light" : "")
                        + (SlotNames.NameOf(nameTable, s) is { } shown ? $"  \"{shown}\"" : "") + (s.ArtId != team.ArtId ? $"  (borrowed: art {s.ArtId})" : "") + (slots.IsDrawable(s) ? "" : "  NOT DRAWN: the vault has no texture pack for it"));
                }
                var free = slots.FreeKits(team);
                Console.WriteLine($"{free.Count} free kit names; first ones:");
                foreach (var f in free.Take(15))
                    Console.WriteLine("     " + f);
                return 0;
            }
        case "slot-set" when a.Length >= 6:
            {
                var project = ModProject.Open(a[1]);
                using var vfs = Mount(a[2]);
                var catalog = OpenTeams(vfs);
                var team = catalog.Find(a[3]) ?? throw new KeyNotFoundException($"No such team: {a[3]}");
                var (slots, _) = OpenSlots(vfs, catalog, project);
                var slot = SlotAt(slots, team, a[4]);
                LocDictionary? names = null;
                foreach (var edit in a.Skip(5))
                {
                    var (key, value) = (edit[..edit.IndexOf('=')].ToLowerInvariant(), edit[(edit.IndexOf('=') + 1)..]);
                    if (key == "name")
                    {
                        names ??= new LocDictionary(ReadCurrent(vfs, project, SlotNames.DictionaryPath));
                        Console.WriteLine($"{edit}: {(SlotNames.SetName(names, slot.Style, slot.ArtId, slot.Variant, value) ? "added" : "changed")} {SlotNames.KeyOf(slot)}");
                        continue;
                    }
                    slot = key switch
                    {
                        "label" => slots.Set(slot, locString: LabelNumber(value)),
                        "light" => slots.Set(slot, light: YesNo(value)),
                        "default" => slots.Set(slot, isDefault: YesNo(value)),
                        "position" => slots.Move(slot, int.Parse(value) - 1),
                        _ => throw new ArgumentException($"Unknown slot setting {key}; use label, position, light or default."),
                    };
                    Console.WriteLine($"{edit}: {slot}");
                }
                var setItems = new List<(string, byte[], string)> { (TeamCatalog.DatabasePath, slots.Save(), $"{team.Name} uniform slots") };
                if (names != null)
                    setItems.Add((SlotNames.DictionaryPath, names.Save(), $"{team.Name} kit name"));
                project.SetMany(setItems, $"{team.Name} uniform slots");
                project.Save();
                Console.WriteLine($"Saved to {project.Folder}");
                return 0;
            }
        case "slot-add" when a.Length >= 5:
            {
                var project = ModProject.Open(a[1]);
                using var vfs = Mount(a[2]);
                var catalog = OpenTeams(vfs);
                var team = catalog.Find(a[3]) ?? throw new KeyNotFoundException($"No such team: {a[3]}");
                var (slots, gear) = OpenSlots(vfs, catalog, project);
                var options = a.Skip(5).Select(o => (Key: o[..o.IndexOf('=')].ToLowerInvariant(), Value: o[(o.IndexOf('=') + 1)..])).ToDictionary(o => o.Key, o => o.Value);
                var free = slots.FreeKits(team);
                var kit = UniformSlots.StyleOf(a[4]) is { } style
                    ? free.FirstOrDefault(f => f.Style == style) ?? throw new InvalidOperationException($"No free {a[4]} kit names are left.")
                    : free.FirstOrDefault(f => f.Name == a[4]) ?? throw new KeyNotFoundException($"{a[4]} is not a free kit name; run \"blueline slots\" to list them.");
                var copy = options.TryGetValue("copy", out var c) ? SlotAt(slots, team, c) : null;
                var art = UniformSlotArt.Make(vfs, kit, new SlotArtOptions(
                    options.TryGetValue("jersey", out var j) ? ImageLoader.Load(j) : null, copy, options.TryGetValue("menu", out var m) ? ImageLoader.Load(m) : null));
                var added = slots.Add(team, kit, options.TryGetValue("label", out var l) ? LabelNumber(l) : 6,
                    options.TryGetValue("position", out var p) ? int.Parse(p) - 1 : null, gear);
                Console.WriteLine($"Added {added} on {kit.Name} at menu position {added.Order + 1}, jersey {added.JerseyIndex}");
                foreach (var file in art)
                    Console.WriteLine($"  {(file.Content == null ? "-" : "+")} {file.Path}  {file.Note}");
                var items = art.Where(f => f.Content != null).Select(f => (f.Path, f.Content!, $"{team.Name} slot {kit.Name}")).ToList();
                items.Add((TeamCatalog.DatabasePath, slots.Save(), $"{team.Name} uniform slot {kit.Name}"));
                if (options.TryGetValue("name", out var kitName))
                {
                    var names = new LocDictionary(ReadCurrent(vfs, project, SlotNames.DictionaryPath));
                    SlotNames.SetName(names, kit.Style, kit.ArtId, kit.Variant, kitName);
                    items.Add((SlotNames.DictionaryPath, names.Save(), $"{team.Name} kit name {kitName}"));
                    Console.WriteLine($"  Named \"{kitName}\" ({SlotNames.KeyOf(kit.Style, kit.ArtId, kit.Variant)})");
                }
                project.SetMany(items, $"Add {team.Name} uniform slot");
                project.Save();
                Console.WriteLine($"Saved to {project.Folder}");
                return 0;
            }
        case "slot-remove" when a.Length == 5:
            {
                var project = ModProject.Open(a[1]);
                using var vfs = Mount(a[2]);
                var catalog = OpenTeams(vfs);
                var team = catalog.Find(a[3]) ?? throw new KeyNotFoundException($"No such team: {a[3]}");
                var (slots, _) = OpenSlots(vfs, catalog, project);
                var slot = SlotAt(slots, team, a[4]);
                slots.Remove(slot);
                project.Set(TeamCatalog.DatabasePath, slots.Save(), $"{team.Name} remove uniform slot");
                project.Save();
                Console.WriteLine($"Removed {slot}; saved to {project.Folder}");
                return 0;
            }
        case "trucolor" when a.Length == 1:
            Console.WriteLine(TruColorCommand.Usage);
            return 0;
        case "trucolor" when a.Length >= 2:
            {
                using var vfs = Mount(a[1]);
                return TruColorCommand.Run(vfs, OpenTeams(vfs), a);
            }
        case "team" when a.Length == 3 || a.Length >= 5:
            {
                using var vfs = Mount(a[1]);
                var catalog = OpenTeams(vfs);
                var team = catalog.Find(a[2]) ?? throw new KeyNotFoundException($"No such team: {a[2]}");
                var project = a.Length >= 5 ? ModProject.Open(a[3]) : null;
                var data = project != null && project.TryGet(TeamCatalog.DatabasePath, out var stored) ? project.ReadBlob(stored) : vfs.ReadContent(vfs.Find(TeamCatalog.DatabasePath)!);
                var file = TdbFile.Parse((byte[])data.Clone());
                file.ApplyMeta(System.Text.Encoding.UTF8.GetString(vfs.ReadContent(vfs.Find(TeamCatalog.MetaPath)!)));
                var details = TeamDetails.Open(file);
                foreach (var edit in a.Skip(4))
                {
                    var split = edit.IndexOf('=');
                    var key = split > 0 ? edit[..split] : throw new ArgumentException($"Not key=value: {edit}");
                    var field = details.FieldsOf(team.Index).FirstOrDefault(f => f.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                        ?? throw new KeyNotFoundException($"{team.Name} has no {key}. Keys look like team.overlayprimarycolor, arena.arenaname or palette.<row>.");
                    var changed = details.Set(field, TeamDetails.Parse(field, edit[(split + 1)..]));
                    Console.WriteLine($"{key}: {TeamDetails.Format(changed)}");
                }
                string? group = null;
                foreach (var field in details.FieldsOf(team.Index))
                {
                    if (field.Group != group)
                        Console.WriteLine($"-- {group = field.Group}");
                    Console.WriteLine($"{field.Key,-40} {field.Label,-30} {TeamDetails.Format(field)}{(field.IsLink ? "  (link)" : "")}");
                }
                if (project != null && a.Length > 4)
                {
                    project.Set(TeamCatalog.DatabasePath, details.Save(), $"{team.Name} team details");
                    project.Save();
                    Console.WriteLine($"Saved to {project.Folder}");
                }
                return 0;
            }
        case "franchise-immersion" when a.Length is 3 or 4 && (a.Length == 3 || a[3] == "--write"):
            {
                var project = ModProject.Open(a[1]);
                using var vfs = Mount(a[2]);
                var workspace = new Workspace(new SessionLog());
                workspace.SetGame(vfs);
                workspace.SetProject(project);
                var result = FranchiseImmersion.Apply(project, workspace, a.Length == 4);
                foreach (var line in result.Log)
                    Console.WriteLine(line);
                Console.WriteLine($"Arena cells: {result.ArenaCells}; insider cells: {result.AuthorCells}; text lines: {result.TextLines}; capacity rows: {result.CapacityRows}");
                if (result.ArenasNotFound.Count > 0)
                    Console.WriteLine($"No club found for: {string.Join(", ", result.ArenasNotFound)}");
                Console.WriteLine(a.Length == 4 ? $"Saved to {project.Folder} as one undo step." : "Dry run; add --write to save it into the project.");
                return 0;
            }
        case "franchise-info" when a.Length == 2:
            {
                var save = FranchiseSave.Read(a[1]);
                Console.WriteLine(save.FileInfo);
                Console.WriteLine($"Blocks: {string.Join(", ", save.BlockSizes.Select((s, i) => $"{i}: {s:N0}" + (i == save.DatabaseBlock ? " (league TDB)" : "")))}; tail {save.TailLength:N0}");
                var db = TdbFile.Parse((byte[])save.Database.Clone());
                var checks = db.VerifyCrcs();
                Console.WriteLine($"{db.Tables.Count} tables, {checks.Count(c => c.Matches)} of {checks.Count} checksums verify");
                foreach (var table in db.Tables.OrderBy(t => t.ShortName, StringComparer.Ordinal))
                    Console.WriteLine($"  {table.ShortName} {table.Count,6}/{table.Allocated}");
                return 0;
            }
        case "franchise-repack" when a.Length == 3:
            {
                var save = FranchiseSave.Read(a[1]);
                RefuseSameFolder(a[1], a[2]);
                save.WriteTo(a[2], save.Database);
                var again = FranchiseSave.Read(a[2]);
                Console.WriteLine(again.Database.AsSpan().SequenceEqual(save.Database) ? $"Written to {a[2]}; it reads back with the same league database." : "Written, but it reads back differently.");
                return 0;
            }
        case "franchise-sync" when a.Length == 5:
            {
                using var vfs = Mount(a[1]);
                var meta = System.Text.Encoding.UTF8.GetString(vfs.ReadContent(vfs.Find(TeamCatalog.MetaPath)!));
                byte[] source;
                if (RosterSave.IsRosterSave(a[2]) && !FranchiseSave.IsFranchiseSave(a[2]))
                    source = RosterSave.Read(a[2]).Database;
                else
                {
                    var project = ModProject.Open(a[2]);
                    source = project.TryGet(TeamCatalog.DatabasePath, out var stored) ? project.ReadBlob(stored) : vfs.ReadContent(vfs.Find(TeamCatalog.DatabasePath)!);
                }
                var from = TdbFile.Parse((byte[])source.Clone());
                from.ApplyMeta(meta);
                var save = FranchiseSave.Read(a[3]);
                var league = TdbFile.Parse((byte[])save.Database.Clone());
                league.ApplyMeta(meta);
                var plan = FranchiseSync.Plan(from, league);
                Console.Write(FranchiseSync.Describe(plan));
                Console.WriteLine($"{plan.Count} cell(s) to change.");
                if (plan.Count > 0)
                {
                    RefuseSameFolder(a[3], a[4]);
                    FranchiseSync.Apply(league, plan);
                    save.WriteTo(a[4], league.Data);
                    Console.WriteLine($"Written to {a[4]}. The original save was not changed.");
                }
                return 0;
            }
        case "roster-league-sync" when a.Length is 4 or 5 && (a.Length == 4 || a[4] == "--write"):
            {
                using var vfs = Mount(a[1]);
                var project = ModProject.Open(a[2]);
                var meta = System.Text.Encoding.UTF8.GetString(vfs.ReadContent(vfs.Find(TeamCatalog.MetaPath)!));
                var source = TdbFile.Parse((byte[])(project.TryGet(TeamCatalog.DatabasePath, out var stored) ? project.ReadBlob(stored) : vfs.ReadContent(vfs.Find(TeamCatalog.DatabasePath)!)).Clone());
                source.ApplyMeta(meta);
                var save = RosterSave.Read(a[3]);
                var roster = TdbFile.Parse((byte[])save.Database.Clone());
                roster.ApplyMeta(meta);
                var plan = FranchiseSync.PlanRoster(source, roster);
                foreach (var change in plan.Pending)
                    Console.WriteLine($"{change.Table} #{change.Index} {change.Field.Label}: {RosterCellChange.Show(change.Field, change.Roster)} -> {RosterCellChange.Show(change.Field, change.Project)}");
                foreach (var s in plan.Skipped)
                    Console.WriteLine("Skipped: " + s);
                Console.WriteLine($"To write: {plan.WhatIsLeft} ({plan.Changes.Count - plan.Pending.Count} already in the roster).");
                if (a.Length == 5)
                {
                    if (ShadInstall.AnyRunning())
                        throw new InvalidOperationException("shadPS4 is running. Close the game before writing its roster save.");
                    var backup = RosterSync.Write(save, plan, meta, Path.Combine(Path.GetDirectoryName(Path.GetFullPath(a[3]).TrimEnd('\\', '/'))! + "-backups"));
                    Console.WriteLine(backup == null ? "Nothing to write." : $"Written. The old save is in {backup}");
                }
                return 0;
            }
        case "roster-sync" when a.Length is 4 or 5 && (a.Length == 4 || a[4] == "--write"):
            {
                using var vfs = Mount(a[1]);
                var project = ModProject.Open(a[2]);
                if (!project.TryGet(TeamCatalog.DatabasePath, out var stored))
                {
                    Console.WriteLine("The project has no database edits, so there is nothing to sync.");
                    return 0;
                }
                var meta = System.Text.Encoding.UTF8.GetString(vfs.ReadContent(vfs.Find(TeamCatalog.MetaPath)!));
                var save = RosterSave.Read(a[3]);
                var plan = RosterSync.Plan(vfs.ReadContent(vfs.Find(TeamCatalog.DatabasePath)!), project.ReadBlob(stored), save, meta);
                Console.Write(RosterSync.Describe(plan));
                Console.WriteLine($"To write: {plan.WhatIsLeft} ({plan.Changes.Count - plan.Pending.Count} cell(s) already in the roster), {plan.Skipped.Count} skipped.");
                if (a.Length == 5)
                {
                    if (ShadInstall.AnyRunning())
                        throw new InvalidOperationException("shadPS4 is running. Close the game before writing its roster save.");
                    var backup = RosterSync.Write(save, plan, meta, Path.Combine(Path.GetDirectoryName(Path.GetFullPath(a[3]).TrimEnd('\\', '/'))! + "-backups"));
                    Console.WriteLine(backup == null ? "Nothing to write." : $"Written. The old save is in {backup}");
                }
                return 0;
            }
        case "players" when a.Length >= 2:
            {
                using var vfs = Mount(a[1]);
                var (book, _, _) = OpenRoster(vfs, a.Skip(2).FirstOrDefault(x => !x.Contains('=')));
                var filters = a.Skip(2).Where(x => x.Contains('=')).Select(x => (Key: x[..x.IndexOf('=')].ToLowerInvariant(), Value: x[(x.IndexOf('=') + 1)..])).ToList();
                var shown = 0;
                foreach (var p in book.Players().Where(p => RosterFilter(book, p, filters)).OrderBy(p => p.Club).ThenBy(p => p.Position).ThenByDescending(p => p.Overall))
                {
                    Console.WriteLine($"{p.GameId,6}  {Abbr(book, p.Club),-4} {"CLRDG"[Math.Clamp(p.Position, 0, 4)]}  #{p.Jersey,-3} {p.Overall,3}  {p.Name}{(p.Active ? "" : "  (inactive)")}");
                    shown++;
                }
                Console.WriteLine($"{shown} player(s). Years shift: {book.YearShift}.");
                return 0;
            }
        case "player" when a.Length >= 3:
            {
                using var vfs = Mount(a[1]);
                var project = a.Length >= 5 || (a.Length == 4 && !a[3].Contains('=')) ? a[3] : null;
                var (book, choices, opened) = OpenRoster(vfs, project);
                var player = OnePlayer(book, a[2]);
                var edits = a.Skip(project == null ? 3 : 4).ToList();
                if (edits.Count > 0 && opened == null)
                    throw new ArgumentException("Give a project folder before the edits, so they are saved somewhere.");
                foreach (var edit in edits)
                {
                    var split = edit.IndexOf('=');
                    if (split <= 0)
                        throw new ArgumentException($"Not key=value: {edit}");
                    var (key, text) = (edit[..split].ToLowerInvariant(), edit[(split + 1)..]);
                    switch (key)
                    {
                        case "move":
                            {
                                var parts = text.Split(':');
                                var from = parts.Length == 2 ? TeamOf(book, parts[0]) : book.Club(player.GameId) ?? throw new InvalidOperationException($"{player.Name} has no NHL or AHL club; use move=<from>:<to>.");
                                var to = TeamOf(book, parts[^1]);
                                book.Move(player.GameId, from, to);
                                Console.WriteLine($"Moved {player.Name}: {book.TeamName(from)} -> {book.TeamName(to)} (lines cleared; he is a scratch there).");
                                break;
                            }
                        case "jersey" or "number":
                            book.SetJersey(player.GameId, int.Parse(text));
                            Console.WriteLine($"Number: {text} (bio and every team)");
                            break;
                        case "archetype":
                            {
                                var style = choices.Get(player.IsGoalie ? RosterChoices.GoalieStyle : RosterChoices.SkaterStyle)!
                                    .FirstOrDefault(c => c.Name.Equals(text, StringComparison.OrdinalIgnoreCase) || c.Value.ToString() == text)
                                    ?? throw new ArgumentException($"{text} is not an archetype.");
                                book.SetArchetype(player.GameId, style.Value);
                                Console.WriteLine($"Archetype: {style.Name} (AI row and every team)");
                                break;
                            }
                        case "ratings":
                            Console.WriteLine($"Ratings {text}: {book.AddToRatings([player.GameId], int.Parse(text.TrimStart('+')))} changed");
                            break;
                        default:
                            {
                                var field = book.Find(player.GameId, key) ?? throw new KeyNotFoundException($"{player.Name} has no {key}. Keys look like bio.lastname, ai.c_speed, skatergear.stick or roster.11.captain.");
                                var changed = book.Set(field, book.Parse(field, text, choices));
                                Console.WriteLine($"{field.Key}: {book.Format(changed, choices)}");
                                break;
                            }
                    }
                }
                player = book.Summary(player.GameId)!;
                Console.WriteLine($"== {player.Name} (game_id {player.GameId}), {book.TeamName(player.Club)}, portrait {RosterSource.PortraitPath(player.ArtId)}{(vfs.Find(RosterSource.PortraitPath(player.ArtId)) == null ? " (missing)" : "")}");
                Console.WriteLine("   Teams: " + string.Join(", ", book.Memberships(player.GameId).Select(m => $"{book.TeamName(m.Team)} ({m.Team})")));
                string? group = null;
                foreach (var field in book.FieldsOf(player.GameId))
                {
                    if (field.Group != group)
                        Console.WriteLine($"-- {group = field.Group}");
                    Console.WriteLine($"{field.Key,-34} {field.Label,-34} {book.Format(field, choices)}");
                }
                foreach (var m in book.Memberships(player.GameId))
                {
                    var slots = book.Lines(m.Team).Where(l => l.Value == player.GameId).Select(l => RosterBook.SlotLabel(l.Key)).ToList();
                    Console.WriteLine($"   {book.TeamName(m.Team)} slots: {(slots.Count == 0 ? "none" : string.Join(", ", slots))}");
                }
                if (edits.Count > 0)
                {
                    opened!.Set(TeamCatalog.DatabasePath, book.Save(), $"Roster: {player.Name}");
                    opened.Save();
                    Console.WriteLine($"Saved to {opened.Folder}");
                }
                return 0;
            }
        case "lines" when a.Length >= 3:
            {
                using var vfs = Mount(a[1]);
                var project = a.Length >= 4 ? a[3] : null;
                var (book, _, opened) = OpenRoster(vfs, project);
                var team = TeamOf(book, a[2]);
                foreach (var edit in a.Skip(4))
                {
                    var split = edit.IndexOf('=');
                    if (split <= 0)
                        throw new ArgumentException($"Not slot=player: {edit}");
                    var who = edit[(split + 1)..];
                    long? gid = who is "" or "-" or "none" ? null : OnePlayer(book, who).GameId;
                    book.SetSlot(team, edit[..split].ToLowerInvariant(), gid);
                    Console.WriteLine($"{RosterBook.SlotLabel(edit[..split].ToLowerInvariant())}: {(gid is { } g ? book.Summary(g)!.Name : "nobody")}");
                }
                var lines = book.Lines(team);
                Console.WriteLine($"== {book.TeamName(team)} ({team}): {book.PlayersOn(team).Count} players");
                foreach (var slot in book.LineSlots)
                    Console.WriteLine($"{slot,-9} {RosterBook.SlotLabel(slot),-20} {(lines.TryGetValue(slot, out var g) ? book.Summary(g)!.Name : "-")}");
                var placed = lines.Values.ToHashSet();
                var unplaced = book.PlayersOn(team).Where(g => !placed.Contains(g)).Select(g => book.Summary(g)!.Name).ToList();
                if (unplaced.Count > 0)
                    Console.WriteLine("In no slot: " + string.Join(", ", unplaced));
                if (a.Length > 4)
                {
                    opened!.Set(TeamCatalog.DatabasePath, book.Save(), $"Roster: {book.TeamName(team)} lines");
                    opened.Save();
                    Console.WriteLine($"Saved to {opened.Folder}");
                }
                return 0;
            }
        case "players-set" when a.Length >= 5:
            {
                using var vfs = Mount(a[2]);
                var (book, choices, opened) = OpenRoster(vfs, a[1]);
                var filters = a.Skip(3).TakeWhile(x => x != "--").Where(x => x.Contains('=')).Select(x => (Key: x[..x.IndexOf('=')].ToLowerInvariant(), Value: x[(x.IndexOf('=') + 1)..])).ToList();
                var sets = a.SkipWhile(x => x != "--").Skip(1).ToList();
                if (sets.Count == 0)
                    throw new ArgumentException("Put the changes after --, e.g. players-set <project> <game> team=EDM -- ratings=+2 bio.weight=200");
                var players = book.Players().Where(p => RosterFilter(book, p, filters)).ToList();
                var cells = 0;
                foreach (var p in players)
                    foreach (var edit in sets)
                    {
                        var split = edit.IndexOf('=');
                        var (key, text) = (edit[..split].ToLowerInvariant(), edit[(split + 1)..]);
                        if (key == "ratings")
                            cells += book.AddToRatings([p.GameId], int.Parse(text.TrimStart('+')));
                        else if (book.Find(p.GameId, key) is { } field)
                        {
                            book.Set(field, book.Parse(field, text, choices));
                            cells++;
                        }
                    }
                Console.WriteLine($"{players.Count} player(s), {cells} cell(s) changed.");
                if (cells > 0)
                {
                    opened!.Set(TeamCatalog.DatabasePath, book.Save(), $"Roster: bulk edit of {players.Count} players");
                    opened.Save();
                    Console.WriteLine($"Saved to {opened.Folder} as one undo step.");
                }
                return 0;
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
        case "horns" when a.Length is 2 or 3:
            {
                using var vfs = Mount(a[1]);
                var project = a.Length == 3 ? ModProject.Open(a[2]) : null;
                byte[]? Current(string path) => project != null && project.TryGet(path, out var c) ? project.ReadBlob(c) : vfs.Find(path) is { } f ? vfs.ReadContent(f) : null;
                var catalog = TeamCatalog.Load(Current, vfs.Files.Select(f => f.Path).ToList(), vfs.ReadStock);
                var file = TdbFile.Parse((byte[])Current(TeamCatalog.DatabasePath)!.Clone());
                file.ApplyMeta(System.Text.Encoding.UTF8.GetString(vfs.ReadContent(vfs.Find(TeamCatalog.MetaPath)!)));
                var details = TeamDetails.Open(file);
                var users = new Dictionary<long, List<string>>();
                foreach (var team in catalog.Teams)
                    if (details.FieldsOf(team.Index).FirstOrDefault(f => f.Key == "arena.goalhorn") is { } horn)
                    {
                        var value = Convert.ToInt64(horn.Value);
                        if (!users.TryGetValue(value, out var list))
                            users[value] = list = [];
                        list.Add(team.Abbreviation);
                    }
                foreach (var horn in GoalHorns.All)
                    Console.WriteLine($"{horn.Value,4}  {horn.Name,-28} {horn.Kind,-16} {(users.TryGetValue(horn.Value, out var who) ? string.Join(' ', who) : "")}");
                var bank = Current(GoalHorns.BankPath)!;
                var samples = HornBank.Samples(bank);
                Console.WriteLine($"-- {GoalHorns.BankPath}: {samples.Count} samples{(project?.TryGet(GoalHorns.BankPath, out _) == true ? " (edited in this project)" : "")}");
                foreach (var sample in samples)
                    Console.WriteLine($"{sample.Index,4}  {sample.CodecName,-9} {(sample.Header[1] >> 2) + 1} ch  {sample.Seconds,5:F1} s  {sample.Length,9:N0} bytes");
                return 0;
            }
        case "horn-export" when a.Length >= 4:
            {
                using var vfs = Mount(a[1]);
                var project = a[2] == "-" ? null : ModProject.Open(a[2]);
                var bank = project != null && project.TryGet(GoalHorns.BankPath, out var c) ? project.ReadBlob(c) : vfs.ReadContent(vfs.Find(GoalHorns.BankPath)!);
                var samples = HornBank.Samples(bank);
                var tool = new OpusTool(Ffmpeg());
                Directory.CreateDirectory(a[3]);
                var wanted = a.Length > 4 ? a.Skip(4).Select(int.Parse).ToList() : samples.Where(OpusTool.CanHandle).Select(s => s.Index).ToList();
                foreach (var index in wanted)
                {
                    var target = Path.Combine(a[3], $"horn_{index:D3}.wav");
                    tool.DecodeAsync(HornBank.Read(bank, samples[index]), target).GetAwaiter().GetResult();
                    Console.WriteLine(target);
                }
                return 0;
            }
        case "horn-set" when a.Length == 5:
            {
                var project = ModProject.Open(a[1]);
                using var vfs = Mount(a[2]);
                var bank = project.TryGet(GoalHorns.BankPath, out var c) ? project.ReadBlob(c) : vfs.ReadContent(vfs.Find(GoalHorns.BankPath)!);
                var result = HornBank.ReplaceAsync(new OpusTool(Ffmpeg()), bank, int.Parse(a[3]), a[4], new Progress<string>(Console.WriteLine)).GetAwaiter().GetResult();
                project.Set(GoalHorns.BankPath, result.Bank, $"Horn sample {result.Sample.Index} from {Path.GetFileName(a[4])}");
                project.Save();
                Console.WriteLine($"Sample {result.Sample.Index}: {result.Encoded.Seconds:F1} s at {result.Encoded.Kilobits} kbit/s, {result.Encoded.Sample.Length:N0} of {result.Sample.Length:N0} bytes. Saved to {project.Folder}");
                return 0;
            }
        case "horn-label" when a.Length == 3:
            {
                var project = ModProject.Open(a[1]);
                using var vfs = Mount(a[2]);
                var bank = vfs.ReadContent(vfs.Find(GoalHorns.BankPath)!);
                var samples = HornBank.Samples(bank);
                var speech = Path.Combine(Path.GetTempPath(), "Blueline", "horn-labels");
                Directory.CreateDirectory(speech);
                Console.WriteLine("Speaking the labels");
                Speak(speech, samples.Select(s => (s.Index, $"Horn sample {s.Index}. {s.Index}. Sample {s.Index}.")).ToList());
                var labelled = HornBank.LabelAsync(new OpusTool(Ffmpeg()), bank,
                    (index, wav) => { File.Copy(Path.Combine(speech, $"{index}.wav"), wav, true); return Task.CompletedTask; },
                    new Progress<(int Done, int Total)>(p => { if (p.Done % 50 == 0) Console.WriteLine($"{p.Done} of {p.Total}"); })).GetAwaiter().GetResult();
                project.Set(GoalHorns.BankPath, labelled, "Horn test bank: every sample says its number");
                project.Save();
                Console.WriteLine($"Saved to {project.Folder}. Revert {GoalHorns.BankPath} in the project to undo.");
                return 0;
            }
        case "faceoff" when a.Length == 4:
            {
                using var vfs = Mount(a[1]);
                var catalog = OpenTeams(vfs);
                var team = catalog.Find(a[2]) ?? throw new KeyNotFoundException($"No such team: {a[2]}");
                using var site = new Blueline.Core.Rinks.FaceoffNet(Path.GetFullPath(a[3]));
                var (file, layout) = Blueline.Core.Rinks.FullIceJob.FetchAsync(site, team, new Progress<string>(Console.WriteLine)).GetAwaiter().GetResult();
                Console.WriteLine($"{layout.RinkName}: {file}  ({site.Requests} requests)");
                return 0;
            }
        case "full-ice-leagues" when a.Length >= 5:
            {
                // full-ice-leagues <game> <cache folder> <out folder> <league,league> [--project P] [--model M] [--default-rest]
                // Every team of those leagues (one per art id) from its newest TheFaceoff.net layout: fetched one request
                // at a time, then fitted and encoded in parallel. Teams without a usable layout are listed; with
                // --default-rest they and every other slot share one plain rink.
                var options = a.Skip(5).ToList();
                string? projectFolder = options.IndexOf("--project") is >= 0 and var pi && pi + 1 < options.Count ? options[pi + 1] : null;
                var model = FullIceModel(options);
                var defaultRest = options.Contains("--default-rest");
                var leagues = a[4].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(l => l.ToUpperInvariant()).ToHashSet();
                using var vfs = Mount(a[1]);
                var catalog = OpenTeams(vfs);
                using var site = new Blueline.Core.Rinks.FaceoffNet(Path.GetFullPath(a[2]));
                var ffmpeg = FfmpegTool.Locate(AppSettings.Load().FfmpegPath);
                Directory.CreateDirectory(a[3]);
                byte[]? GameRead(string p) => vfs.Find(p) is { } f ? vfs.ReadContent(f) : null;
                byte[]? Stock(int art) => vfs.ReadStock(Blueline.Core.Rinks.FullIce.SlotPath(art)) ?? GameRead(Blueline.Core.Rinks.FullIce.SlotPath(art));

                var teams = catalog.Teams.Where(t => leagues.Contains(t.League.ToUpperInvariant()))
                    .GroupBy(t => t.ArtId).Select(g => g.First()).Where(t => Stock(t.ArtId) != null).ToList();
                Console.WriteLine($"{teams.Count} teams in {string.Join(", ", leagues)} with a centre ice slot");

                var fetched = new List<(Blueline.Core.Teams.TeamInfo Team, string File, string Rink)>();
                var misses = new List<string>();
                foreach (var team in teams)
                {
                    try
                    {
                        var (file, layout) = Blueline.Core.Rinks.FullIceJob.FetchAsync(site, team).GetAwaiter().GetResult();
                        fetched.Add((team, file, layout.RinkName));
                        Console.WriteLine($"  {team.League} {team.Name}: {layout.RinkName}");
                    }
                    catch (Exception e) when (e is KeyNotFoundException or InvalidDataException or InvalidOperationException or HttpRequestException)
                    {
                        misses.Add($"{team.League} {team.Name} (art {team.ArtId}): {e.Message}");
                        Console.WriteLine($"  {team.League} {team.Name}: MISSING");
                    }
                }
                Console.WriteLine($"{site.Requests} requests to TheFaceoff.net");

                var built = new System.Collections.Concurrent.ConcurrentBag<(string, byte[], string)>();
                var doneArt = new System.Collections.Concurrent.ConcurrentDictionary<int, bool>();
                var count = 0;
                Parallel.ForEach(fetched, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, item =>
                {
                    var source = Blueline.Core.Rinks.FullIceJob.LoadPictureAsync(item.File, ffmpeg).GetAwaiter().GetResult();
                    var result = Blueline.Core.Rinks.FullIceJob.Build(item.Team, source, item.File, item.Rink, Blueline.Core.Rinks.FullIceFit.Layout, Stock(item.Team.ArtId)!);
                    File.WriteAllBytes(Path.Combine(a[3], $"fullice_{item.Team.ArtId}_preview.png"), Blueline.Formats.Textures.PngFile.Write(result.Preview));
                    if (result.Measure is not { Found: true } || !result.Measure.How.StartsWith("goal lines", StringComparison.Ordinal))
                        lock (misses) misses.Add($"{item.Team.League} {item.Team.Name} (art {item.Team.ArtId}): {item.Rink} fitted by {result.Measure?.How}; check its preview");
                    if (result.Measure is { Found: true })
                    {
                        built.Add((result.SlotPath, result.Gnf, $"Full ice for {item.Team.Name} ({item.Rink})"));
                        doneArt[item.Team.ArtId] = true;
                    }
                    Console.WriteLine($"  built {Interlocked.Increment(ref count)}/{fetched.Count}: {item.Team.Name}");
                });
                var written = built.ToList();

                if (defaultRest)
                {
                    var others = catalog.Teams.GroupBy(t => t.ArtId).Select(g => g.First())
                        .Where(t => !doneArt.ContainsKey(t.ArtId) && Stock(t.ArtId) != null).ToList();
                    if (others.Count > 0)
                    {
                        var plain = Blueline.Core.Rinks.FullIceJob.BuildPlain(others[0], GameRead, Stock(others[0].ArtId)!, teamArt: false);
                        written.AddRange(Blueline.Core.Rinks.FullIceJob.Spread(plain.Fitted, others.Select(t => (Blueline.Core.Rinks.FullIce.SlotPath(t.ArtId), Stock(t.ArtId)!)))
                            .Select(t => (t.Slot, t.Gnf, "Full ice (shared plain rink)")));
                        File.WriteAllBytes(Path.Combine(a[3], "fullice_shared_preview.png"), Blueline.Formats.Textures.PngFile.Write(plain.Preview));
                    }
                    Console.WriteLine($"{others.Count} other slots share one plain rink");
                }
                File.WriteAllLines(Path.Combine(a[3], "misses.txt"), misses);
                Console.WriteLine($"{built.Count} rinks from TheFaceoff.net; {misses.Count} to look at (misses.txt)");
                foreach (var miss in misses)
                    Console.WriteLine("  " + miss);
                if (projectFolder != null)
                {
                    var project = File.Exists(Path.Combine(projectFolder, "project.json")) ? ModProject.Open(projectFolder) : ModProject.Create(projectFolder, "Full ice rinks");
                    if (model != null && !project.TryGet(Blueline.Core.Rinks.FullIce.ModelPath, out _))
                        written.Add((Blueline.Core.Rinks.FullIce.ModelPath, model, "Full-ice rink model"));
                    project.SetMany(written, $"Full ice for {written.Count} slots");
                    project.Save();
                    Console.WriteLine($"Wrote {written.Count} files into {project.Folder}");
                }
                return 0;
            }
        case "full-ice-set" when a.Length >= 5:
            {
                // full-ice-set <game> <cache folder> <out folder> [--project P] [--model M] [--plain-rest] <team> [team ...]
                // One site session, a preview each; --plain-rest gives every other centre ice slot one shared plain rink.
                var rest = a.Skip(4).ToList();
                string? projectFolder = null;
                var setModel = FullIceModel(rest);
                var plainRest = rest.Remove("--plain-rest");
                if (rest.IndexOf("--project") is >= 0 and var pi && pi + 1 < rest.Count) { projectFolder = rest[pi + 1]; rest.RemoveRange(pi, 2); }
                if (rest.IndexOf("--model") is >= 0 and var mi && mi + 1 < rest.Count) rest.RemoveRange(mi, 2);
                using var vfs = Mount(a[1]);
                var catalog = OpenTeams(vfs);
                using var site = new Blueline.Core.Rinks.FaceoffNet(Path.GetFullPath(a[2]));
                var ffmpeg = FfmpegTool.Locate(AppSettings.Load().FfmpegPath);
                Directory.CreateDirectory(a[3]);
                var written = new List<(string, byte[], string)>();
                var doneArt = new HashSet<int>();
                byte[]? GameRead(string p) => vfs.Find(p) is { } f ? vfs.ReadContent(f) : null;
                foreach (var name in rest)
                {
                    var team = catalog.Find(name) ?? throw new KeyNotFoundException($"No such team: {name}");
                    var slot = Blueline.Core.Rinks.FullIce.SlotPath(team.ArtId);
                    var stockSlot = vfs.ReadStock(slot) ?? (vfs.Find(slot) is { } modded ? vfs.ReadContent(modded) : null);
                    if (stockSlot == null)
                    {
                        Console.WriteLine($"{team.Name}: no centre ice slot {slot}; skipped");
                        continue;
                    }
                    var result = Blueline.Core.Rinks.FullIceJob.AutoAsync(site, team, p => vfs.Find(p) is { } f ? vfs.ReadContent(f) : null, stockSlot, ffmpeg,
                        new Progress<string>(Console.WriteLine)).GetAwaiter().GetResult();
                    File.WriteAllBytes(Path.Combine(a[3], $"fullice_{team.ArtId}_preview.png"), Blueline.Formats.Textures.PngFile.Write(result.Preview));
                    File.WriteAllBytes(Path.Combine(a[3], $"fullice_{team.ArtId}.gnf"), result.Gnf);
                    Console.WriteLine($"{team.Name} (art {team.ArtId}): {(result.IsPlain ? "PLAIN" : "FACEOFF " + result.RinkName)}  {result.Measure?.How}");
                    written.Add((result.SlotPath, result.Gnf, $"Full ice for {team.Name}"));
                    doneArt.Add(team.ArtId);
                }
                Console.WriteLine($"{site.Requests} requests to TheFaceoff.net");
                if (plainRest)
                {
                    // One plain rink without a logo, encoded once, for every other centre ice slot.
                    var others = catalog.Teams.GroupBy(t => t.ArtId).Select(g => g.OrderBy(t => t.LeagueId == 0 ? 0 : 1).First())
                        .Where(t => !doneArt.Contains(t.ArtId))
                        .Select(t => (Slot: Blueline.Core.Rinks.FullIce.SlotPath(t.ArtId), Team: t))
                        .Select(t => (t.Slot, t.Team, Stock: vfs.ReadStock(t.Slot) ?? GameRead(t.Slot)))
                        .Where(t => t.Stock != null).ToList();
                    if (others.Count > 0)
                    {
                        var plain = Blueline.Core.Rinks.FullIceJob.BuildPlain(others[0].Team, GameRead, others[0].Stock!, teamArt: false);
                        var shared = Blueline.Core.Rinks.FullIceJob.Spread(plain.Fitted, others.Select(t => (t.Slot, t.Stock!)));
                        written.AddRange(shared.Select(t => (t.Slot, t.Gnf, "Full ice (shared plain rink)")));
                        File.WriteAllBytes(Path.Combine(a[3], "fullice_shared_preview.png"), Blueline.Formats.Textures.PngFile.Write(plain.Preview));
                    }
                    Console.WriteLine($"{others.Count} other teams share one plain rink");
                }
                if (projectFolder != null)
                {
                    var project = ModProject.Open(projectFolder);
                    if (setModel != null)
                        written.Add((Blueline.Core.Rinks.FullIce.ModelPath, setModel, "Full-ice rink model"));
                    project.SetMany(written, $"Full ice for {written.Count} slots");
                    project.Save();
                    Console.WriteLine($"Wrote {written.Count} files into {project.Folder}{(setModel != null ? ", with the full-ice model" : "")}");
                }
                return 0;
            }
        case "full-ice" when a.Length >= 5:
            {
                // full-ice <game> <team> <picture | cache folder> <out folder> [--template] [--project <folder>]
                var options = a.Skip(5).ToList();
                var fit = options.Contains("--template") ? Blueline.Core.Rinks.FullIceFit.Template : Blueline.Core.Rinks.FullIceFit.Layout;
                var projectAt = options.IndexOf("--project");
                using var vfs = Mount(a[1]);
                var catalog = OpenTeams(vfs);
                var team = catalog.Find(a[2]) ?? throw new KeyNotFoundException($"No such team: {a[2]}");
                string file;
                string? rinkName = null;
                var slot = Blueline.Core.Rinks.FullIce.SlotPath(team.ArtId);
                var stockSlot = vfs.ReadStock(slot) ?? (vfs.Find(slot) is { } modded ? vfs.ReadContent(modded) : null)
                    ?? throw new KeyNotFoundException($"{team.Name} (art id {team.ArtId}) has no centre ice slot {slot}.");
                Blueline.Core.Rinks.FullIceResult result;
                if (a[3] == "plain")
                {
                    result = Blueline.Core.Rinks.FullIceJob.BuildPlain(team, p => vfs.Find(p) is { } f ? vfs.ReadContent(f) : null, stockSlot);
                    file = result.SourceFile;
                }
                else
                {
                if (Directory.Exists(a[3]))
                {
                    using var site = new Blueline.Core.Rinks.FaceoffNet(Path.GetFullPath(a[3]));
                    var fetched = Blueline.Core.Rinks.FullIceJob.FetchAsync(site, team, new Progress<string>(Console.WriteLine)).GetAwaiter().GetResult();
                    (file, rinkName) = (fetched.File, fetched.Layout.RinkName);
                }
                else
                    file = a[3];
                var source = Blueline.Core.Rinks.FullIceJob.LoadPictureAsync(file, FfmpegTool.Locate(AppSettings.Load().FfmpegPath)).GetAwaiter().GetResult();
                result = Blueline.Core.Rinks.FullIceJob.Build(team, source, file, rinkName, fit, stockSlot);
                }
                Directory.CreateDirectory(a[4]);
                var stem = $"fullice_{team.ArtId}";
                File.WriteAllBytes(Path.Combine(a[4], stem + "_preview.png"), Blueline.Formats.Textures.PngFile.Write(result.Preview));
                File.WriteAllBytes(Path.Combine(a[4], stem + "_texture.png"), Blueline.Formats.Textures.PngFile.Write(result.Fitted));
                File.WriteAllBytes(Path.Combine(a[4], stem + ".gnf"), result.Gnf);
                Console.WriteLine($"{team.Name} -> {slot}");
                Console.WriteLine($"  source {(a[3] == "plain" ? file : $"{result.Source.Width}x{result.Source.Height}")}{(rinkName != null ? $" ({rinkName})" : "")}; {result.Measure?.How ?? (a[3] == "plain" ? "" : "template fit")}");
                Console.WriteLine($"  wrote {stem}_preview.png, {stem}_texture.png and {stem}.gnf to {a[4]}");
                if (projectAt >= 0 && projectAt + 1 < options.Count)
                {
                    var project = ModProject.Open(options[projectAt + 1]);
                    var items = new List<(string, byte[], string)> { (slot, result.Gnf, $"Full ice for {team.Name}") };
                    if (FullIceModel(options) is { } model)
                        items.Add((Blueline.Core.Rinks.FullIce.ModelPath, model, "Full-ice rink model"));
                    project.SetMany(items, $"Full ice for {team.Name}");
                    project.Save();
                    Console.WriteLine($"  set in project {project.Folder}{(items.Count > 1 ? ", with the full-ice model" : "")}");
                }
                return 0;
            }
        case "pack":
            Console.WriteLine(PackCommand.Usage);
            return 0;
        case string verb when verb.StartsWith("pack-", StringComparison.Ordinal):
            return PackCommand.Run(a);
        default:
            Console.WriteLine(Usage);
            return a.Length == 0 ? 0 : 1;
    }
}

// The Franchise commands always write a copy, never the save they read.
static void RefuseSameFolder(string from, string to)
{
    if (Path.GetFullPath(from).TrimEnd('\\', '/').Equals(Path.GetFullPath(to).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
        throw new ArgumentException("Write to another folder than the save being read.");
    if (ShadInstall.AnyRunning())
        throw new InvalidOperationException("shadPS4 is running. Close the game before writing a save.");
}

// ---- Roster editor helpers

// The roster from a project folder (edits are saved there), a roster save folder (read only), or the game itself.
static (RosterBook Book, RosterChoices Choices, ModProject? Project) OpenRoster(VirtualFileSystem vfs, string? source)
{
    RosterBook book;
    ModProject? project = null;
    if (source != null && RosterSave.IsRosterSave(source))
        book = RosterSource.OpenSave(vfs, source, RosterSource.Open(vfs, null));
    else
    {
        project = source == null ? null : ModProject.Open(source);
        book = RosterSource.Open(vfs, project);
    }
    return (book, RosterSource.Choices(vfs, book), project);
}

static PlayerSummary OnePlayer(RosterBook book, string who)
{
    var found = RosterSource.Find(book, who);
    return found.Count switch
    {
        0 => throw new KeyNotFoundException($"No player matches {who}."),
        1 => found[0],
        _ => throw new ArgumentException($"{found.Count} players match {who}; use a game_id: " + string.Join(", ", found.Take(12).Select(p => $"{p.GameId} {p.Name} ({book.TeamName(p.Club)})"))),
    };
}

// A team by index, abbreviation (NHL and AHL clubs first) or full name.
static int TeamOf(RosterBook book, string text)
{
    if (int.TryParse(text, out var index) && book.Teams.ContainsKey(index))
        return index;
    var team = book.Teams.Values.FirstOrDefault(t => t.Abbreviation.Equals(text, StringComparison.OrdinalIgnoreCase) && RosterBook.ClubLeagues.Contains(t.League))
        ?? book.Teams.Values.FirstOrDefault(t => t.Abbreviation.Equals(text, StringComparison.OrdinalIgnoreCase))
        ?? book.Teams.Values.FirstOrDefault(t => t.Name.Equals(text, StringComparison.OrdinalIgnoreCase));
    return team?.Index ?? throw new KeyNotFoundException($"No team {text}.");
}

static string Abbr(RosterBook book, int team) => book.Teams.TryGetValue(team, out var t) ? t.Abbreviation : team < 0 ? "-" : team.ToString();

// Filters: team=EDM, pos=C|LW|RW|D|G|F, name=text, min=80 (overall), league=0, active=yes.
static bool RosterFilter(RosterBook book, PlayerSummary p, IReadOnlyList<(string Key, string Value)> filters)
{
    foreach (var (key, value) in filters)
    {
        var ok = key switch
        {
            "team" => p.Club == TeamOf(book, value) || book.MembershipOn(p.GameId, TeamOf(book, value)) != null,
            "pos" => value.ToUpperInvariant() switch
            {
                "C" => p.Position == 0, "LW" => p.Position == 1, "RW" => p.Position == 2, "D" => p.Position == 3, "G" => p.Position == 4,
                "F" => p.Position <= 2, _ => throw new ArgumentException($"pos is C, LW, RW, D, G or F, not {value}."),
            },
            "name" => p.Name.Contains(value, StringComparison.OrdinalIgnoreCase),
            "min" => p.Overall >= int.Parse(value),
            "max" => p.Overall <= int.Parse(value),
            "league" => book.Teams.TryGetValue(p.Club, out var t) && t.League == int.Parse(value),
            "active" => p.Active == value.ToLowerInvariant() is "1" or "yes" or "true",
            _ => throw new ArgumentException($"Unknown filter {key}. Use team, pos, name, min, max, league or active."),
        };
        if (!ok)
            return false;
    }
    return true;
}

static VirtualFileSystem Mount(string folder) => VirtualFileSystem.Mount(GameInstall.Open(folder));

// --model <file>: the full-ice rink model, checked before it goes anywhere.
static byte[]? FullIceModel(IList<string> options)
{
    var at = options.IndexOf("--model");
    if (at < 0 || at + 1 >= options.Count)
        return null;
    var bytes = File.ReadAllBytes(options[at + 1]);
    if (!Blueline.Core.Rinks.FullIce.IsFullIceModel(Blueline.Formats.Models.McdFile.Parse(bytes)))
        throw new InvalidDataException($"{options[at + 1]} does not stretch the centre ice logo over the rink; it is not the full-ice model.");
    return bytes;
}

static FfmpegTool Ffmpeg() => FfmpegTool.Locate(AppSettings.Load().FfmpegPath)
    ?? throw new InvalidOperationException("FFmpeg was not found. Install it (winget install Gyan.FFmpeg) or choose it in Studio's Settings.");

// Windows speech, one PowerShell run for every label: <folder>/<n>.wav.
static void Speak(string folder, IReadOnlyList<(int Index, string Text)> labels)
{
    var script = new System.Text.StringBuilder("Add-Type -AssemblyName System.Speech; $s = New-Object System.Speech.Synthesis.SpeechSynthesizer; $s.Rate = 1;" + Environment.NewLine);
    foreach (var (index, text) in labels)
        script.AppendLine($"$s.SetOutputToWaveFile('{Path.Combine(folder, $"{index}.wav").Replace("'", "''")}'); $s.Speak('{text.Replace("'", "''")}');");
    script.AppendLine("$s.Dispose()");
    var file = Path.Combine(folder, "speak.ps1");
    File.WriteAllText(file, script.ToString());
    using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell.exe", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", file]) { UseShellExecute = false, CreateNoWindow = true })!;
    process.WaitForExit();
    if (process.ExitCode != 0 || labels.Any(l => !File.Exists(Path.Combine(folder, $"{l.Index}.wav"))))
        throw new InvalidOperationException("Windows speech did not write every label.");
}

static Blueline.Formats.Textures.RgbaImage ReadTexture(VirtualFileSystem vfs, string path)
{
    var file = vfs.Find(path) ?? throw new KeyNotFoundException($"No such path: {path}");
    var texture = Blueline.Formats.Textures.GameTexture.TryOpen(vfs.ReadContent(file)) ?? throw new InvalidDataException($"{path} is not a texture this tool can read.");
    return texture.DecodeMip(0);
}

static (UniformSlots Slots, GearColours Gear) OpenSlots(VirtualFileSystem vfs, TeamCatalog catalog, ModProject? project)
{
    var data = project != null && project.TryGet(TeamCatalog.DatabasePath, out var stored) ? project.ReadBlob(stored) : vfs.ReadContent(vfs.Find(TeamCatalog.DatabasePath)!);
    var file = TdbFile.Parse((byte[])data.Clone());
    file.ApplyMeta(System.Text.Encoding.UTF8.GetString(vfs.ReadContent(vfs.Find(TeamCatalog.MetaPath)!)));
    var vault = vfs.Find(UniformSlots.VaultPath) is { } v ? vfs.ReadContent(v) : null;
    return (UniformSlots.Open(file, catalog, vault), GearColours.Open(file));
}

static byte[] ReadCurrent(VirtualFileSystem vfs, ModProject project, string path) =>
    project.TryGet(path, out var stored) ? project.ReadBlob(stored) : vfs.ReadContent(vfs.Find(path) ?? throw new KeyNotFoundException($"No such path: {path}"));

static UniformSlot SlotAt(UniformSlots slots, TeamInfo team, string number)
{
    var list = slots.SlotsOf(team);
    return int.TryParse(number, out var n) && n >= 1 && n <= list.Count ? list[n - 1]
        : throw new ArgumentException($"{team.Name} has slots 1 to {list.Count}; {number} is not one.");
}

static int LabelNumber(string text) =>
    int.TryParse(text, out var n) ? n
    : UniformSlots.Labels.ToList().FindIndex(l => l.Equals(text, StringComparison.OrdinalIgnoreCase)) is var i and >= 0 ? i
    : throw new ArgumentException($"Unknown label {text}; use one of: {string.Join(", ", UniformSlots.Labels)}.");

static bool YesNo(string text) => text.ToLowerInvariant() switch
{
    "yes" or "true" or "1" => true,
    "no" or "false" or "0" => false,
    _ => throw new ArgumentException($"Use yes or no, not {text}."),
};

static TeamCatalog OpenTeams(VirtualFileSystem vfs) =>
    TeamCatalog.Load(p => vfs.Find(p) is { } f ? vfs.ReadContent(f) : null, vfs.Files.Select(f => f.Path).ToList(), vfs.ReadStock);

static TdbFile OpenDatabase(VirtualFileSystem vfs, string path)
{
    // A roster save folder (like RosterCustom0) is read as the database it holds.
    var file = Directory.Exists(path) && RosterSave.IsRosterSave(path) ? null : vfs.Find(path) ?? throw new KeyNotFoundException($"No such path: {path}");
    var db = TdbFile.Parse(file == null ? RosterSave.Read(path).Database : vfs.ReadContent(file));
    var meta = (file != null && path.LastIndexOf('/') is > 0 and var slash ? vfs.Find(path[..slash] + "/nhl_game-meta.xml") : null) ?? vfs.Find("nhl/data/common/gamemodes/database/t3db/nhl_game-meta.xml");
    if (meta != null)
        db.ApplyMeta(System.Text.Encoding.UTF8.GetString(vfs.ReadContent(meta)));
    return db;
}

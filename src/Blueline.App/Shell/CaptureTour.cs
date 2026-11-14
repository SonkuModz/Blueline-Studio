using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Blueline.App.Modules;
using Blueline.Core;
using Blueline.Core.Overlays;
using Blueline.Formats.Textures;

namespace Blueline.App.Shell;

/// <summary>
/// A scripted walk through the real UI (`--capture folder`). It selects real assets and runs real edits in a
/// throwaway project, saving a PNG of each screen. The window renders offscreen, so it uses neither the
/// user's screen nor their game install beyond reads. It also serves as a UI smoke test: any failure is
/// written to tour.log and the run still finishes.
/// </summary>
public sealed class CaptureTour(MainWindow window, bool pressPlay = false)
{
    private readonly List<string> _log = [];
    private string _folder = "";

    public async Task RunAsync(string folder)
    {
        _folder = folder;
        Directory.CreateDirectory(folder);
        var services = window.Services;
        window.ToastsEnabled = false;
        try
        {
            if (services.Workspace.Vfs == null)
            {
                Note("No game mounted; capturing the empty state only.");
                await Shot("01-home");
                return;
            }

            await Step("01-home", () => Task.CompletedTask);

            var project = Path.Combine(folder, "tour-project");
            if (Directory.Exists(project))
                Directory.Delete(project, true);
            services.CreateProject(project, "Demo Mod");
            services.Settings.RecentProjects.Remove(project);
            services.Settings.Save();

            var assets = Module<AssetsModule>();
            await Step("02-assets-browse", async () =>
            {
                window.Navigate("Assets", null);
                await Idle();
                await Search(assets.BrowserControl, "screentitle .lua");
                Select(assets.BrowserControl);
                await Idle();
            });

            var textures = Module<TexturesModule>();
            var jersey = services.Index!.Find("nhl/data/kettle/rendering/characters/player/font_adidas_anaheim_home_cm.gnf")
                ?? services.Index.Rows.First(r => r.Category == AssetCategory.Texture);
            await Step("03-textures-preview", async () =>
            {
                window.Navigate("Textures", jersey.Path);
                await Idle();
                await WaitFor(() => textures.BrowserControl.Selected != null);
                await WaitFor(() => !textures.PreviewControl.IsDecoding);
                await Idle();
            });

            await Step("04-textures-import", async () =>
            {
                var source = Path.Combine(folder, "tour-source.png");
                File.WriteAllBytes(source, PngFile.Write(TestPattern(512, 512)));
                await textures.ImportFrom(source);
                await WaitForJobs();
                await WaitFor(() => !textures.PreviewControl.IsDecoding);
                await Idle();
            });

            async Task ShowTexture(string path)
            {
                window.Navigate("Textures", path);
                await Idle();
                await WaitFor(() => textures.BrowserControl.Selected?.Path == path);
                await WaitFor(() => !textures.PreviewControl.IsDecoding);
                await Idle();
            }

            await Step("04a-textures-greyscale", () => ShowTexture("nhl/data/common/postfx/burn_01.dds"));
            await Step("04b-textures-greyscale-imported", async () =>
            {
                await textures.ImportFrom(Path.Combine(folder, "tour-source.png"));
                await WaitForJobs();
                await WaitFor(() => !textures.PreviewControl.IsDecoding);
                await Idle();
            });
            await Step("04c-textures-float", () => ShowTexture("nhl/data/common/rendering/brdfggxlut.dds"));
            await Step("04d-textures-cubemap", () => ShowTexture("nhl/data/common/rendering/indoorsky.dds"));
            await Step("04e-textures-volume", () => ShowTexture("nhl/data/common/sample/loading_stadium.dds"));

            var teams = Module<TeamsModule>();
            await Step("05-teams", async () =>
            {
                window.Navigate("Teams", null);
                await WaitFor(() => !teams.IsLoading);
                await Idle();
                teams.SelectTeam("Toronto Maple Leafs");
                teams.SelectCategory(Core.Teams.TeamArtCategory.Logos);
                await TeamsSettled(teams);
            });

            await Step("05b-teams-center-ice", async () =>
            {
                teams.SelectCategory(Core.Teams.TeamArtCategory.CenterIce);
                await TeamsSettled(teams);
            });

            await Step("05c-teams-uniforms", async () =>
            {
                teams.SelectCategory(Core.Teams.TeamArtCategory.Uniforms);
                teams.SelectAsset("nhl/data/kettle/rendering/characters/player/font_adidas_toronto_home_cm.gnf");
                await TeamsSettled(teams);
            });

            await Step("05d-teams-arena", async () =>
            {
                teams.SelectCategory(Core.Teams.TeamArtCategory.ArenaArt);
                await TeamsSettled(teams);
            });

            await Step("05e-teams-other-league", async () =>
            {
                teams.SelectTeam("Adler Mannheim");
                teams.SelectCategory(Core.Teams.TeamArtCategory.CenterIce);
                await TeamsSettled(teams);
            });

            await Step("05f-teams-logo-set", async () =>
            {
                teams.SelectTeam("Anaheim Ducks");
                teams.SelectCategory(Core.Teams.TeamArtCategory.Logos);
                await TeamsSettled(teams);
                var logo = Path.Combine(folder, "tour-logo.png");
                File.WriteAllBytes(logo, PngFile.Write(TestDisc(512)));
                await teams.ReplaceLogoSet(logo);
                await WaitForJobs();
                await TeamsSettled(teams);
            });

            await Step("05g-teams-from-textures", async () =>
            {
                window.Navigate("Teams", "nhl/data/kettle/rendering/stadiums/common/ice_centerlogo_15_cm.gnf");
                await TeamsSettled(teams);
            });

            await Step("05h-teams-export-import", async () =>
            {
                var before = services.Workspace.Project!.ChangeCount;
                var pack = Path.Combine(folder, "tour-pack");
                await teams.ExportPaths([.. teams.ShownTiles.Where(t => t.Art.Section == "Center ice logo").Select(t => t.Art.Path)], pack);
                await WaitForJobs();
                Note($"Exported {Directory.EnumerateFiles(pack, "*.png", SearchOption.AllDirectories).Count()} team images");
                await teams.ImportFolder(pack);
                await WaitForJobs();
                Note($"Import folder changed {services.Workspace.Project.ChangeCount - before} files");
                await TeamsSettled(teams);
            });

            await Step("05i-teams-reverted", async () =>
            {
                var before = services.Workspace.Project!.ChangeCount;
                teams.RevertTeam();
                await TeamsSettled(teams);
                Note($"Revert team removed {before - services.Workspace.Project.ChangeCount} files");
                Directory.Delete(Path.Combine(folder, "tour-pack"), true);
            });

            if (services.Workspace.Vfs.Install.ModsFolder != null)
                await Step("05j-teams-modded", async () =>
                {
                    teams.SelectTeam("Kraken");
                    teams.SelectCategory(Core.Teams.TeamArtCategory.Uniforms);
                    await TeamsSettled(teams);
                    var all = teams.ShownTiles.Count;
                    teams.ModsOnly.IsChecked = true;
                    teams.ModsOnly.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    await TeamsSettled(teams);
                    var strays = teams.ShownTiles.Count(t => services.Workspace.Vfs.Find(t.Art.Path)?.IsFromModsFolder != true);
                    Note($"Mods only: {all} uniforms down to {teams.ShownTiles.Count}, {strays} not from the mods folder");
                });

            async Task UniformSettled()
            {
                await TeamsSettled(teams);
                await WaitFor(() => !teams.UniformBusy);
                await Idle();
            }

            await Step("05k-uniform-3d-front", async () =>
            {
                teams.ModsOnly.IsChecked = false;
                teams.SelectTeam("Toronto Maple Leafs");
                teams.SelectCategory(Core.Teams.TeamArtCategory.Uniforms);
                teams.SelectAsset("nhl/data/kettle/rendering/characters/player/jersey_adidas_toronto_home_cm.gnf");
                teams.ShowUniform(true);
                await UniformSettled();
            });

            await Step("05l-uniform-3d-back", async () =>
            {
                teams.UniformView.SetView(Math.PI + 0.45, 0.12);
                await Idle();
            });

            await Step("05m-uniform-3d-away", async () =>
            {
                teams.SelectAsset("nhl/data/kettle/rendering/characters/player/jersey_adidas_toronto_away_cm.gnf");
                teams.UniformView.SetView(0.45, 0.12);
                await UniformSettled();
            });

            await Step("05x1-gear-colours", async () =>
            {
                teams.ShowUniform(false);
                teams.SelectTeam("St. Louis Blues");
                await TeamsSettled(teams);
                teams.ShowGearColours(true);
                await Idle();
            });

            await Step("05x2-gear-colours-own-row", async () =>
            {
                // Away gloves share the fallback row with Home; giving them their own row lets them differ.
                var panel = teams.GearPanel;
                panel.KitBox.SelectedItem = panel.KitBox.Items.Cast<Core.Teams.GearKit>().First(k => k.Label == "Away");
                await Idle();
                panel.GiveOwnRow((Core.Teams.GearKit)panel.KitBox.SelectedItem, Core.Teams.GearPart.Gloves);
                panel.SetKitMain(Core.Teams.GearPart.Gloves, 0xFFFFFF);
                await Idle();
                panel.SaveToProject();
                await Idle();
            });

            await Step("05x3-gear-colours-closed", async () =>
            {
                teams.ShowGearColours(false);
                await Idle();
            });

            await Step("05x4-uniform-3d-gear-away", async () =>
            {
                // The saved edit gives St. Louis white Away gloves; the 3D view should draw them with the kit.
                teams.SelectCategory(Core.Teams.TeamArtCategory.Uniforms);
                teams.SelectAsset("nhl/data/kettle/rendering/characters/player/jersey_adidas_stlouis_away_cm.gnf");
                teams.ShowUniform(true);
                await UniformSettled();
            });

            await Step("05x5-uniform-3d-gear-home", async () =>
            {
                teams.SelectAsset("nhl/data/kettle/rendering/characters/player/jersey_adidas_stlouis_home_cm.gnf");
                await UniformSettled();
            });

            await Step("05x6-studio-glove-zone", async () =>
            {
                // Zone 7 of the CCM glove is its logo (seen in game): choosing it should light it up and bring the camera to it.
                teams.GearPanel.SelectZone(Core.Teams.GearPart.Gloves, 7);
                await UniformSettled();
                Note($"Selected zone: {teams.GearPanel.SelectedZone}");
            });

            await Step("05x7-studio-helmet-pick", async () =>
            {
                // A click on the helmet selects the zone under the mouse.
                teams.GearPanel.SelectZone(Core.Teams.GearPart.Gloves, 0);
                teams.Studio.ShowView("helmet");
                await UniformSettled();
                var view = teams.UniformView;
                var picked = view.PickAt(new System.Windows.Point(view.ActualWidth / 2, view.ActualHeight * 0.3));
                await UniformSettled();
                Note($"Picked the helmet shell: {picked}, selected zone: {teams.GearPanel.SelectedZone}");
            });

            await Step("05x8-studio-front", async () =>
            {
                teams.GearPanel.SelectZone(Core.Teams.GearPart.Helmet, 0);
                teams.Studio.ShowView("front");
                await UniformSettled();
            });

            if (services.Workspace.Vfs.Install.ModsFolder != null)
                await Step("05n-uniform-3d-modded", async () =>
                {
                    teams.SelectTeam("Kraken");
                    teams.SelectCategory(Core.Teams.TeamArtCategory.Uniforms);
                    await UniformSettled();
                });

            await Step("05o-uniform-3d-edit", async () =>
            {
                // A test pattern on the sheet shows how the UVs lay it out, and that an edit reaches the mesh.
                teams.SelectTeam("Toronto Maple Leafs");
                teams.SelectCategory(Core.Teams.TeamArtCategory.Uniforms);
                teams.SelectAsset("nhl/data/kettle/rendering/characters/player/jersey_adidas_toronto_home_cm.gnf");
                await UniformSettled();
                var pattern = Path.Combine(folder, "tour-source.png");
                await teams.ReplaceSelected(pattern);
                await WaitForJobs();
                await UniformSettled();
                teams.SetUniformOptions(Core.Teams.UniformSize.Small, Core.Teams.UniformTuck.Full, Core.Teams.UniformPants.Short);
                await UniformSettled();
            });

            await Step("05p-uniform-3d-reverted", async () =>
            {
                var before = services.Workspace.Project!.ChangeCount;
                teams.RevertTeam();
                teams.SetUniformOptions(Core.Teams.UniformSize.Large, Core.Teams.UniformTuck.None, Core.Teams.UniformPants.Long);
                await UniformSettled();
                teams.ShowUniform(false);
                await Idle();
                Note($"Uniform edit reverted: {before - services.Workspace.Project.ChangeCount} files");
            });

            var maps = Module<JerseyMapsModule>();
            async Task MapsSettled()
            {
                await Idle();
                await WaitFor(() => !maps.IsBusy);
                await Idle();
            }

            await Step("05q-jersey-maps-normal", async () =>
            {
                window.Navigate("Jersey Maps", null);
                await Idle();
                maps.SelectJersey("nhl/data/kettle/rendering/characters/player/jersey_adidas_toronto_home_cm.gnf");
                await WaitFor(() => maps.HasMaps);
                await MapsSettled();
                maps.ShowView("Normal");
                await Idle();
            });

            await Step("05r-jersey-maps-raised", async () =>
            {
                maps.ShowView("Raised");
                await Idle();
            });

            await Step("05s-jersey-maps-coeff", async () =>
            {
                maps.ShowView("Coeff");
                await Idle();
            });

            await Step("05t-jersey-maps-reebok", async () =>
            {
                maps.SelectJersey("nhl/data/kettle/rendering/characters/player/jersey_rbk_rochester_away_cm.gnf");
                await Idle();
                await WaitFor(() => maps.HasMaps && !maps.IsBusy);
                maps.ShowView("Normal");
                await MapsSettled();
            });

            await Step("05u-jersey-maps-saved", async () =>
            {
                var before = services.Workspace.Project!.ChangeCount;
                await maps.ApplyAsync();
                await WaitForJobs();
                await MapsSettled();
                Note($"Jersey maps saved: {services.Workspace.Project.ChangeCount - before} files");
                maps.ShowView("GameNormal");
                await Idle();
            });

            {
                var before = services.Workspace.Project!.ChangeCount;
                services.Workspace.Project.Revert(
                    ["nhl/data/kettle/rendering/characters/player/jersey_rbk_rochester_away_nm.gnf", "nhl/data/kettle/rendering/characters/player/jersey_rbk_rochester_away_coeff.gnf"],
                    "Tour: revert jersey maps");
                Note($"Jersey maps reverted: {before - services.Workspace.Project.ChangeCount} files");
                await MapsSettled();
            }

            const string bostonHome = "nhl/data/kettle/rendering/characters/player/jersey_adidas_boston_home_cm.gnf";
            await Step("05v-teams-jersey-maps-button", async () =>
            {
                window.Navigate("Teams", null);
                await Idle();
                teams.SelectTeam("Boston Bruins");
                teams.SelectCategory(Core.Teams.TeamArtCategory.Uniforms);
                await Idle();
                teams.SelectAsset(bostonHome);
                await Idle();
                Note($"Make maps button on {bostonHome}: {teams.JerseyMapsButton.Visibility}");
            });

            await Step("05w-jersey-maps-from-teams", async () =>
            {
                teams.MakeJerseyMaps();
                await Idle();
                await WaitFor(() => maps.HasMaps && !maps.IsBusy && maps.NameText.Text == "jersey_adidas_boston_home");
                await MapsSettled();
                Note($"From Teams, Jersey Maps shows {maps.NameText.Text}, maps made: {maps.HasMaps}");
            });

            var models = Module<ModelsModule>();
            await Step("06-models", async () =>
            {
                window.Navigate("Models", null);
                await Idle();
                await Search(models.BrowserControl, "anaheim_duck_hat_lod0");
                Select(models.BrowserControl);
                await Idle();
                await WaitFor(() => !models.IsLoading);
                await Idle();
            });

            var text = Module<TextModule>();
            await Step("07-text", async () =>
            {
                window.Navigate("Text", null);
                await WaitFor(() => !text.IsLoading && text.VisibleRows.Count > 0);
                text.SetSearch("PressStart");
                text.SelectFirst();
                await Idle();
                text.ApplyEdit("PressStart", "BLUELINE");
                text.SelectFirst();
                await Idle();
            });

            var database = Module<DatabaseModule>();
            await Step("08-database", async () =>
            {
                window.Navigate("Database", null);
                await WaitFor(() => !database.IsLoading && database.File_ != null);
                database.SelectTable("exhibitionteams");
                await Idle();
            });

            var audio = Module<AudioModule>();
            await Step("09-audio", async () =>
            {
                window.Navigate("Audio", null);
                await Idle();
                await Search(audio.BrowserControl, "music .sbs");
                Select(audio.BrowserControl);
                await WaitFor(() => !audio.IsLoading);
                if (audio.SampleList.Items.Count > 0)
                    audio.SampleList.SelectedIndex = 0;
                await Idle();
            });

            const string movies = "nhl/data/common/movie/";
            var video = Module<VideoModule>();
            async Task VideoReady()
            {
                await Idle();
                await WaitFor(() => !video.IsLoading && !video.PlayerControl.IsPreparing, 120);
                await WaitFor(() => video.PlayerControl.IsReady, 20);
                await Idle();
            }

            await Step("09b-video-mkv", async () =>
            {
                window.Navigate("Video", movies + "bootflow/press_start_reveal.mkv");
                await VideoReady();
                video.PlayerControl.Play();
                await Task.Delay(2500);
                Note($"Playing {video.Info?.Summary}: decoded width {video.PlayerControl.VideoWidth}, position {video.PlayerControl.Position.TotalSeconds:F1} s");
                video.PlayerControl.Pause();
                await Idle();
            });

            var commentary = services.Index!.Rows.First(r => r.Path.Contains("/commentary/context_", StringComparison.Ordinal) && r.Path.EndsWith(".mkv", StringComparison.Ordinal));
            await Step("09c-video-matte", async () =>
            {
                window.Navigate("Video", commentary.Path);
                await VideoReady();
                await video.ShowTrack(VideoTrack.Matte);
                await VideoReady();
                Note($"Matte of {commentary.Name}: decoded width {video.PlayerControl.VideoWidth}");
            });

            await Step("09d-video-vp6", async () =>
            {
                window.Navigate("Video", movies + "ones/intro_logo_cove.vp6");
                await VideoReady();
                Note($"VP6: {video.Info?.Summary}; replace label {video.ReplaceButton.Content}");
                await video.ShowTrack(VideoTrack.Matte);
                await VideoReady();
                Note($"VP6 matte: decoded width {video.PlayerControl.VideoWidth}");
            });

            await Step("09e-video-replaced", async () =>
            {
                const string target = movies + "threes/threes_europe_in.mkv";
                window.Navigate("Video", target);
                await VideoReady();
                var clip = Path.Combine(folder, "tour-clip.mp4");
                var maker = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(services.Ffmpeg!.Executable,
                    ["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc=size=1280x720:rate=30:duration=1", "-pix_fmt", "yuv420p", clip]) { CreateNoWindow = true, UseShellExecute = false })!;
                await maker.WaitForExitAsync();
                var before = services.Workspace.Project!.ChangeCount;
                await video.ReplaceFrom(clip);
                await WaitForJobs();
                await VideoReady();
                Note($"Video replaced: {services.Workspace.Project.ChangeCount - before} file; now {video.Info?.Summary}");
            });
            {
                var before = services.Workspace.Project!.ChangeCount;
                services.Workspace.Project.Revert([movies + "threes/threes_europe_in.mkv"], "Tour: revert video");
                Note($"Video reverted: {before - services.Workspace.Project.ChangeCount} file");
                await Idle();
            }

            var png = services.Index.Rows.First(r => r.Path.EndsWith("saveload/000/icon0.png", StringComparison.Ordinal));
            await Step("09f-assets-png", async () =>
            {
                window.Navigate("Assets", png.Path);
                await Idle();
                await Idle();
            });

            await Step("09g-assets-jpg", async () =>
            {
                window.Navigate("Assets", "nhl/data/kettle/online/session.jpg");
                await Idle();
                await Idle();
            });

            await Step("09h-assets-font", async () =>
            {
                window.Navigate("Assets", "nhl/data/common/font/dinengschriftstd.ttf");
                await Idle();
                await Idle();
                Note($"Font preview drawn: {assets.FontControl.Visibility}, {assets.FontControl.Description}");
            });

            await Step("09i-assets-video", async () =>
            {
                window.Navigate("Assets", movies + "bootflow/press_start_reveal.mkv");
                await Idle();
                await WaitFor(() => assets.VideoControl.IsReady, 60);
                await Idle();
            });
            assets.VideoControl.Clear();

            await Step("09j-textures-png", async () =>
            {
                window.Navigate("Textures", png.Path);
                await Idle();
                await Idle();
                Note($"Textures on a PNG: {textures.SummaryLine}");
            });

            var overlays = Module<OverlaysModule>();
            async Task OverlaysSettled()
            {
                await Idle();
                await WaitFor(() => !overlays.IsBusy && !overlays.ThumbsBusy);
                await Idle();
            }

            async Task Type(System.Windows.Controls.TextBox? box, string value)
            {
                box!.Text = value;
                box.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
                await OverlaysSettled();
            }

            const string clockLayout = "nhl/ui/features/overlays/vvms/scoreclock/scoreclockoverlay.layout";
            await Step("09k-overlays-layout", async () =>
            {
                window.Navigate("Overlays", null);
                await WaitFor(() => overlays.Catalog != null);
                overlays.Select("ScoreClock");
                await OverlaysSettled();
                await WaitFor(() => overlays.CanvasControl.DrawnCount > 0);
                await Idle();
                Note($"Overlays: {overlays.Catalog!.Overlays.Count}; score clock drew {overlays.CanvasControl.DrawnCount} widgets from {overlays.SelectedLayout}");
            });

            await Step("09k2-overlays-close-up", async () =>
            {
                overlays.SetCloseUp(true);
                await OverlaysSettled();
            });

            await Step("09k3-overlays-other-teams", async () =>
            {
                overlays.SetTeams("Calgary Flames", "Vancouver Canucks");
                await OverlaysSettled();
                await WaitFor(() => overlays.CanvasControl.DrawnCount > 0);
                await Idle();
            });

            await Step("09k4-overlays-hidden-parts", async () =>
            {
                overlays.SetShowHidden(true);
                await OverlaysSettled();
                Note($"With hidden parts the score clock drew {overlays.CanvasControl.DrawnCount} widgets");
            });

            async Task MomentStep(string name, Blueline.Core.Overlays.ScoreClockEvent moment, int frame) => await Step(name, async () =>
            {
                overlays.SetMoment(moment);
                if (moment != Blueline.Core.Overlays.ScoreClockEvent.InPlay)
                {
                    await WaitFor(() => overlays.MomentFrameCount > 0);
                    overlays.SetMovieFrame(frame);
                }
                await OverlaysSettled();
                await Task.Delay(200);
            });

            overlays.SetShowHidden(false);
            await MomentStep("09k5-overlays-clock-slides-in", Blueline.Core.Overlays.ScoreClockEvent.ShowClock, 22);
            await MomentStep("09k6-overlays-goal-build", Blueline.Core.Overlays.ScoreClockEvent.GoalHome, 34);
            await MomentStep("09k7-overlays-goal", Blueline.Core.Overlays.ScoreClockEvent.GoalHome, 95);
            await Step("09k7b-overlays-goal-playing", async () =>
            {
                overlays.SetMoment(Blueline.Core.Overlays.ScoreClockEvent.GoalHome);
                await WaitFor(() => overlays.MomentFrameCount > 0);
                overlays.PressPlay();
                var started = DateTime.UtcNow;
                await Task.Delay(2000);
                Note($"Goal playing: frame {overlays.MomentFrame} of {overlays.MomentFrameCount} after {(DateTime.UtcNow - started).TotalSeconds:0.00} s, playing {overlays.MomentPlaying}");
                await WaitFor(() => !overlays.MomentPlaying, 15);
                Note($"Goal finished after {(DateTime.UtcNow - started).TotalSeconds:0.00} s on frame {overlays.MomentFrame}");
            });
            await MomentStep("09k8-overlays-goal-away", Blueline.Core.Overlays.ScoreClockEvent.GoalAway, 150);
            await MomentStep("09k9-overlays-power-play", Blueline.Core.Overlays.ScoreClockEvent.PowerPlayAway, 55);
            await MomentStep("09k10-overlays-5-on-3", Blueline.Core.Overlays.ScoreClockEvent.TwoManAdvantage, 55);
            await MomentStep("09k11-overlays-four-on-four", Blueline.Core.Overlays.ScoreClockEvent.FourOnFour, 55);
            await MomentStep("09k12-overlays-delayed-penalty", Blueline.Core.Overlays.ScoreClockEvent.DelayedPenaltyHome, 55);
            await MomentStep("09k13-overlays-two-line", Blueline.Core.Overlays.ScoreClockEvent.PenaltyDetailsAway, 70);
            await MomentStep("09k14-overlays-full-width", Blueline.Core.Overlays.ScoreClockEvent.FullWidthMessage, 100);
            await MomentStep("09k15-overlays-icing", Blueline.Core.Overlays.ScoreClockEvent.ClockWarning, 50);
            await MomentStep("09k16-overlays-comparison", Blueline.Core.Overlays.ScoreClockEvent.Comparison, 90);
            await MomentStep("09k17-overlays-shootout", Blueline.Core.Overlays.ScoreClockEvent.Shootout, 50);
            overlays.SetMoment(Blueline.Core.Overlays.ScoreClockEvent.InPlay);

            await Step("09l-overlays-widget-moved", async () =>
            {
                overlays.SetShowHidden(false);
                await OverlaysSettled();
                overlays.SelectWidget("clock");
                await Idle();
                var before = overlays.CanvasControl.Composite!.Pixels.ToArray();
                var from = overlays.CanvasControl.PositionOf("clock");
                await Type(overlays.WidgetEditorControl.BoxFor("x"), "380.0");
                Note($"Clock drawn at {from} then {overlays.CanvasControl.PositionOf("clock")}; picture changed {!before.AsSpan().SequenceEqual(overlays.CanvasControl.Composite!.Pixels)}");
                Note($"Moved clock: edited {services.Workspace.IsEdited(clockLayout)}, x in file {System.Text.Encoding.UTF8.GetString(services.Workspace.ReadCurrent(clockLayout)!).Contains("\"x\": 380.0")}");
            });

            await Step("09m-overlays-textures", async () =>
            {
                overlays.ShowPage("Textures");
                overlays.ChooseArt(0);
                await OverlaysSettled();
                await WaitFor(() => !overlays.ArtPreviewControl.IsDecoding);
                await Idle();
                Note($"Score clock textures shown: {overlays.ArtShown}");
            });

            byte[]? pictureBeforeImport = null;
            await Step("09n-overlays-texture-imported", async () =>
            {
                pictureBeforeImport = overlays.CanvasControl.Composite?.Pixels.ToArray();
                await overlays.ImportArt(Path.Combine(folder, "tour-source.png"));
                await WaitForJobs();
                await OverlaysSettled();
                await WaitFor(() => !overlays.ArtPreviewControl.IsDecoding);
                await Idle();
            });

            await Step("09o-overlays-layout-new-art", async () =>
            {
                overlays.ShowPage("Layout");
                await OverlaysSettled();
                await WaitFor(() => overlays.CanvasControl.DrawnCount > 0);
                await Idle();
                Note($"Imported texture reached the preview: {pictureBeforeImport != null && !pictureBeforeImport.AsSpan().SequenceEqual(overlays.CanvasControl.Composite!.Pixels)}");
            });

            await Step("09p-overlays-settings", async () =>
            {
                overlays.Select("GoalScoredThrees");
                await OverlaysSettled();
                overlays.ShowPage("Settings");
                await Idle();
                await Type(overlays.DefinitionEditorControl.BoxFor("lifetime"), "8");
                var saved = JsonEdit.Parse(services.Workspace.ReadCurrent(OverlayCatalog.DefinitionPath)!)["overlays"]!.AsArray().First(o => o!["name"]!.ToString() == "GoalScoredThrees")!["lifetime"]!.ToJsonString();
                Note($"GoalScoredThrees lifetime now {saved}");
            });

            await Step("09q-overlays-files", async () =>
            {
                overlays.Select("ScoreClock");
                await OverlaysSettled();
                overlays.ShowPage("Files");
                overlays.ChooseFile("scoreclock.nav");
                await Idle();
                var good = overlays.FileTextControl.Text;
                overlays.FileTextControl.Text = good + " not json {";
                overlays.SaveFileText();
                Note($"Broken JSON: {overlays.FileProblemText}; edited {services.Workspace.IsEdited("nhl/ui/features/overlays/scoreclock.nav")}");
                await Idle();
            });

            await Step("09r-overlays-movie-art", async () =>
            {
                overlays.Select("Robocam");
                await OverlaysSettled();
                overlays.ShowPage("Files");
                overlays.ChooseFile("robocam.gfx");
                await Idle();
                Note($"Robocam textures: {overlays.ArtShown}");
            });

            await Step("09s-overlays-movie-textures", async () =>
            {
                overlays.ShowPage("Textures");
                overlays.ChooseArt(0);
                await OverlaysSettled();
                await WaitFor(() => !overlays.ArtPreviewControl.IsDecoding);
                await Idle();
            });

            async Task MovieStep(string step, string overlay, string movie, int? frame = null)
            {
                await Step(step, async () =>
                {
                    overlays.Select(overlay);
                    await OverlaysSettled();
                    overlays.ShowPage("Layout");
                    overlays.ChooseLayout(movie);
                    await OverlaysSettled();
                    await WaitFor(() => overlays.MovieFrameCount > 0);
                    if (frame != null)
                        overlays.SetMovieFrame(frame.Value);
                    await OverlaysSettled();
                    await Idle();
                    Note($"{movie}: {overlays.MovieFrameCount} frames, {overlays.CanvasControl.MovieLayerCount} pieces on screen");
                });
            }

            await MovieStep("09t-overlays-movie-robocam", "Robocam", "robocam.gfx", 40);
            await MovieStep("09u-overlays-movie-period", "EndOfPeriod", "endofperiod.gfx");
            await MovieStep("09v-overlays-movie-goal", "GoalScoredThrees", "goalscored.gfx");
            async Task OverlayStep(string step, string overlay)
            {
                await Step(step, async () =>
                {
                    overlays.Select(overlay);
                    await OverlaysSettled();
                    await WaitFor(() => overlays.CanvasControl.DrawnCount > 0);
                    await Idle();
                    Note($"{overlay}: {overlays.SelectedLayout}, {overlays.CanvasControl.DrawnCount} widgets");
                });
            }

            overlays.SetCloseUp(false);
            await OverlayStep("09w-overlays-team-stats", "TeamStats");
            await OverlayStep("09x-overlays-replay", "InstantReplay");

            await Step("09y1-sportsnet-look", async () =>
            {
                window.Navigate("Overlays", null);
                await WaitFor(() => overlays.Catalog != null);
                overlays.Select("ScoreClock");
                await OverlaysSettled();
                overlays.SetTeams("Calgary Flames", "Vancouver Canucks");
                overlays.SetCloseUp(true);
                await WaitFor(() => overlays.CanvasControl.DrawnCount > 0);
                await overlays.ApplySportsnetLook();
                await WaitFor(() => !overlays.IsBusy, 30);
                await OverlaysSettled();
                await WaitFor(() => overlays.CanvasControl.DrawnCount > 0);
                await Idle();
                Note($"Sportsnet look: layout edited {services.Workspace.IsEdited(clockLayout)}, drew {overlays.CanvasControl.DrawnCount} widgets");
            });
            await MomentStep("09y2-sportsnet-goal-wipe", Blueline.Core.Overlays.ScoreClockEvent.GoalHome, 34);
            await MomentStep("09y3-sportsnet-goal", Blueline.Core.Overlays.ScoreClockEvent.GoalHome, 95);
            await MomentStep("09y4-sportsnet-power-play", Blueline.Core.Overlays.ScoreClockEvent.PowerPlayAway, 55);
            await MomentStep("09y5-sportsnet-delayed-penalty", Blueline.Core.Overlays.ScoreClockEvent.DelayedPenaltyHome, 55);
            await MomentStep("09y6-sportsnet-clock-in", Blueline.Core.Overlays.ScoreClockEvent.ShowClock, 22);
            await MomentStep("09y7-sportsnet-in-play", Blueline.Core.Overlays.ScoreClockEvent.InPlay, 0);

            await Step("09z1-flat-before", async () =>
            {
                window.Navigate("Overlays", null);
                await WaitFor(() => overlays.Catalog != null);
                overlays.Select("ScoreClock");
                await OverlaysSettled();
                overlays.SetTeams("Calgary Flames", "Vancouver Canucks");
                overlays.SetCloseUp(true);
                overlays.SetMoment(Blueline.Core.Overlays.ScoreClockEvent.InPlay);
                await WaitFor(() => overlays.CanvasControl.DrawnCount > 0);
                await Idle();
            });
            await Step("09z2-flat-after", async () =>
            {
                await overlays.ApplyFlatLook();
                await WaitFor(() => !overlays.IsBusy, 60);
                await OverlaysSettled();
                await WaitFor(() => overlays.CanvasControl.DrawnCount > 0);
                await Idle();
                Note($"Flat look: {services.Workspace.Project!.Changes.Count} changed files");
            });
            await MomentStep("09z3-flat-power-play", Blueline.Core.Overlays.ScoreClockEvent.PowerPlayAway, 55);
            await MomentStep("09z4-flat-goal", Blueline.Core.Overlays.ScoreClockEvent.GoalHome, 95);
            await MomentStep("09z5-flat-delayed-penalty", Blueline.Core.Overlays.ScoreClockEvent.DelayedPenaltyHome, 55);
            await MomentStep("09z6-flat-full-width", Blueline.Core.Overlays.ScoreClockEvent.FullWidthMessage, 100);
            await MomentStep("09z7-flat-clock-in", Blueline.Core.Overlays.ScoreClockEvent.ShowClock, 22);

            // UI revamp: both themes, then a page transition captured part way through.
            await Step("12a-theme-dark-home", async () =>
            {
                window.Navigate("Settings", null);
                await Idle();
            });
            await Step("12b-theme-light-home", async () =>
            {
                window.SwitchTheme(Blueline.Core.UiPalette.Light, animate: false, save: false);
                await Idle();
                Note($"Light theme applied: {ThemeManager.Current.Name}");
            });
            await Step("12c-theme-light-textures", async () =>
            {
                window.Navigate("Textures", null);
                await Idle();
            });
            await Step("12d-theme-light-teams", async () =>
            {
                window.Navigate("Teams", null);
                await Idle();
            });
            window.SwitchTheme(Blueline.Core.UiPalette.Dark, animate: false, save: false);

            async Task Filmstrip(string name, string from, string to, int[] atMs)
            {
                window.Navigate(from, null);
                await Idle();
                Motion.ForceInstant = false;
                window.Navigate(to, null);

                // The window is off screen, where WPF does not tick animations evenly, so each picture is taken at an exact moment instead.
                window.PausePageMovement();
                foreach (var ms in atMs)
                {
                    window.SeekPageMovement(ms);
                    await Idle();
                    ShotNow($"{name}-{ms:000}ms");
                }
                window.ResumePageMovement();
                await WaitFor(() => !window.IsPageMoving, 5);
                Note($"{name}: finished moving {!window.IsPageMoving}");
                Motion.ForceInstant = true;
            }

            // The start screen, in both themes, as it arrives, and with a picture of the user's choice behind it.
            await Step("13a-start-screen", async () =>
            {
                window.ShowStart();
                await Idle();
            });
            await Step("13b-start-screen-light", async () =>
            {
                window.SwitchTheme(Blueline.Core.UiPalette.Light, animate: false, save: false);
                await Idle();
            });
            window.SwitchTheme(Blueline.Core.UiPalette.Dark, animate: false, save: false);
            await Step("13e-start-screen-recents", async () =>
            {
                // Shows the user's real projects as recents for one picture. Nothing is saved.
                var before = services.Settings.RecentProjects.ToList();
                try
                {
                    var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Blueline Projects");
                    services.Settings.RecentProjects.Clear();
                    if (Directory.Exists(folder))
                        services.Settings.RecentProjects.AddRange(Directory.GetDirectories(folder).Where(d => File.Exists(Path.Combine(d, "project.json")) || Directory.GetFiles(d, "*.json").Length > 0).Take(5));
                    window.StartScreenView.RefreshRecent();
                    await Idle();
                    ShotNow("13e-start-screen-recents-shot");
                    Note($"Start screen recents shown: {services.Settings.RecentProjects.Count}");
                }
                finally
                {
                    services.Settings.RecentProjects.Clear();
                    services.Settings.RecentProjects.AddRange(before);
                    window.StartScreenView.RefreshRecent();
                }
            });
            await Step("13c-start-screen-wallpaper", async () =>
            {
                var picture = Path.Combine(_folder, "tour-source.png");
                var before = services.Settings.StartBackground;
                try
                {
                    services.Settings.StartBackground = picture;
                    window.StartScreenView.ApplyBackground();
                    await Idle();
                    ShotNow("13c-start-screen-wallpaper-shot");
                }
                finally
                {
                    services.Settings.StartBackground = before;
                    window.StartScreenView.ApplyBackground();
                }
            });
            {
                Motion.ForceInstant = false;
                window.ShowStart();
                window.StartScreenView.PauseMovement();
                foreach (var ms in new[] { 0, 150, 300, 450, 700, 1000 })
                {
                    window.StartScreenView.SeekMovement(ms);
                    await Idle();
                    ShotNow($"13d-start-entrance-{ms:0000}ms");
                }
                window.StartScreenView.ResumeMovement();
                await WaitFor(() => !window.StartScreenView.IsMoving, 5);
                Motion.ForceInstant = true;
                await window.StartScreenView.CloseAsync();
            }

            await Filmstrip("12e-transition-forward", "Assets", "Teams", [0, 40, 80, 120, 160, 220]);
            await Filmstrip("12f-transition-back", "Teams", "Assets", [0, 40, 80, 120, 160, 220]);

            // The grouped tab bar: where the blue line sits, the wide layout with group names, light theme, and the line sliding.
            await Step("14a-tabbar-home", async () =>
            {
                window.Navigate("Settings", null);
                await Idle();
                Note($"Tab shortcuts: {window.InputBindings.Count} bindings; tabs in order: {string.Join(", ", window.Modules.Select(m => m.Title))}");
            });
            await Step("14b-tabbar-overlays", async () =>
            {
                window.Navigate("Overlays", null);
                await Idle();
            });
            await Step("14c-tabbar-wide", async () =>
            {
                var before = window.Width;
                window.Width = 1840;
                await Idle();
                window.Navigate("Build", null);
                await Idle();
                ShotNow("14c-tabbar-wide-shot");
                window.Width = before;
                await Idle();
            });
            await Step("14d-tabbar-light", async () =>
            {
                window.SwitchTheme(Blueline.Core.UiPalette.Light, animate: false, save: false);
                window.Navigate("Teams", null);
                await Idle();
            });
            window.SwitchTheme(Blueline.Core.UiPalette.Dark, animate: false, save: false);
            await Filmstrip("14e-indicator-forward", "Assets", "Overlays", [0, 40, 80, 120, 160, 220]);
            await Filmstrip("14f-indicator-back", "Play", "Textures", [0, 40, 80, 120, 160, 220]);



            // The Ctrl+K palette: open, tabs and actions, game files, nothing found, a result really run, and in light.
            string Listing() => string.Join(" | ", window.PaletteView.Items.Take(7).Select(i => $"{i.Kind}:{i.Title}"));
            await Step("17a-palette-open", async () =>
            {
                window.Navigate("Assets", null);
                await Idle();
                window.OpenPalette();
                await Idle();
                Note($"Palette open with {window.PaletteView.ResultCount} results, chosen: {window.PaletteView.SelectedTitle}");
            });
            await Step("17b-palette-tabs", async () =>
            {
                window.PaletteView.SetQuery("tex");
                await Idle();
                Note($"'tex' gives {window.PaletteView.ResultCount}: {Listing()}");
            });
            await Step("17c-palette-files", async () =>
            {
                window.PaletteView.SetQuery("burn 01");
                await Idle();
                Note($"'burn 01' gives {window.PaletteView.ResultCount}: {Listing()}");
            });
            await Step("17d-palette-nothing", async () =>
            {
                window.PaletteView.SetQuery("zzzzqq");
                await Idle();
                Note($"'zzzzqq' gives {window.PaletteView.ResultCount} results");
            });
            await Step("17e-palette-light", async () =>
            {
                window.SwitchTheme(Blueline.Core.UiPalette.Light, animate: false, save: false);
                window.PaletteView.SetQuery("over");
                await Idle();
            });
            window.SwitchTheme(Blueline.Core.UiPalette.Dark, animate: false, save: false);
            await Step("17f-palette-run", async () =>
            {
                window.PaletteView.SetQuery("go to teams");
                await Idle();
                var ran = window.PaletteView.RunSelected();
                await Idle();
                Note($"Ran a tab: {ran}, now on {window.CurrentTabTitle}, palette open {window.PaletteView.IsOpen}");

                window.OpenPalette();
                window.PaletteView.SetQuery("burn_01");
                await Idle();
                var first = window.PaletteView.Items.FirstOrDefault(i => i.Kind != "TAB" && i.Kind != "ACTION");
                Note($"File result: {first?.Kind}:{first?.Title} in {first?.Detail}");
                if (first != null)
                {
                    window.PaletteView.RunSelected();
                    await Idle();
                    Note($"Ran the first result; now on {window.CurrentTabTitle}");
                }
                window.PaletteView.Close();
            });

            // Every tab at the default size (icons in the bar), and a narrow window where the bar drops its words.
            var tabNumber = 0;
            foreach (var module in window.Modules.ToList())
            {
                var title = module.Title;
                await Step($"18{(char)('a' + tabNumber++)}-tab-{title.ToLowerInvariant().Replace(' ', '-')}", async () =>
                {
                    window.Navigate(title, null);
                    await Idle();
                });
            }
            await Step("18z-tabbar-narrow", async () =>
            {
                var before = window.Width;
                window.Width = 1200;
                await Idle();
                window.Navigate("Overlays", null);
                await Idle();
                ShotNow("18z-tabbar-narrow-shot");
                window.Width = before;
                await Idle();
            });

            // The project chip and its menu in three states, then the toasts. The menu is its own window, so it is pictured by itself.
            await Step("16a-project-chip", async () =>
            {
                window.Navigate("Assets", null);
                await Idle();
                window.OpenProjectMenu();
                await Idle();
                ShotElement("16a-project-menu-clean", window.ProjectMenuCardView);
                window.CloseProjectMenu();
            });
            await Step("16b-project-chip-changes", async () =>
            {
                var project = services.Workspace.Project!;
                project.Set("nhl/tour/chip-test.txt", [1, 2, 3], "Changed a test file");
                await Idle();
                window.OpenProjectMenu();
                await Idle();
                ShotElement("16b-project-menu-changes", window.ProjectMenuCardView);
                window.CloseProjectMenu();
            });
            await Step("16c-project-chip-none", async () =>
            {
                var project = services.Workspace.Project;
                services.Workspace.SetProject(null);
                await Idle();
                window.OpenProjectMenu();
                await Idle();
                ShotElement("16c-project-menu-none", window.ProjectMenuCardView);
                window.CloseProjectMenu();
                services.Workspace.SetProject(project);
                await Idle();
            });
            await Step("16d-toasts", async () =>
            {
                window.ToastsEnabled = true;
                window.ToastView.Clear();
                services.Notify("Imported hockey_puck.png into big_logo.gnf, resized to match");
                services.Notify("Shading removed from 11 textures. Restore shading puts the originals back.");
                services.Notify("Project closed");
                await Idle();
                Note($"Toasts showing: {window.ToastView.Count}");
            });
            window.ToastView.Clear();
            window.ToastsEnabled = false;

            // Settings: how it looks in both themes, then the controls really clicked. The user's settings file is put back afterwards.
            await Step("15a-settings-dark", async () =>
            {
                window.Navigate("Settings", null);
                await Idle();
            });
            await Step("15b-settings-light", async () =>
            {
                window.SwitchTheme(Blueline.Core.UiPalette.Light, animate: false, save: false);
                await Idle();
            });
            window.SwitchTheme(Blueline.Core.UiPalette.Dark, animate: false, save: false);
            await Step("15c-settings-interactions", async () =>
            {
                var view = (HomeView)window.Modules.OfType<HomeModule>().First().View;
                var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Blueline", "settings.json");
                var backup = File.Exists(file) ? File.ReadAllBytes(file) : null;
                var settings = services.Settings;
                var (theme, reduce, picture) = (settings.Theme, settings.ReduceMotion, settings.StartBackground);
                var click = new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent);
                try
                {
                    window.Navigate("Settings", null);
                    await Idle();
                    view.ThemeLight.RaiseEvent(click);
                    await Idle();
                    Note($"Light chosen: dark={ThemeManager.IsDark}, saved theme={settings.Theme}, toggles dark={view.ThemeDark.IsChecked} light={view.ThemeLight.IsChecked}");
                    ShotNow("15c-settings-light-chosen");
                    view.ThemeDark.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    await Idle();
                    Note($"Dark chosen: dark={ThemeManager.IsDark}, saved theme={settings.Theme}, toggles dark={view.ThemeDark.IsChecked} light={view.ThemeLight.IsChecked}");
                    view.ReduceMotionBox.IsChecked = true;
                    view.ReduceMotionBox.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    Note($"Reduce motion ticked: saved={settings.ReduceMotion}, Motion.Reduce={Motion.Reduce}, Motion.Enabled={Motion.Enabled}");
                }
                finally
                {
                    window.SwitchTheme(theme == Blueline.Core.UiPalette.LightName ? Blueline.Core.UiPalette.Light : Blueline.Core.UiPalette.Dark, animate: false, save: false);
                    (settings.Theme, settings.ReduceMotion, settings.StartBackground) = (theme, reduce, picture);
                    Motion.Reduce = reduce;
                    if (backup != null)
                        File.WriteAllBytes(file, backup);
                }
            });
            await OverlayStep("09y-overlays-team-intro", "TeamIntro");

            var overlayEdits = services.Workspace.Project!.Changes.Select(c => c.Path).Where(p => p.StartsWith("nhl/ui/", StringComparison.Ordinal) && (p.Contains("overlay") || p.Contains("widgets/scoreclock") || p.Contains("definition."))).ToList();
            services.Workspace.Project!.Revert(overlayEdits, "Tour: revert overlays");
            Note($"Overlay edits reverted: {overlayEdits.Count} files");
            overlays.ShowPage("Layout");

            var build = Module<BuildModule>();
            await Step("10-build", async () =>
            {
                window.Navigate("Build", null);
                await Idle();
                await build.BuildAsync();
                await WaitForJobs();
                await Idle();
            });

            var other = Path.Combine(folder, "tour-project-other");
            await Step("10b-add-mod-review", async () =>
            {
                // A second throwaway mod: never installed, so its altered bytes reach nothing.
                if (Directory.Exists(other))
                    Directory.Delete(other, true);
                var second = ModProject.Create(other, "Second Mod");
                var mine = services.Workspace.Project!;
                var conflicts = mine.Changes.Where(c => c.Path.EndsWith(".db", StringComparison.Ordinal)).Take(1).Append(mine.Changes.First(c => c.Path == jersey.Path));
                var ring = services.Workspace.Vfs!.Files.First(f => f.Path.Contains("ice_centerlogo_27_cm.gnf", StringComparison.Ordinal));
                var items = new List<(string, byte[], string)>();
                foreach (var change in conflicts)
                {
                    var altered = mine.ReadBlob(change);
                    altered[^1] ^= 1;
                    items.Add((change.Path, altered, "Second mod's version"));
                }
                var center = services.Workspace.Vfs.ReadContent(ring);
                center[^1] ^= 1;
                items.Add((ring.Path, center, "Second mod's center ice"));
                items.Add(("nhl/blueline/second_mod_note.txt", [1, 2, 3], "New file"));
                second.SetMany(items, "Second mod");
                await build.CompareMod(other);
                await WaitForJobs();
                await Idle();
                Note($"Compared Second Mod: {build.ReviewRows.Count} rows, {build.ReviewRows.Count(r => r.Take)} ticked");
            });

            await Step("10c-add-mod-added", async () =>
            {
                var before = services.Workspace.Project!.ChangeCount;
                build.UseTheirsOnConflicts();
                await build.MergeChosen();
                await WaitForJobs();
                await Idle();
                Note($"Merged: {before} changes became {services.Workspace.Project.ChangeCount}");
                await build.BuildAsync();
                await WaitForJobs();
                await Idle();
                Directory.Delete(other, true);
            });

            var play = Module<PlayModule>();
            await Step("11-play", async () =>
            {
                window.Navigate("Play", null);
                await Idle();
            });

            await Step("11b-play-all-games", async () =>
            {
                play.EditGameFile(false);
                await Idle();
            });
            play.EditGameFile(true);
            await Idle();

            if (pressPlay)
            {
                await Step("12-play-running", async () =>
                {
                    if (play.EmulatorIsThrowaway)
                        play.EnterSetting("vblank_frequency", "144");
                    if (!play.Launch())
                        throw new InvalidOperationException("Play did not start the game.");
                    await Task.Delay(TimeSpan.FromSeconds(40));
                });
                play.StopGame();
                await WaitFor(() => !play.SessionActive, 15);
                await Idle();
                Note($"Stopped; game still active: {play.SessionActive}");
            }
        }
        catch (Exception e)
        {
            Note($"Tour stopped: {e}");
        }
        finally
        {
            foreach (var entry in services.Log.Entries.Where(e => e.Level != LogLevel.Info))
                Note($"{entry.Level}: {entry.Message}");
            File.WriteAllLines(Path.Combine(folder, "tour.log"), _log);
            Application.Current.Shutdown();
        }
    }

    private T Module<T>() where T : class => window.Modules.OfType<T>().First();

    private void Note(string message) => _log.Add(message);

    /// <summary>Set BLUELINE_TOUR_ONLY to a step name prefix to run just those steps, which keeps a design loop short.</summary>
    private readonly string? _only = Environment.GetEnvironmentVariable("BLUELINE_TOUR_ONLY") is { Length: > 0 } only ? only : null;

    private async Task Step(string name, Func<Task> action)
    {
        if (_only != null && !name.StartsWith(_only, StringComparison.Ordinal))
            return;
        try
        {
            await action();
            await WaitForJobs();
        }
        catch (Exception e)
        {
            Note($"{name}: {e}");
        }
        await Shot(name);
        Note($"{name}: captured");
    }

    private static async Task Search(Controls.AssetBrowser browser, string term)
    {
        var done = new TaskCompletionSource();
        browser.SetSearch(term, () => done.TrySetResult());
        await done.Task;
        await Idle();
    }

    private static void Select(Controls.AssetBrowser browser)
    {
        if (browser.VisibleRows.Count > 0)
            browser.Select(browser.VisibleRows[0]);
    }

    private static async Task Idle()
    {
        await Task.Delay(500);
        await Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private static async Task WaitFor(Func<bool> condition, int seconds = 60)
    {
        var limit = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition() && DateTime.UtcNow < limit)
            await Task.Delay(100);
    }

    private Task WaitForJobs() => WaitFor(() => !window.Services.Jobs.IsBusy, 300);

    /// <summary>Renders the window's content to a PNG. Nothing touches the screen.</summary>
    private async Task Shot(string name)
    {
        await Idle();
        ShotNow(name);
    }

    /// <summary>A picture of one element on its own, for things that live in their own window, such as a menu.</summary>
    private void ShotElement(string name, FrameworkElement element)
    {
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)element.ActualWidth + 40), Math.Max(1, (int)element.ActualHeight + 40), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(_folder, name + ".png"));
        encoder.Save(stream);
    }

    /// <summary>Takes the picture at once, for catching something part way through moving.</summary>
    private void ShotNow(string name)
    {
        var root = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(_folder, name + ".png"));
        encoder.Save(stream);
    }

    private static async Task TeamsSettled(TeamsModule teams)
    {
        await Idle();
        await WaitFor(() => !teams.ThumbnailsBusy && !teams.PreviewControl.IsDecoding);
        await Idle();
    }

    /// <summary>A blue disc on transparency, the shape of a real logo file.</summary>
    private static RgbaImage TestDisc(int size)
    {
        var pixels = new byte[size * size * 4];
        var (center, radius) = (size / 2.0, size * 0.45);
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var distance = Math.Sqrt((x - center) * (x - center) + (y - center) * (y - center));
                if (distance > radius)
                    continue;
                var i = (y * size + x) * 4;
                var ring = distance > radius * 0.8;
                pixels[i] = (byte)(ring ? 232 : 47);
                pixels[i + 1] = (byte)(ring ? 236 : 127);
                pixels[i + 2] = (byte)(ring ? 242 : 255);
                pixels[i + 3] = 255;
            }
        return new RgbaImage(size, size, pixels);
    }

    private static RgbaImage TestPattern(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                var band = (x / 64 + y / 64) % 2 == 0;
                pixels[i] = (byte)(band ? 47 : 16);
                pixels[i + 1] = (byte)(band ? 127 : 24);
                pixels[i + 2] = (byte)(band ? 255 : 48);
                pixels[i + 3] = 255;
            }
        return new RgbaImage(width, height, pixels);
    }
}

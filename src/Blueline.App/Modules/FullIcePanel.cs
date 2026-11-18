using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BCnEncoder.Encoder;
using Blueline.App.Controls;
using Blueline.App.Shell;
using Blueline.Core;
using Blueline.Core.Rinks;
using Blueline.Core.Teams;
using Blueline.Formats.Models;
using Microsoft.Win32;

namespace Blueline.App.Modules;

/// <summary>
/// A team's whole rink for the full-ice model: from TheFaceoff.net (the team's current rink, or any rink link), from any
/// picture, or a plain rink with the team's own logo. Shows the source next to the encoded texture as the game will lay it
/// on the ice before anything is written. Writing puts the texture in the team's centre ice slot and, the first time, the
/// full-ice model into the project, as one undo step.
/// </summary>
public sealed class FullIcePanel : UserControl
{
    private const string PictureFilter = "Pictures (*.png;*.jpg;*.jpeg;*.webp;*.avif;*.bmp;*.tif;*.tiff;*.dds)|*.png;*.jpg;*.jpeg;*.webp;*.avif;*.bmp;*.tif;*.tiff;*.dds|All files|*.*";

    private readonly AppServices _services;
    private readonly TextBlock _title = new() { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 6, 0, 0) };
    private readonly TextBlock _model = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 0, 6) };
    private readonly TextBox _link = new() { MinWidth = 320, ToolTip = "A rink page on TheFaceoff.net, such as https://www.thefaceoff.net/center-ice/..." };
    private readonly CheckBox _template = new()
    {
        Content = "The picture is drawn to the full-ice template (2:1, whole frame): stretch it as it is",
        Margin = new Thickness(0, 6, 0, 0),
        ToolTip = "Off: the rink's boards and goal lines are found and the goal lines put on the game's, where the nets stand (right for TheFaceoff.net layouts). On: the whole picture fills the frame.",
    };
    private readonly Image _preview = new() { Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Top };
    private readonly Button _write = new() { Content = "Write into team", Margin = new Thickness(0, 0, 6, 0) };
    private readonly Button _fetch = new() { Content = "Team's rink from TheFaceoff.net", Margin = new Thickness(0, 0, 6, 4) };
    private readonly Button _fetchLink = new() { Content = "Use this link", Margin = new Thickness(6, 0, 0, 0) };
    private readonly Button _picture = new() { Content = "Choose a picture…", Margin = new Thickness(0, 0, 6, 4) };
    private readonly Button _plain = new() { Content = "Plain rink with team logo", Margin = new Thickness(0, 0, 6, 4) };
    private readonly Button _plainBare = new() { Content = "Plain rink, no logo", Margin = new Thickness(0, 0, 6, 4) };
    private readonly Button _fillRest = new()
    {
        Content = "Use this rink for every team without one",
        Margin = new Thickness(0, 0, 6, 0),
        ToolTip = "Puts the rink in the preview into every team's slot this project has not changed yet. It is encoded once, so it takes seconds. Giving a team its own rink later replaces just that team's.",
    };
    private TeamInfo? _team;
    private IReadOnlyList<TeamInfo> _teams = [];
    private FullIceResult? _result;
    private string? _lastPicture;
    private FaceoffNet? _site;

    public event Action? CloseRequested;

    public FullIcePanel(AppServices services)
    {
        _services = services;
        _write.Style = (Style)FindResource("Primary");
        _write.Click += (_, _) => Write();
        _fetch.Click += (_, _) => _ = FetchTeam();
        _fetchLink.Click += (_, _) => _ = FetchLink();
        _picture.Click += (_, _) => ChoosePicture();
        _plain.Click += (_, _) => _ = MakePlain();
        _plainBare.Click += (_, _) => _ = MakePlain(teamArt: false);
        _fillRest.Click += (_, _) => _ = UseForTheRest();
        _template.Click += (_, _) =>
        {
            if (_lastPicture != null)
                _ = FromPicture(_lastPicture, null);
        };

        var close = new Button { Content = "Back to the art", Style = (Style)FindResource("Quiet"), FontSize = 12, Padding = new Thickness(6, 0, 6, 0) };
        close.Click += (_, _) => CloseRequested?.Invoke();
        var title = new DockPanel { Margin = new Thickness(2, 0, 0, 4) };
        DockPanel.SetDock(close, Dock.Right);
        title.Children.Add(close);
        title.Children.Add(_title);

        var about = Dim("With the full-ice rink model, the centre ice logo is stretched over the whole rink, so each team's centre ice slot holds its whole rink. " +
            "Every arena that uses the NHL rink reads its home team's slot, so teams without a rink of their own need one too: make one (a plain rink without a logo, say) and use it for every team without one.");
        about.Margin = new Thickness(2, 0, 0, 6);

        var modelButton = new Button { Content = "Model file…", Style = (Style)FindResource("Quiet"), FontSize = 12, Padding = new Thickness(6, 0, 6, 0) };
        modelButton.Click += (_, _) => ChooseModel();
        var modelRow = new DockPanel();
        DockPanel.SetDock(modelButton, Dock.Right);
        modelRow.Children.Add(modelButton);
        modelRow.Children.Add(_model);

        var sources = new WrapPanel();
        sources.Children.Add(_fetch);
        sources.Children.Add(_picture);
        sources.Children.Add(_plain);
        sources.Children.Add(_plainBare);
        var linkRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        var linkHint = new TextBlock { Text = "or paste any rink link", Style = (Style)FindResource("Dim"), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        _link.TextChanged += (_, _) => linkHint.Visibility = _link.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var linkBox = new Grid();
        linkBox.Children.Add(_link);
        linkBox.Children.Add(linkHint);
        linkRow.Children.Add(linkBox);
        linkRow.Children.Add(_fetchLink);

        var top = new StackPanel();
        top.Children.Add(title);
        top.Children.Add(about);
        top.Children.Add(modelRow);
        top.Children.Add(sources);
        top.Children.Add(linkRow);
        top.Children.Add(_template);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(_write);
        buttons.Children.Add(_fillRest);
        var credit = Dim("Pictures from TheFaceoff.net are for personal use, with credit to the site. Blueline keeps them on this PC and never packs them into anything it shares.");
        credit.Margin = new Thickness(2, 4, 0, 0);
        var bottom = new StackPanel();
        bottom.Children.Add(buttons);
        bottom.Children.Add(_status);
        bottom.Children.Add(credit);

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(bottom);
        root.Children.Add(new Border
        {
            Style = (Style)FindResource("Card"),
            Margin = new Thickness(0, 8, 0, 0),
            Padding = new Thickness(8),
            Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _preview },
        });
        Content = root;
        ShowModel();
        SetButtons();
    }

    public FullIceResult? Result => _result;

    private TextBlock Dim(string text) => new() { Text = text, Style = (Style)FindResource("Dim"), TextWrapping = TextWrapping.Wrap };

    public void SetTeam(TeamInfo? team, IReadOnlyList<TeamInfo> teams)
    {
        _teams = teams;
        if (team?.Index != _team?.Index)
        {
            _result = null;
            _lastPicture = null;
            _preview.Source = null;
            _status.Text = "";
        }
        _team = team;
        _title.Text = team == null ? "Full ice" : $"Full ice: {team.Name}";
        if (team != null && StockSlot(team) == null)
            _status.Text = $"{team.Name} (art id {team.ArtId}) has no centre ice slot, so it cannot have a full-ice rink.";
        SetButtons();
    }

    private void SetButtons()
    {
        var hasSlot = _team != null && StockSlot(_team) != null;
        _fetch.IsEnabled = hasSlot && FaceoffTeams.LeagueFor(_team!) != null;
        _fetchLink.IsEnabled = _picture.IsEnabled = _plain.IsEnabled = _plainBare.IsEnabled = hasSlot;
        _write.IsEnabled = _fillRest.IsEnabled = _result != null && _result.Team.Index == _team?.Index;
        _write.Content = _team == null ? "Write into team" : $"Write into {_team.Name}";
    }

    private void ShowModel()
    {
        var path = _services.Settings.FullIceModelPath;
        _model.Text = path != null && File.Exists(path) ? $"Full-ice model: {path}" : "Full-ice model: not chosen yet (asked for when writing)";
        _model.Style = (Style)FindResource("Dim");
    }

    private void ChooseModel()
    {
        var dialog = new OpenFileDialog { Title = "Choose the full-ice rink model (rink_ice_nhl.mcd)", Filter = "Rink model (*.mcd)|*.mcd" };
        if (dialog.ShowDialog() != true)
            return;
        try
        {
            if (!FullIce.IsFullIceModel(McdFile.Parse(File.ReadAllBytes(dialog.FileName))))
            {
                _services.ReportError("Not a full-ice model", "This model does not stretch the centre ice logo over the rink. The stock rink_ice_nhl.mcd will not do.");
                return;
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or IndexOutOfRangeException or ArgumentException)
        {
            _services.ReportError("The model could not be read", e);
            return;
        }
        _services.Settings.FullIceModelPath = dialog.FileName;
        _services.Settings.Save();
        ShowModel();
    }

    /// <summary>The slot's stock bytes (its format template), or the mods folder's when the game has none.</summary>
    private byte[]? StockSlot(TeamInfo team)
    {
        var path = FullIce.SlotPath(team.ArtId);
        return _services.Workspace.ReadStock(path) ?? GameRead(path);
    }

    /// <summary>Reads the game as it is with any mods folder, never the open project.</summary>
    private byte[]? GameRead(string path) =>
        _services.Workspace.Vfs is { } vfs && vfs.Find(path) is { } file ? vfs.ReadContent(file) : null;

    private FaceoffNet Site => _site ??= new FaceoffNet(_services.Settings.FaceoffCache ?? AppSettings.DefaultFaceoffCache);

    public Task FetchTeam()
    {
        if (_team is not { } team || StockSlot(team) is not { } stock)
            return Task.CompletedTask;
        var site = Site;
        var ffmpeg = _services.Ffmpeg;
        return Run($"Finding {team.Name}'s rink on TheFaceoff.net", async context =>
        {
            var (file, layout) = await FullIceJob.FetchAsync(site, team, new Progress<string>(context.Status), context.Cancel);
            var source = await FullIceJob.LoadPictureAsync(file, ffmpeg, context.Cancel);
            return FullIceJob.Build(team, source, file, layout.RinkName, FullIceFit.Layout, stock);
        });
    }

    public Task FetchLink()
    {
        var text = _link.Text.Trim();
        if (_team is not { } team || StockSlot(team) is not { } stock)
            return Task.CompletedTask;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || !uri.Host.EndsWith("thefaceoff.net", StringComparison.OrdinalIgnoreCase))
        {
            _services.ReportError("Not a TheFaceoff.net link", "Paste the address of a rink page, such as https://www.thefaceoff.net/center-ice/...");
            return Task.CompletedTask;
        }
        var site = Site;
        var ffmpeg = _services.Ffmpeg;
        return Run("Reading the rink from TheFaceoff.net", async context =>
        {
            var (file, layout) = await FullIceJob.FetchRinkAsync(site, new FaceoffRink(uri.AbsoluteUri, uri.AbsoluteUri, null), new Progress<string>(context.Status), context.Cancel);
            var source = await FullIceJob.LoadPictureAsync(file, ffmpeg, context.Cancel);
            return FullIceJob.Build(team, source, file, layout.RinkName, FullIceFit.Layout, stock);
        });
    }

    private void ChoosePicture()
    {
        var dialog = new OpenFileDialog { Title = "Choose a whole-rink picture (a top-down layout, or one drawn to the full-ice template)", Filter = PictureFilter };
        if (dialog.ShowDialog() == true)
            _ = FromPicture(dialog.FileName, null);
    }

    /// <summary>Any picture file: what Windows reads directly, anything else (WebP, AVIF) through FFmpeg.</summary>
    public Task FromPicture(string file, FullIceFit? fit)
    {
        if (_team is not { } team || StockSlot(team) is not { } stock)
            return Task.CompletedTask;
        _lastPicture = file;
        if (fit is { } wanted)
            _template.IsChecked = wanted == FullIceFit.Template;
        var chosen = _template.IsChecked == true ? FullIceFit.Template : FullIceFit.Layout;
        var ffmpeg = _services.Ffmpeg;
        return Run($"Fitting {Path.GetFileName(file)}", async context =>
        {
            var source = ImageFiles.IsSupported(file) ? ImageFiles.Load(file) : await FullIceJob.LoadPictureAsync(file, ffmpeg, context.Cancel);
            return FullIceJob.Build(team, source, file, null, chosen, stock);
        });
    }

    public Task MakePlain(bool teamArt = true)
    {
        if (_team is not { } team || StockSlot(team) is not { } stock)
            return Task.CompletedTask;
        _lastPicture = null;
        return Run(teamArt ? $"Drawing a plain rink for {team.Name}" : "Drawing a plain rink without a logo",
            context => Task.FromResult(FullIceJob.BuildPlain(team, GameRead, stock, teamArt: teamArt)));
    }

    private Task Run(string title, Func<JobContext, Task<FullIceResult>> work) =>
        _services.Jobs.RunAsync(title, work, result =>
        {
            _result = result;
            _preview.Source = OverlayCanvas.ToBitmap(result.Preview);
            var what = result.IsPlain ? "Plain rink" : result.RinkName ?? Path.GetFileName(result.SourceFile);
            var fit = result.Measure is { } m ? (m.Found ? m.How : "No board outline was found, so the picture's width was taken as the rink. If it is drawn to the template, tick the box above.") : "";
            _status.Text = $"{what}. {fit} Top: the source. Bottom: the texture as the game will lay it on the ice; the magenta line is the game's ice outline. Nothing is written yet.";
            _status.SetResourceReference(TextBlock.ForegroundProperty, result.Measure is { Found: false } ? "WarningBrush" : "TextBrush");
            SetButtons();
        });

    /// <summary>The model's bytes, asking for the file the first time. Null when the user gave up.</summary>
    private byte[]? ModelBytes()
    {
        if (_services.Settings.FullIceModelPath is not { } path || !File.Exists(path))
        {
            ChooseModel();
            path = _services.Settings.FullIceModelPath;
            if (path == null || !File.Exists(path))
                return null;
        }
        return File.ReadAllBytes(path);
    }

    private List<(string Path, byte[] Content, string Description)> WithModel(ModProject project, List<(string Path, byte[] Content, string Description)> items)
    {
        if (!project.TryGet(FullIce.ModelPath, out _))
        {
            var model = ModelBytes();
            if (model == null)
                return [];
            items.Add((FullIce.ModelPath, model, "Full-ice rink model"));
        }
        return items;
    }

    public void Write()
    {
        if (_result is not { } result || _services.RequireProject() is not { } project)
            return;
        var items = WithModel(project, [(result.SlotPath, result.Gnf, $"Full ice for {result.Team.Name}")]);
        if (items.Count == 0)
            return;
        project.SetMany(items, $"Full ice for {result.Team.Name}");
        _services.Notify($"{result.Team.Name}'s full ice is in the project{(items.Count > 1 ? ", with the full-ice model" : "")}. Build the project to try it.");
    }

    /// <summary>
    /// The rink in the preview for every centre ice slot the project has not changed yet (one per art id), encoded once.
    /// </summary>
    public Task UseForTheRest()
    {
        if (_result is not { } result || _services.RequireProject() is not { } project)
            return Task.CompletedTask;
        var todo = _teams.GroupBy(t => t.ArtId).Select(g => g.OrderBy(t => t.LeagueId == 0 ? 0 : 1).First())
            .Select(t => (Team: t, Slot: FullIce.SlotPath(t.ArtId)))
            .Where(t => t.Slot != result.SlotPath && !project.TryGet(t.Slot, out _))
            .Select(t => (t.Slot, Stock: StockSlot(t.Team)))
            .Where(t => t.Stock != null).Select(t => (t.Slot, t.Stock!)).ToList();
        if (todo.Count == 0)
        {
            _services.Notify("Every team with a centre ice slot already has a rink in this project.");
            return Task.CompletedTask;
        }
        var what = result.IsPlain ? "this plain rink" : "this rink";
        if (!ConfirmDialog.Ask("Rink for the other teams", $"Use {what} for the {todo.Count} teams that have no rink in this project yet? It takes a few seconds and is one undo step. Giving a team its own rink later replaces just that team's.", "Use it"))
            return Task.CompletedTask;
        var fitted = result.Fitted;
        return _services.Jobs.RunAsync($"Rink for {todo.Count} teams", context =>
            Task.FromResult(FullIceJob.Spread(fitted, todo).Select(s => (s.Slot, s.Gnf, "Full ice (shared rink)")).ToList()),
            items =>
            {
                var all = WithModel(project, items);
                if (all.Count == 0)
                    return;
                project.SetMany(all, $"Shared full ice for {items.Count} teams");
                _services.Notify($"{items.Count} teams without a rink now use it. Give any of them its own rink and only that one changes.");
            });
    }
}

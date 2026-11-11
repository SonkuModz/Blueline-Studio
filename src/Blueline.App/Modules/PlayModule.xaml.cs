using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Blueline.App.Shell;
using Blueline.Core;
using Blueline.Core.Emulator;
using Blueline.Formats.Sony;
using Microsoft.Win32;

namespace Blueline.App.Modules;

/// <summary>Launches the game in shadPS4 and edits that emulator's settings. The game's own files are only read.</summary>
public partial class PlayModule : UserControl, IEditorModule
{
    private static readonly (string Button, string Label)[] ControlRows =
    [
        ("cross", "⊗"), ("circle", "○"), ("square", "□"), ("triangle", "△"), ("options", "Options"),
    ];

    private readonly AppServices _services;
    private readonly Dictionary<ShadSetting, Func<string?>> _readers = [];
    private readonly Dictionary<ShadSetting, Action<string?>> _writers = [];
    private readonly Dictionary<ShadSetting, string?> _saved = [];
    private readonly Dictionary<ShadSetting, UIElement> _inputs = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private IReadOnlyList<ShadSetting> _form = [];
    private ShadInstall? _emulator;
    private ShadSession? _session;
    private bool _stopRequested;
    private bool _populating;
    private bool _editingGameFile = true;
    private bool _logHidesOpens;
    private string? _lastGameFolder;
    private string? _lastLogPath;
    private string _titleId = "CUSA11126";

    public PlayModule(AppServices services)
    {
        _services = services;
        InitializeComponent();
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    public string Title => "Play";
    public FrameworkElement View => this;
    public bool SessionActive => _session?.Running == true;
    public int SessionModsOpened => _session?.ModsOpened.Count ?? 0;

    public void Activated()
    {
        RefreshGame();
        LoadEmulator();
        RefreshPlayState();
    }

    /// <summary>The file Save writes to: this game's custom config when the build has them, otherwise the shared file.</summary>
    private string? TargetPath => _emulator == null ? null
        : _emulator.EditsGameConfig && _editingGameFile ? _emulator.GameConfigFile(_titleId)
        : _emulator.ConfigPath;

    public void OpenPath(string path) { }

    /// <summary>For the UI tour: true only for an emulator with its own `user` folder, so a tour edit cannot reach real settings.</summary>
    public bool EmulatorIsThrowaway => _emulator != null && _emulator.UserFolder.StartsWith(_emulator.Folder, StringComparison.OrdinalIgnoreCase);

    /// <summary>For the UI tour: types a value into the form, as a person would, without saving it.</summary>
    public void EnterSetting(string key, string value)
    {
        _writers[_form.First(s => s.Key == key)](value);
        FormChanged();
    }

    /// <summary>For the UI tour: picks the file the form edits.</summary>
    public void EditGameFile(bool game) => ScopeBox.SelectedIndex = game ? 0 : 1;

    // ---- Game ----

    private void RefreshGame()
    {
        var install = _services.Workspace.Vfs?.Install;
        if (install == null)
        {
            _lastGameFolder = null;
            Art.Source = null;
            NoArt.Visibility = Visibility.Visible;
            TitleText.Text = "No game loaded";
            FactsText.Text = "Choose the game folder in Settings.";
            FolderText.Text = ModsText.Text = ProjectNote.Text = "";
            return;
        }

        NoArt.Visibility = Visibility.Collapsed;
        if (install.Root != _lastGameFolder)
        {
            _lastGameFolder = install.Root;
            Art.Source = LoadImage(Path.Combine(install.Root, "sce_sys", "pic0.png")) ?? LoadImage(Path.Combine(install.Root, "sce_sys", "icon0.png"));
            FolderText.Text = install.Root;
            ReadGameFacts(install.Root);
        }

        var mods = ModInstaller.InstalledMods(install);
        ModsText.Text = mods.Count == 0 ? "None, so the game runs as it shipped." : string.Join("\n", mods);
        var project = _services.Workspace.Project;
        ProjectNote.Text = project is { ChangeCount: > 0 } && !ModInstaller.IsInstalled(project, install)
            ? $"{project.Name} has {project.ChangeCount} changed file{(project.ChangeCount == 1 ? "" : "s")} that are not installed, so they will not be in this game."
            : "";
    }

    private void ReadGameFacts(string root)
    {
        try
        {
            var sfo = ParamSfo.Parse(File.ReadAllBytes(Path.Combine(root, "sce_sys", "param.sfo")));
            _titleId = sfo["TITLE_ID"] ?? _titleId;
            TitleText.Text = sfo["TITLE"] ?? "NHL 19";
            FactsText.Text = $"{_titleId}  ·  Version {sfo["APP_VER"] ?? "unknown"}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _services.Log.Warning($"Could not read param.sfo: {e.Message}");
            TitleText.Text = "NHL 19";
            FactsText.Text = "The game's details could not be read from param.sfo.";
        }
    }

    private static BitmapImage? LoadImage(string path)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path);
            image.DecodePixelWidth = 800;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception e) when (e is IOException or NotSupportedException or UriFormatException)
        {
            return null;
        }
    }

    // ---- Emulator ----

    private void LoadEmulator()
    {
        var path = _services.Settings.EmulatorPath;
        _emulator = path is { Length: > 0 } && File.Exists(path) ? new ShadInstall(path) : null;
        OpenFolder.IsEnabled = _emulator != null;

        if (_emulator == null)
        {
            EmulatorText.Text = path is { Length: > 0 } ? $"Not found: {path}" : "Not set. Choose your shadPS4.exe to play from here.";
            ConfigText.Text = "";
            ControlsText.Text = "Choose the emulator to see these.";
            ShowSettingsMessage("Settings appear here once the emulator is chosen.");
            return;
        }

        EmulatorText.Text = _emulator.Executable;
        ControlsText.Text = DescribeControls(_emulator);
        DescribeTarget();

        if (!_emulator.HasConfig)
        {
            ShowSettingsMessage("The emulator writes its settings file the first time it starts. Press Play once, then these appear.");
            return;
        }
        if (!ReferenceEquals(_form, _emulator.Settings))
            BuildSettingsForm(_emulator.Settings);
        else if (IsDirty())
            return;
        LoadSettings();
    }

    /// <summary>Says which file the form edits, and offers the choice on builds that have per-game files.</summary>
    private void DescribeTarget()
    {
        var gameName = _emulator!.GameConfigFile(_titleId);
        ScopeCard.Visibility = _emulator.EditsGameConfig ? Visibility.Visible : Visibility.Collapsed;
        if (!_emulator.EditsGameConfig)
        {
            var perGame = _emulator.GameConfigPath(_titleId);
            ConfigText.Text = _emulator.ConfigPath + "  ·  Shared by every game this emulator runs"
                + (perGame != null ? $"  ·  {Path.GetFileName(perGame)} overrides some of these for this game" : "");
            return;
        }

        ConfigText.Text = TargetPath;
        _populating = true;
        var options = new[]
        {
            new ShadOption("game", $"{TitleText.Text} only  ·  {Path.GetFileName(gameName)}"),
            new ShadOption("all", $"All games  ·  {Path.GetFileName(_emulator.ConfigPath)}"),
        };
        ScopeBox.ItemsSource = options;
        ScopeBox.SelectedItem = options[_editingGameFile ? 0 : 1];
        _populating = false;
        ScopeNote.Text = _editingGameFile
            ? (File.Exists(gameName) ? "" : "This file does not exist yet. Saving creates it. ")
                + $"Saves change only this game's file. Anything it leaves out comes from {Path.GetFileName(_emulator.ConfigPath)}, and the form shows that value."
            : $"Saves change the file every game shares. {Path.GetFileName(gameName)} still overrides these for {TitleText.Text}.";
    }

    private void OnScopeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_populating || _emulator == null || ScopeBox.SelectedItem is not ShadOption option)
            return;
        _editingGameFile = option.Value == "game";
        DescribeTarget();
        LoadSettings();
    }

    private static string DescribeControls(ShadInstall emulator)
    {
        var bindings = emulator.KeyboardBindings();
        if (bindings.Count == 0)
            return "No default.ini yet. The emulator writes it on first launch.";
        var lines = ControlRows.Where(r => bindings.ContainsKey(r.Button))
            .Select(r => $"{r.Label}   {string.Join(" or ", bindings[r.Button].Select(KeyName))}");
        return string.Join("\n", lines);
    }

    private static string KeyName(string key) => key.StartsWith("kp", StringComparison.Ordinal) ? "Numpad " + key[2..]
        : key.Length == 1 ? key.ToUpperInvariant()
        : char.ToUpperInvariant(key[0]) + key[1..];

    private void OnChooseEmulator(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Locate shadPS4.exe", Filter = "shadPS4.exe|shadPS4.exe|Programs (*.exe)|*.exe" };
        if (dialog.ShowDialog() != true)
            return;
        _services.Settings.EmulatorPath = dialog.FileName;
        _services.Settings.Save();
        _services.Log.Info($"Emulator set to {dialog.FileName}");
        _saved.Clear();
        Activated();
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        if (_emulator != null && Directory.Exists(_emulator.UserFolder))
            Process.Start(new ProcessStartInfo(_emulator.UserFolder) { UseShellExecute = true });
    }

    private void OnOpenBuild(object sender, RoutedEventArgs e) => _services.Navigate("Build");

    // ---- Settings form ----

    private void BuildSettingsForm(IReadOnlyList<ShadSetting> settings)
    {
        _form = settings;
        _saved.Clear();
        _readers.Clear();
        _writers.Clear();
        _inputs.Clear();
        Groups.Children.Clear();
        foreach (var group in settings.GroupBy(s => s.Group))
        {
            // Fields and on/off switches differ in height, so each gets its own grid and neither leaves gaps.
            var fields = new UniformGrid { Columns = 3 };
            var toggles = new UniformGrid { Columns = 3 };
            foreach (var setting in group)
                (setting.Kind == ShadSettingKind.Toggle ? toggles : fields).Children.Add(BuildField(setting));

            var card = new StackPanel();
            card.Children.Add(new TextBlock { Text = group.Key.ToUpperInvariant(), Style = (Style)FindResource("Section") });
            card.Children.Add(fields);
            card.Children.Add(toggles);
            Groups.Children.Add(new Border { Style = (Style)FindResource("Card"), Margin = new Thickness(0, 0, 0, 8), Child = card });
        }
    }

    private FrameworkElement BuildField(ShadSetting setting)
    {
        var field = new StackPanel { Margin = new Thickness(0, 0, 14, 10) };
        switch (setting.Kind)
        {
            case ShadSettingKind.Toggle:
                var box = new CheckBox { Content = setting.Label, Margin = new Thickness(0, 3, 0, 0) };
                box.Click += (_, _) => FormChanged();
                field.Children.Add(box);
                _inputs[setting] = box;
                _readers[setting] = () => box.IsEnabled ? (box.IsChecked == true ? "true" : "false") : null;
                _writers[setting] = value =>
                {
                    box.IsEnabled = value != null;
                    box.IsChecked = value == "true";
                };
                break;

            case ShadSettingKind.Number:
                var text = new TextBox();
                text.TextChanged += (_, _) => FormChanged();
                field.Children.Add(Label(setting));
                field.Children.Add(text);
                _inputs[setting] = text;
                _readers[setting] = () => text.IsEnabled ? text.Text.Trim() : null;
                _writers[setting] = value =>
                {
                    text.IsEnabled = value != null;
                    text.Text = value ?? "";
                };
                break;

            default:
                var combo = new ComboBox { DisplayMemberPath = nameof(ShadOption.Label) };
                combo.SelectionChanged += (_, _) => FormChanged();
                field.Children.Add(Label(setting));
                field.Children.Add(combo);
                _inputs[setting] = combo;
                _readers[setting] = () => combo.IsEnabled ? (combo.SelectedItem as ShadOption)?.Value : null;
                _writers[setting] = value =>
                {
                    // A value this list does not offer stays selectable, so opening the page never changes it.
                    var options = setting.Options!.ToList();
                    if (value != null && options.All(o => o.Value != value))
                        options.Add(new ShadOption(value, $"{value} (current)"));
                    combo.ItemsSource = options;
                    combo.IsEnabled = value != null;
                    combo.SelectedItem = options.FirstOrDefault(o => o.Value == value);
                };
                break;
        }

        if (setting.Hint.Length > 0)
            field.Children.Add(new TextBlock { Text = setting.Hint, Style = (Style)FindResource("Dim"), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
        return field;
    }

    private TextBlock Label(ShadSetting setting) =>
        new() { Text = setting.Label, Style = (Style)FindResource("Dim"), Margin = new Thickness(0, 0, 0, 3) };

    private void ShowSettingsMessage(string message)
    {
        SettingsMessage.Text = message;
        SettingsMessage.Visibility = Visibility.Visible;
        Groups.Visibility = Visibility.Collapsed;
        ScopeCard.Visibility = Visibility.Collapsed;
        SaveState.Text = "";
        SaveButton.IsEnabled = DiscardButton.IsEnabled = RestoreButton.IsEnabled = false;
    }

    private void LoadSettings()
    {
        try
        {
            var config = _emulator!.ReadSettings(TargetPath);
            var gameFile = TargetPath != _emulator.ConfigPath;
            _populating = true;
            foreach (var setting in _form)
            {
                var value = ShadSettings.Read(config, setting);
                _saved[setting] = value;
                _writers[setting](value);
                // Shown so the value in effect is visible, but a per-game file would not change it.
                if (gameFile && !setting.GameOverride)
                    _inputs[setting].IsEnabled = false;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ShowSettingsMessage($"{Path.GetFileName(TargetPath)} could not be read: {e.Message}");
            // Keep the file choice on screen, so a broken per-game file does not lock the shared one away too.
            if (_emulator?.EditsGameConfig == true)
            {
                ScopeCard.Visibility = Visibility.Visible;
                ScopeBox.IsEnabled = true;
            }
            _saved.Clear();
            return;
        }
        finally
        {
            _populating = false;
        }

        SettingsMessage.Visibility = Visibility.Collapsed;
        Groups.Visibility = Visibility.Visible;
        FormChanged();
    }

    private Dictionary<ShadSetting, string> Changes()
    {
        var changes = new Dictionary<ShadSetting, string>();
        foreach (var setting in _form)
            if (_saved.TryGetValue(setting, out var saved) && saved != null && _readers[setting]() is { } value && value != saved)
                changes[setting] = value;
        return changes;
    }

    private bool IsDirty() => _saved.Count > 0 && Changes().Count > 0;

    private void FormChanged()
    {
        if (_populating)
            return;
        var changes = Changes();
        var problem = changes.Select(c => ShadSettings.Validate(c.Key, c.Value)).FirstOrDefault(p => p != null);
        // The emulator writes this file when it exits, so a save made during a game could be overwritten.
        var locked = SessionActive || ShadInstall.AnyRunning();
        SaveButton.IsEnabled = changes.Count > 0 && problem == null && !locked;
        DiscardButton.IsEnabled = changes.Count > 0;
        // Switching files with edits pending would silently drop them.
        ScopeBox.IsEnabled = changes.Count == 0;
        RestoreButton.IsEnabled = TargetPath is { } target && _emulator!.HasBackupFor(target) && !locked;
        SaveState.Foreground = (Brush)FindResource(problem != null ? "WarningBrush" : "DimBrush");
        SaveState.Text = problem
            ?? (locked ? "Settings can be saved once the game has closed."
            : changes.Count > 0 ? $"{changes.Count} unsaved change{(changes.Count == 1 ? "" : "s")} to {Path.GetFileName(TargetPath)}. Play saves them first."
            : "Settings take effect the next time the game starts.");
    }

    private bool SaveChanges()
    {
        var changes = Changes();
        if (changes.Count == 0)
            return true;
        if (changes.Select(c => ShadSettings.Validate(c.Key, c.Value)).FirstOrDefault(p => p != null) is { } problem)
        {
            _services.ReportError("Settings not saved", problem);
            return false;
        }

        try
        {
            _emulator!.SaveSettings(changes, TargetPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            _services.ReportError("Settings not saved", e.Message);
            return false;
        }
        _services.Notify($"Saved {changes.Count} setting{(changes.Count == 1 ? "" : "s")} to {Path.GetFileName(TargetPath)}: {string.Join(", ", changes.Select(c => $"{c.Key.Key} = {c.Value}"))}");
        DescribeTarget();
        LoadSettings();
        return true;
    }

    private void OnSave(object sender, RoutedEventArgs e) => SaveChanges();

    private void OnDiscard(object sender, RoutedEventArgs e) => LoadSettings();

    private void OnRestore(object sender, RoutedEventArgs e)
    {
        var name = Path.GetFileName(TargetPath);
        if (_emulator == null || !Controls.ConfirmDialog.Ask("Restore original settings?",
                $"This puts back {name} as it was before Blueline first changed it, and undoes any settings changed since, here or elsewhere.", "Restore"))
            return;
        try
        {
            _emulator.RestoreBackup(TargetPath);
            _services.Notify($"Restored {name}.");
            DescribeTarget();
            LoadSettings();
        }
        catch (IOException error)
        {
            _services.ReportError("Settings not restored", error.Message);
        }
    }

    // ---- Playing ----

    private void OnPlay(object sender, RoutedEventArgs e) => Launch();

    /// <summary>Saves pending settings and starts the game. Returns whether it started.</summary>
    public bool Launch()
    {
        var root = _services.Workspace.Vfs?.Install.Root;
        if (_emulator == null || root == null || SessionActive || ShadInstall.AnyRunning())
            return false;
        if (!SaveChanges())
            return false;

        try
        {
            _session?.Dispose();
            _stopRequested = false;
            _endReported = false;
            _logHidesOpens = _emulator.LogHidesFileOpens(_emulator.GameConfigFile(_titleId));
            _session = ShadSession.Start(_emulator, root, Path.Combine(Path.GetDirectoryName(_services.Log.FilePath)!, "game"));
            _lastLogPath = _session.LogPath;
            _services.Log.Info($"Started {root} in {_emulator.Executable}. Log: {_session.LogPath}");
            LogBox.Text = "";
            RefreshPlayState();
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _services.ReportError("The game did not start", error.Message);
            return false;
        }
    }

    private void OnStop(object sender, RoutedEventArgs e) => StopGame();

    public void StopGame()
    {
        if (_session == null)
            return;
        _stopRequested = true;
        try
        {
            _session.Stop();
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or AggregateException)
        {
            _services.ReportError("The game did not stop", error.Message);
        }
    }

    private bool _endReported;
    private bool _wasRunning;
    private bool _wasElsewhere;

    private void Tick()
    {
        if (_session != null)
        {
            try
            {
                if (_session.Poll())
                    ShowLog();
            }
            catch (IOException)
            {
                // The emulator is mid-write; the next tick reads it.
            }

            if (!_session.Running && !_endReported)
            {
                _endReported = true;
                var code = _session.ExitCode;
                _services.Log.Info($"Game ended with code {code} after {Elapsed(_session)}, {_session.ErrorLines} error lines. Mods opened: {_session.ModsOpened.Count}");
            }
        }
        if (IsVisible)
            RefreshPlayState();
    }

    private void ShowLog()
    {
        var lines = _session!.Recent;
        var atEnd = LogBox.VerticalOffset + LogBox.ViewportHeight >= LogBox.ExtentHeight - 4;
        LogBox.Text = string.Join("\n", lines);
        if (atEnd)
            LogBox.ScrollToEnd();
    }

    private static string Elapsed(ShadSession session) => (DateTime.Now - session.Started).ToString(@"m\:ss");

    private void RefreshPlayState()
    {
        var active = SessionActive;
        var elsewhere = !active && ShadInstall.AnyRunning();
        var ready = _emulator != null && _services.Workspace.Vfs != null;
        if (active != _wasRunning || elsewhere != _wasElsewhere)
        {
            _wasRunning = active;
            _wasElsewhere = elsewhere;
            if (Groups.Visibility == Visibility.Visible)
                FormChanged();
        }

        PlayButton.IsEnabled = ready && !active && !elsewhere;
        PlayButton.Content = active ? "Running" : "Play";
        StopButton.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        OpenLog.IsEnabled = _lastLogPath != null;

        var warning = false;
        if (_services.Workspace.Vfs == null)
            StateText.Text = "Choose the game folder in Settings first.";
        else if (_emulator == null)
            StateText.Text = "Choose shadPS4.exe to play from here.";
        else if (active)
            StateText.Text = $"Running for {Elapsed(_session!)}. {OpenedMods()} The first launch compiles shaders, so it can be slow.";
        else if (elsewhere)
        {
            StateText.Text = "shadPS4 is already running outside this page. Close it to play from here.";
            warning = true;
        }
        else if (_session is { } ended)
        {
            var failed = !_stopRequested && ended.ExitCode is not (null or 0);
            warning = failed;
            StateText.Text = _stopRequested ? "You stopped the game." : failed
                ? $"The game closed with code {ended.ExitCode}. The log below shows what happened last."
                : "The game closed.";
        }
        else
            StateText.Text = "Ready. Runs the game folder above in shadPS4.";
        StateText.Foreground = (Brush)FindResource(warning ? "WarningBrush" : "TextBrush");
    }

    private string OpenedMods()
    {
        var opened = _session!.ModsOpened;
        if (_logHidesOpens)
            return "Log detail hides file opens, so opened mods cannot be counted.";
        return opened.Count == 0 ? "No mod archive opened so far." : $"Opened {opened.Count} mod archive{(opened.Count == 1 ? "" : "s")}.";
    }

    private void OnOpenLog(object sender, RoutedEventArgs e)
    {
        if (_lastLogPath != null && File.Exists(_lastLogPath))
            Process.Start(new ProcessStartInfo(_lastLogPath) { UseShellExecute = true });
    }
}

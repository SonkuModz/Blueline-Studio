using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Blueline.App.Shell;
using Blueline.Core;
using Blueline.Formats.Tdb;
using Microsoft.Win32;

namespace Blueline.App.Modules;

public sealed class TextRow(string? key, string? text, string? original)
{
    public string? Key { get; } = key;
    public string? Text { get; } = text;
    public string? Original { get; } = original;
    public bool IsEdited => Text != Original;
    public string Mark => IsEdited ? "●" : "";
    public string OneLine => (Text ?? "").Replace("\r", " ").Replace("\n", " ");
}

/// <summary>The game's localized strings, per language.</summary>
public partial class TextModule : UserControl, IEditorModule
{
    private const string LocFolder = "nhl/data/kettle/loc/";
    private readonly AppServices _services;
    private readonly DispatcherTimer _debounce;
    private LocDictionary? _dictionary;
    private string? _path;
    private string? _loadedBlob;
    private List<TextRow> _rows = [];
    private int _generation;
    private bool _settingEdit;

    public TextModule(AppServices services)
    {
        _services = services;
        InitializeComponent();
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            ApplyFilter();
        };
        services.Workspace.Changed += () => Dispatcher.BeginInvoke(() =>
        {
            if (IsVisible && _path != null && CurrentBlob() != _loadedBlob)
                LoadLanguage();
        });
    }

    public string Title => "Text";
    public FrameworkElement View => this;
    public IReadOnlyList<TextRow> VisibleRows => Rows.ItemsSource as List<TextRow> ?? [];
    public bool IsLoading { get; private set; }

    private string? CurrentBlob() =>
        _path != null && _services.Workspace.Project?.TryGet(_path, out var change) == true ? change.Blob : null;

    public void Activated()
    {
        if (_services.Workspace.Vfs is not { } vfs)
        {
            Count.Text = "Load the game in Settings first.";
            return;
        }
        if (LanguageBox.Items.Count > 0)
            return;
        var languages = vfs.Files.Where(f => f.Path.StartsWith(LocFolder, StringComparison.Ordinal) && f.Extension == ".db")
            .Select(f => f.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
        LanguageBox.ItemsSource = languages;
        LanguageBox.SelectedItem = languages.Contains("eng_us.db") ? "eng_us.db" : languages.FirstOrDefault();
    }

    public void OpenPath(string path)
    {
        if (path.StartsWith(LocFolder, StringComparison.Ordinal))
            LanguageBox.SelectedItem = path[LocFolder.Length..];
    }

    public void SetSearch(string text)
    {
        Search.Text = text;
        _debounce.Stop();
        ApplyFilter();
    }

    public void SelectFirst()
    {
        if (Rows.Items.Count > 0)
            Rows.SelectedIndex = 0;
    }

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e) => LoadLanguage();

    private void LoadLanguage()
    {
        if (LanguageBox.SelectedItem is not string name || _services.Workspace.Vfs is not { } vfs)
            return;
        var path = LocFolder + name;
        var workspace = _services.Workspace;
        var generation = ++_generation;
        IsLoading = true;
        Count.Text = "Loading";
        _path = path;
        _loadedBlob = CurrentBlob();

        Task.Run(() =>
        {
            var stock = new LocDictionary(vfs.ReadContent(vfs.Find(path)!));
            var current = new LocDictionary(workspace.ReadCurrent(path)!);
            var originals = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var row in stock.Rows)
                if (row.Key != null)
                    originals.TryAdd(row.Key, row.Text);
            var rows = current.Rows.Where(r => r.Key != null)
                .Select(r => new TextRow(r.Key, r.Text, originals.TryGetValue(r.Key!, out var o) ? o : null))
                .OrderBy(r => r.Key, StringComparer.Ordinal).ToList();
            return (current, rows);
        }).ContinueWith(task =>
        {
            if (generation != _generation)
                return;
            IsLoading = false;
            if (task.IsFaulted)
            {
                Count.Text = "";
                _services.ReportError($"{name} could not be read", task.Exception!.GetBaseException());
                return;
            }
            (_dictionary, _rows) = task.Result;
            ApplyFilter();
            ShowRow();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = Search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _debounce.Stop();
        _debounce.Start();
    }

    private void OnFilterClicked(object sender, RoutedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        var terms = Search.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var edited = OnlyEdited.IsChecked == true;
        var selectedKey = (Rows.SelectedItem as TextRow)?.Key;
        var list = _rows.Where(r => (!edited || r.IsEdited) && terms.All(t =>
            (r.Key?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false) ||
            (r.Text?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false))).ToList();
        Rows.ItemsSource = list;
        Rows.SelectedItem = list.FirstOrDefault(r => r.Key == selectedKey);
        Count.Text = $"{list.Count:N0} of {_rows.Count:N0} strings, {_rows.Count(r => r.IsEdited)} edited";
    }

    private void OnRowSelected(object sender, SelectionChangedEventArgs e) => ShowRow();

    private void ShowRow()
    {
        var row = Rows.SelectedItem as TextRow;
        _settingEdit = true;
        KeyText.Text = row?.Key ?? "None selected";
        OriginalText.Text = row?.Original ?? (row == null ? "" : "(added by this project)");
        EditBox.Text = row?.Text ?? "";
        EditBox.IsEnabled = row != null;
        _settingEdit = false;
        UseOriginal.IsEnabled = row?.IsEdited == true && row.Original != null;
        Apply.IsEnabled = false;
        ShowProblem(null);
    }

    private void ShowProblem(string? text)
    {
        Problem.Text = text ?? "";
        Problem.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnEditChanged(object sender, TextChangedEventArgs e)
    {
        if (_settingEdit || _dictionary == null || Rows.SelectedItem is not TextRow row)
            return;
        var bad = _dictionary.FindUnencodable(EditBox.Text);
        ShowProblem(bad is { } c ? $"The character '{c}' is not in this language's encoding table, so it cannot be saved." : null);
        Apply.IsEnabled = bad == null && EditBox.Text != row.Text;
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        if (Rows.SelectedItem is TextRow row)
            ApplyEdit(row.Key!, EditBox.Text);
    }

    private void OnUseOriginal(object sender, RoutedEventArgs e)
    {
        if (Rows.SelectedItem is TextRow row)
            ApplyEdit(row.Key!, row.Original);
    }

    /// <summary>Applies text to a key and stores the rebuilt dictionary as one undo step.</summary>
    public void ApplyEdit(string key, string? text)
    {
        if (_dictionary == null || _path == null || _services.RequireProject() is not { } project)
            return;
        try
        {
            _dictionary.SetText(key, text);
            Store(project, $"Text of {key}");
        }
        catch (Exception e) when (e is InvalidOperationException or KeyNotFoundException)
        {
            ShowProblem(e.Message);
        }
    }

    private void Store(ModProject project, string label)
    {
        var saved = _dictionary!.Save();
        // Refuse to keep a dictionary that does not read back.
        var check = new LocDictionary(saved);
        if (check.Rows.Count != _dictionary.Rows.Count)
            throw new InvalidOperationException("The rebuilt dictionary did not read back correctly.");
        var selected = (Rows.SelectedItem as TextRow)?.Key;
        project.Set(_path!, saved, label);
        _loadedBlob = CurrentBlob();
        var originals = _rows.ToDictionary(r => r.Key!, r => r.Original, StringComparer.Ordinal);
        _rows = _dictionary.Rows.Where(r => r.Key != null)
            .Select(r => new TextRow(r.Key, r.Text, originals.GetValueOrDefault(r.Key!)))
            .OrderBy(r => r.Key, StringComparer.Ordinal).ToList();
        ApplyFilter();
        Rows.SelectedItem = (Rows.ItemsSource as List<TextRow>)?.FirstOrDefault(r => r.Key == selected);
        ShowRow();
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        var key = NewKey.Text.Trim();
        if (key.Length == 0 || _dictionary == null || _path == null)
        {
            ShowProblem("A new string needs a key.");
            return;
        }
        if (_services.RequireProject() is not { } project)
            return;
        try
        {
            _dictionary.AddKey(key, NewText.Text);
            Store(project, $"Added {key}");
            NewKey.Text = NewText.Text = "";
            _services.Notify($"Added {key}.");
        }
        catch (InvalidOperationException error)
        {
            ShowProblem(error.Message);
        }
    }

    private void OnExportCsv(object sender, RoutedEventArgs e)
    {
        if (LanguageBox.SelectedItem is not string name)
            return;
        var dialog = new SaveFileDialog { Title = "Export strings", FileName = Path.ChangeExtension(name, ".csv"), Filter = "CSV|*.csv" };
        if (dialog.ShowDialog() != true)
            return;
        var lines = new List<string> { CsvText.Line(["key", "text"]) };
        lines.AddRange(_rows.Select(r => CsvText.Line([r.Key, r.Text])));
        CsvText.Write(dialog.FileName, lines);
        _services.Notify($"Exported {_rows.Count:N0} strings.");
    }

    private void OnImportCsv(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Import strings", Filter = "CSV|*.csv" };
        if (dialog.ShowDialog() == true)
            ImportCsv(dialog.FileName);
    }

    public void ImportCsv(string file)
    {
        if (_dictionary == null || _services.RequireProject() is not { } project)
            return;
        var rows = CsvText.Parse(File.ReadAllText(file));
        if (rows.Count == 0 || rows[0].Count < 2 || !rows[0][0].Equals("key", StringComparison.OrdinalIgnoreCase))
        {
            _services.ReportError("This CSV cannot be imported", "The first row must have the columns key and text.");
            return;
        }

        // Validate all rows first, so a bad row cannot leave half an import.
        var known = _rows.ToDictionary(r => r.Key!, r => r.Text, StringComparer.Ordinal);
        var changes = new List<(string Key, string Text)>();
        var problems = new List<string>();
        foreach (var row in rows.Skip(1).Where(r => r.Count >= 2))
        {
            if (!known.TryGetValue(row[0], out var current))
                problems.Add($"{row[0]}: no such key");
            else if (_dictionary.FindUnencodable(row[1]) is { } c)
                problems.Add($"{row[0]}: '{c}' is not in the encoding table");
            else if (row[1] != (current ?? ""))
                changes.Add((row[0], row[1]));
        }
        if (problems.Count > 0)
        {
            foreach (var problem in problems)
                _services.Log.Warning($"CSV import: {problem}");
            _services.ReportError("Nothing was imported", $"{problems.Count} rows have problems. The first: {problems[0]}. All of them are in the log.");
            return;
        }
        if (changes.Count == 0)
        {
            _services.Notify("The CSV matches the current text; nothing to import.");
            return;
        }

        foreach (var (key, text) in changes)
            _dictionary.SetText(key, text);
        Store(project, $"Imported {changes.Count} strings from {Path.GetFileName(file)}");
        _services.Notify($"Imported {changes.Count} changed strings.");
    }
}

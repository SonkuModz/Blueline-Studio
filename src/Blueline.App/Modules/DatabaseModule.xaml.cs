using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Blueline.App.Controls;
using Blueline.App.Shell;
using Blueline.Core;
using Blueline.Formats.Tdb;
using Microsoft.Win32;

namespace Blueline.App.Modules;

/// <summary>A row's cells for the grid, read on demand from the table so large tables stay cheap.</summary>
public sealed class TdbRowView(TdbTable table, int row)
{
    public int Row { get; } = row;
    public string this[int field] => Format(table.Read(Row, table.Fields[field]));
    private static string Format(object value) => value is long n ? n.ToString(CultureInfo.InvariantCulture) : (string)value;
}

/// <summary>Browse and edit TDB tables. Saving repairs every checksum and re-verifies them.</summary>
public partial class DatabaseModule : UserControl, IEditorModule
{
    private readonly AppServices _services;
    private readonly DispatcherTimer _debounce;
    private TdbFile? _file;
    private string? _path;
    private string? _loadedBlob;
    private bool _dirty;
    private int _generation;

    public DatabaseModule(AppServices services)
    {
        _services = services;
        InitializeComponent();
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            ShowTable();
        };
        services.Workspace.Changed += () => Dispatcher.BeginInvoke(() =>
        {
            if (IsVisible && _path != null && !_dirty && CurrentBlob() != _loadedBlob)
                LoadDatabase(_path);
        });
    }

    public string Title => "Database";
    public FrameworkElement View => this;
    public bool IsLoading { get; private set; }
    public TdbFile? File_ => _file;

    private string? CurrentBlob() =>
        _path != null && _services.Workspace.Project?.TryGet(_path, out var change) == true ? change.Blob : null;

    public void Activated()
    {
        if (_services.Workspace.Vfs is not { } vfs)
        {
            StatusLine.Text = "Load the game in Settings first.";
            return;
        }
        if (DatabaseBox.Items.Count > 0)
            return;
        // Localization dictionaries are single Huffman tables with their own editor on the Text tab.
        var databases = vfs.Files.Where(f => f.Extension == ".db" && !f.Path.Contains("/loc/", StringComparison.Ordinal))
            .Select(f => f.Path).OrderBy(p => p.EndsWith("nhl_game.db", StringComparison.Ordinal) ? 0 : 1).ThenBy(p => p, StringComparer.Ordinal).ToList();
        DatabaseBox.ItemsSource = databases;
        DatabaseBox.SelectedIndex = databases.Count > 0 ? 0 : -1;
    }

    public void OpenPath(string path)
    {
        if (path.Contains("/loc/", StringComparison.Ordinal))
            _services.Navigate("Text", path);
        else
            DatabaseBox.SelectedItem = path;
    }

    public void SelectTable(string name)
    {
        Tables.SelectedItem = _file?.Tables.FirstOrDefault(t => t.Label == name);
    }

    private void OnDatabaseChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DatabaseBox.SelectedItem is string path)
        {
            if (_dirty && !ConfirmDialog.Ask("Unsaved edits", "This database has edits that are not in the project yet. Discard them?", "Discard"))
                return;
            LoadDatabase(path);
        }
    }

    private void LoadDatabase(string path)
    {
        var workspace = _services.Workspace;
        var vfs = workspace.Vfs!;
        var generation = ++_generation;
        IsLoading = true;
        StatusLine.Text = "Loading";
        _path = path;
        _loadedBlob = CurrentBlob();
        _dirty = false;
        Save.IsEnabled = Discard.IsEnabled = false;

        Task.Run(() =>
        {
            // Copy: edits happen in this buffer until saved.
            var file = TdbFile.Parse((byte[])workspace.ReadCurrent(path)!.Clone());
            var folder = path[..path.LastIndexOf('/')];
            var meta = vfs.Find(folder + "/nhl_game-meta.xml") ?? vfs.Find("nhl/data/common/gamemodes/database/t3db/nhl_game-meta.xml");
            if (meta != null)
                file.ApplyMeta(Encoding.UTF8.GetString(vfs.ReadContent(meta)));
            return (file, file.VerifyCrcs());
        }).ContinueWith(task =>
        {
            if (generation != _generation)
                return;
            IsLoading = false;
            if (task.IsFaulted)
            {
                StatusLine.Text = "";
                _services.ReportError($"{path} could not be read", task.Exception!.GetBaseException());
                return;
            }
            var (file, checks) = task.Result;
            _file = file;
            var bad = checks.Count(c => !c.Matches);
            CrcText.Text = bad == 0 ? $"{checks.Count} checksums verified" : $"{bad} of {checks.Count} checksums do not match the file";
            Tables.ItemsSource = file.Tables.OrderBy(t => t.Label, StringComparer.OrdinalIgnoreCase).ToList();
            Tables.SelectedIndex = 0;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void OnTableChanged(object sender, SelectionChangedEventArgs e)
    {
        Filter.Text = "";
        ShowTable();
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        FilterHint.Visibility = Filter.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _debounce.Stop();
        _debounce.Start();
    }

    private void ShowTable()
    {
        if (Tables.SelectedItem is not TdbTable table)
            return;

        if (Grid.Tag != table)
        {
            Grid.Columns.Clear();
            Grid.Tag = table;
            Grid.Columns.Add(new DataGridTextColumn { Header = "#", Binding = new Binding(nameof(TdbRowView.Row)), IsReadOnly = true });
            for (var i = 0; i < table.Fields.Count; i++)
            {
                var field = table.Fields[i];
                Grid.Columns.Add(new DataGridTextColumn
                {
                    Header = field.Label.Replace("_", "__"),
                    Binding = new Binding($"[{i}]") { Mode = BindingMode.OneWay },
                    IsReadOnly = field.IsPoolPointer,
                });
            }
        }

        var terms = Filter.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var rows = Enumerable.Range(0, table.Count).Select(r => new TdbRowView(table, r));
        if (terms.Length > 0)
            rows = rows.Where(r => terms.All(t => Enumerable.Range(0, table.Fields.Count).Any(f => r[f].Contains(t, StringComparison.OrdinalIgnoreCase))));
        var list = rows.ToList();
        Grid.ItemsSource = list;
        StatusLine.Text = $"{table.Label}: {list.Count:N0} of {table.Count:N0} rows, {table.Fields.Count} fields, {(_dirty ? "unsaved edits" : "no unsaved edits")}. Values must fit each field's bit width.";
    }

    private void OnCellEdit(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit || Grid.Tag is not TdbTable table || e.Row.Item is not TdbRowView row || e.EditingElement is not TextBox box)
            return;
        var index = e.Column.DisplayIndex - 1;
        if (index < 0)
            return;
        var field = table.Fields[index];

        if (!TryWrite(table, row.Row, field, box.Text, out var problem))
        {
            _services.ReportError($"{field.Label} was not changed", problem!);
            e.Cancel = true;
            return;
        }
        MarkDirty();
    }

    private static bool TryWrite(TdbTable table, int row, TdbField field, string text, out string? problem)
    {
        problem = null;
        try
        {
            if (field.IsInlineString)
            {
                table.Write(row, field, text);
                return true;
            }
            if (!long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                problem = $"'{text}' is not a whole number.";
                return false;
            }
            table.Write(row, field, value);
            return true;
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or InvalidOperationException)
        {
            problem = e.Message;
            return false;
        }
    }

    private void MarkDirty()
    {
        _dirty = true;
        Save.IsEnabled = Discard.IsEnabled = true;
    }

    private void OnSave(object sender, RoutedEventArgs e) => SaveToProject();

    public void SaveToProject()
    {
        if (_file == null || _path == null || _services.RequireProject() is not { } project)
            return;
        Grid.CommitEdit(DataGridEditingUnit.Row, true);
        _file.RepairCrcs();
        var checks = _file.VerifyCrcs();
        if (checks.Any(c => !c.Matches))
        {
            _services.ReportError("The database was not saved", "Its checksums did not verify after repair: " + string.Join(", ", checks.Where(c => !c.Matches).Take(4).Select(c => c.Name)));
            return;
        }
        project.Set(_path, (byte[])_file.Data.Clone(), $"Edited {Path.GetFileName(_path)}");
        _loadedBlob = CurrentBlob();
        _dirty = false;
        Save.IsEnabled = Discard.IsEnabled = false;
        ShowTable();
        _services.Notify($"Saved {Path.GetFileName(_path)} to the project, {checks.Count} checksums verified.");
    }

    private void OnDiscard(object sender, RoutedEventArgs e)
    {
        if (_path != null)
            LoadDatabase(_path);
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        if (Tables.SelectedItem is not TdbTable table)
            return;
        var dialog = new SaveFileDialog { Title = "Export table", FileName = table.Label + ".csv", Filter = "CSV|*.csv" };
        if (dialog.ShowDialog() != true)
            return;

        var lines = new List<string> { CsvText.Line(table.Fields.Select(f => f.Label).Prepend("row")) };
        for (var r = 0; r < table.Count; r++)
        {
            var view = new TdbRowView(table, r);
            lines.Add(CsvText.Line(Enumerable.Range(0, table.Fields.Count).Select(f => view[f]).Prepend(r.ToString(CultureInfo.InvariantCulture))));
        }
        CsvText.Write(dialog.FileName, lines);
        _services.Notify($"Exported {table.Count:N0} rows.");
    }

    private void OnImport(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Import table", Filter = "CSV|*.csv" };
        if (dialog.ShowDialog() == true)
            ImportCsv(dialog.FileName);
    }

    /// <summary>Checks every cell before changing any; a bad CSV leaves the table as it was.</summary>
    public void ImportCsv(string file)
    {
        if (Tables.SelectedItem is not TdbTable table)
            return;

        var rows = CsvText.Parse(System.IO.File.ReadAllText(file));
        if (rows.Count == 0 || rows[0].Count == 0 || rows[0][0] != "row")
        {
            _services.ReportError("This CSV cannot be imported", "It must start with a 'row' column, as Export CSV writes.");
            return;
        }

        var fields = rows[0].Skip(1).Select(h => table.Fields.FirstOrDefault(f => f.Label == h)).ToList();
        if (fields.Any(f => f == null))
        {
            _services.ReportError("This CSV does not match the table", $"{table.Label} has no field named {rows[0][fields.IndexOf(null) + 1]}.");
            return;
        }

        var snapshot = table.RecordsSnapshot();
        var changed = 0;
        foreach (var row in rows.Skip(1).Where(r => r.Count == rows[0].Count))
        {
            if (!int.TryParse(row[0], out var index) || index < 0 || index >= table.Count)
            {
                table.RestoreRecords(snapshot);
                _services.ReportError("Nothing was imported", $"Row '{row[0]}' does not exist in {table.Label}, which has {table.Count} rows.");
                return;
            }
            for (var i = 0; i < fields.Count; i++)
            {
                var field = fields[i]!;
                if (field.IsPoolPointer)
                    continue;
                var view = new TdbRowView(table, index);
                if (view[table.Fields.ToList().IndexOf(field)] == row[i + 1])
                    continue;
                if (!TryWrite(table, index, field, row[i + 1], out var problem))
                {
                    table.RestoreRecords(snapshot);
                    _services.ReportError("Nothing was imported", $"Row {index}, {field.Label}: {problem}");
                    return;
                }
                changed++;
            }
        }

        if (changed > 0)
            MarkDirty();
        ShowTable();
        _services.Notify($"{changed} cells changed. Save to project to keep them.");
    }
}

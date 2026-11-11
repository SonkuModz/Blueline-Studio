using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Blueline.Core;

namespace Blueline.App.Controls;

/// <summary>
/// The shared file browser: folder tree, search and a virtualized list. Filtering runs off the UI thread and
/// the newest request wins, so typing stays responsive across 150,000 paths.
/// </summary>
public partial class AssetBrowser : UserControl
{
    private const string AllTypes = "All types";
    private readonly DispatcherTimer _debounce;
    private AssetIndex? _index;
    private Func<AssetRow, bool>? _scope;
    private string _folder = "";
    private int _generation;

    public AssetBrowser()
    {
        InitializeComponent();
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            ApplyFilter();
        };
        TypeFilter.Items.Add(AllTypes);
        foreach (var category in Enum.GetValues<AssetCategory>())
            TypeFilter.Items.Add(category.ToString());
        TypeFilter.SelectedIndex = 0;
    }

    /// <summary>The first selected row, or null.</summary>
    public AssetRow? Selected => List.SelectedItem as AssetRow;
    public IReadOnlyList<AssetRow> SelectedRows => List.SelectedItems.Cast<AssetRow>().ToList();
    public IReadOnlyList<AssetRow> VisibleRows => List.ItemsSource as AssetRow[] ?? [];

    public event Action? SelectionChanged;

    /// <summary>Restricts the browser to one kind of asset, and hides the type chooser.</summary>
    public void SetScope(Func<AssetRow, bool> scope)
    {
        _scope = scope;
        TypeFilter.Visibility = Visibility.Collapsed;
        TreeColumn.Width = new GridLength(150);
        NameColumn.Width = 222;
    }

    public void Load(AssetIndex? index)
    {
        if (ReferenceEquals(index, _index))
            return;
        _index = index;
        if (index == null)
        {
            Tree.ItemsSource = null;
            List.ItemsSource = null;
            return;
        }
        Tree.ItemsSource = new FolderNode("", "", index).Children;
        ApplyFilter();
    }

    /// <summary>Selects a path even if the current filter hides it.</summary>
    public void Reveal(string path)
    {
        Search.Text = "";
        _folder = "";
        ApplyFilter(() =>
        {
            var row = _index?.Find(path);
            if (row == null)
                return;
            List.SelectedItem = row;
            List.ScrollIntoView(row);
        });
    }

    public void Select(AssetRow row)
    {
        List.SelectedItem = row;
        List.ScrollIntoView(row);
    }

    public void SetSearch(string text, Action? then = null)
    {
        Search.Text = text;
        _debounce.Stop();
        ApplyFilter(then);
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = Search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _debounce.Stop();
        _debounce.Start();
    }

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e) => ApplyFilter();

    private void OnFolderSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        _folder = (e.NewValue as FolderNode)?.Path ?? "";
        ApplyFilter();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => SelectionChanged?.Invoke();

    private void ApplyFilter(Action? then = null)
    {
        if (_index == null)
            return;

        var generation = ++_generation;
        var rows = _index.Rows;
        var scope = _scope;
        var folder = _folder;
        var terms = Search.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        AssetCategory? category = TypeFilter.SelectedItem is string s && s != AllTypes ? Enum.Parse<AssetCategory>(s) : null;

        Task.Run(() =>
        {
            var query = rows.AsParallel().AsOrdered().Where(r =>
                (scope == null || scope(r))
                && (category == null || r.Category == category)
                && (folder.Length == 0 || r.Folder == folder || r.Folder.StartsWith(folder + "/", StringComparison.Ordinal))
                && terms.All(t => r.Path.Contains(t, StringComparison.OrdinalIgnoreCase)));
            return query.ToArray();
        }).ContinueWith(task =>
        {
            if (generation != _generation)
                return;
            List.ItemsSource = task.Result;
            Count.Text = task.Result.Length == 1 ? "1 file" : $"{task.Result.Length:N0} files";
            then?.Invoke();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }
}

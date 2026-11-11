using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Blueline.App.Shell;

/// <summary>One line in the palette: what kind of thing it is, what it is called, and what happens when it is chosen.</summary>
public sealed record PaletteItem(string Kind, string Title, string? Detail, Action Run);

/// <summary>
/// The Ctrl+K box. You type, it lists matching tabs, actions and game files, and Enter does the first or chosen one.
/// What to list comes from <see cref="Source"/>, so the window decides what exists.
/// </summary>
public partial class CommandPalette : UserControl
{
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(70) };

    public CommandPalette()
    {
        InitializeComponent();
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Refresh();
        };
    }

    /// <summary>Gives the items to show for what has been typed.</summary>
    public Func<string, IReadOnlyList<PaletteItem>>? Source { get; set; }

    public bool IsOpen => Visibility == Visibility.Visible;

    public int ResultCount => Results.Items.Count;

    public string? SelectedTitle => (Results.SelectedItem as PaletteItem)?.Title;

    public IReadOnlyList<PaletteItem> Items => Results.Items.OfType<PaletteItem>().ToList();

    public void Open()
    {
        if (IsOpen)
            return;
        Query.Text = "";
        Visibility = Visibility.Visible;
        Refresh();
        Hint.Visibility = Visibility.Visible;

        var slide = (TranslateTransform)Card.RenderTransform;
        if (Motion.Enabled)
        {
            Backdrop.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 0.5, Motion.Of(Motion.Quick)) { EasingFunction = Motion.Out });
            Card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Motion.Of(Motion.Page)) { EasingFunction = Motion.Out });
            slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-12, 0, Motion.Of(Motion.Page)) { EasingFunction = Motion.Out });
        }
        else
        {
            Backdrop.BeginAnimation(OpacityProperty, null);
            Card.BeginAnimation(OpacityProperty, null);
            slide.BeginAnimation(TranslateTransform.YProperty, null);
            Backdrop.Opacity = 0.5;
            Card.Opacity = 1;
            slide.Y = 0;
        }
        Dispatcher.BeginInvoke(() => Query.Focus(), DispatcherPriority.Input);
    }

    public void Close()
    {
        if (!IsOpen)
            return;
        _debounce.Stop();
        if (!Motion.Enabled)
        {
            Visibility = Visibility.Collapsed;
            return;
        }

        var fade = new DoubleAnimation(Card.Opacity, 0, Motion.Of(Motion.Quick)) { EasingFunction = Motion.In };
        fade.Completed += (_, _) =>
        {
            Visibility = Visibility.Collapsed;
            Card.BeginAnimation(OpacityProperty, null);
            Backdrop.BeginAnimation(OpacityProperty, null);
        };
        Card.BeginAnimation(OpacityProperty, fade);
        Backdrop.BeginAnimation(OpacityProperty, new DoubleAnimation(Backdrop.Opacity, 0, Motion.Of(Motion.Quick)));
    }

    /// <summary>Types into the box, as a test or a shortcut would.</summary>
    public void SetQuery(string text)
    {
        Query.Text = text;
        _debounce.Stop();
        Refresh();
    }

    /// <summary>Does the chosen item, or the first one when none is chosen. Returns whether anything ran.</summary>
    public bool RunSelected()
    {
        var item = Results.SelectedItem as PaletteItem ?? Results.Items.OfType<PaletteItem>().FirstOrDefault();
        if (item == null)
            return false;
        Close();
        item.Run();
        return true;
    }

    private void Refresh()
    {
        var items = Source?.Invoke(Query.Text.Trim()) ?? [];
        Results.ItemsSource = items;
        Hint.Visibility = Query.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Empty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Results.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (items.Count > 0)
        {
            Results.SelectedIndex = 0;
            Results.ScrollIntoView(items[0]);
        }
    }

    private void OnQueryChanged(object sender, TextChangedEventArgs e)
    {
        Hint.Visibility = Query.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _debounce.Stop();
        _debounce.Start();
    }

    private void OnQueryKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                Move(1);
                e.Handled = true;
                break;
            case Key.Up:
                Move(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                // Typing and Enter close together: list what was typed first.
                if (_debounce.IsEnabled)
                {
                    _debounce.Stop();
                    Refresh();
                }
                RunSelected();
                e.Handled = true;
                break;
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
        }
    }

    private void Move(int by)
    {
        if (Results.Items.Count == 0)
            return;
        var next = Math.Clamp(Results.SelectedIndex + by, 0, Results.Items.Count - 1);
        Results.SelectedIndex = next;
        Results.ScrollIntoView(Results.SelectedItem);
    }

    private void OnResultClick(object sender, MouseButtonEventArgs e)
    {
        for (var at = e.OriginalSource as DependencyObject; at != null; at = VisualTreeHelper.GetParent(at))
            if (at is ListBoxItem item)
            {
                Results.SelectedItem = item.DataContext;
                RunSelected();
                return;
            }
    }

    private void OnBackdrop(object sender, MouseButtonEventArgs e) => Close();
}

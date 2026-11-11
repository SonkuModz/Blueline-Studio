using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace Blueline.App.Shell;

/// <summary>
/// Short messages in the bottom right corner. Each one slides in, stays long enough to read, and fades away. Click one to
/// dismiss it. At most a few show at once, so the oldest goes first. They stand in for the one line of text in the status bar.
/// </summary>
public sealed class ToastHost : StackPanel
{
    private const int MostShown = 4;

    public ToastHost()
    {
        Orientation = Orientation.Vertical;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Bottom;
        Margin = new Thickness(0, 0, 18, 44);
    }

    public int Count => Children.Count;

    /// <summary>Removes every toast at once.</summary>
    public void Clear() => Children.Clear();

    public void Show(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 420, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        var bar = new Border { Width = 3, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 12, 0) };
        bar.SetResourceReference(Border.BackgroundProperty, "AccentTextBrush");
        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(bar, Dock.Left);
        row.Children.Add(bar);
        row.Children.Add(text);

        var slide = new TranslateTransform(0, 12);
        var card = new Border
        {
            Child = row,
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(14, 11, 18, 11),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 8, 0, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
            RenderTransform = slide,
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Opacity = 0.35, Color = Colors.Black },
        };
        card.SetResourceReference(Border.BackgroundProperty, "RaisedBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        card.MouseLeftButtonUp += (_, _) => Dismiss(card);

        Children.Add(card);
        while (Children.Count > MostShown)
            Remove(Children[0]);

        if (Motion.Enabled)
        {
            card.Opacity = 0;
            card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Motion.Of(Motion.Page)) { EasingFunction = Motion.Out });
            slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, 0, Motion.Of(Motion.Page)) { EasingFunction = Motion.Out });
        }
        else
        {
            slide.Y = 0;
        }

        // Longer messages stay longer.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Min(8000, 3000 + 35 * message.Length)) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Dismiss(card);
        };
        timer.Start();
    }

    private void Dismiss(UIElement card)
    {
        if (!Children.Contains(card))
            return;
        if (!Motion.Enabled)
        {
            Remove(card);
            return;
        }

        var fade = new DoubleAnimation(card.Opacity, 0, Motion.Of(Motion.Quick)) { EasingFunction = Motion.In };
        fade.Completed += (_, _) => Remove(card);
        card.BeginAnimation(OpacityProperty, fade);
    }

    private void Remove(UIElement card) => Children.Remove(card);
}

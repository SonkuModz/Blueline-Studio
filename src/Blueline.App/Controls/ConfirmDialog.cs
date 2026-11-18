using System.Windows;
using System.Windows.Controls;

namespace Blueline.App.Controls;

/// <summary>A themed yes/no with the action named on the button.</summary>
public static class ConfirmDialog
{
    public static bool Ask(string title, string message, string action) => Choose(title, message, action) == 0;

    /// <summary>Asks with several actions, the first one primary. Returns the chosen index, or -1 for Cancel.</summary>
    public static int Choose(string title, string message, params string[] actions)
    {
        var chosen = -1;
        var window = new Window
        {
            Title = title,
            Width = 480,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current.MainWindow,
            ShowInTaskbar = false,
            Background = (System.Windows.Media.Brush)Application.Current.FindResource("PanelBrush"),
        };

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.FindResource("Heading"), Margin = new Thickness(0, 0, 0, 8) });
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 20) });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        buttons.Children.Add(cancel);
        for (var i = actions.Length - 1; i >= 0; i--)
        {
            var index = i;
            var button = new Button { Content = actions[i], Margin = new Thickness(0, 0, i == 0 ? 0 : 8, 0) };
            if (i == 0)
            {
                button.Style = (Style)Application.Current.FindResource("Primary");
                button.IsDefault = true;
            }
            button.Click += (_, _) => { chosen = index; window.DialogResult = true; };
            buttons.Children.Add(button);
        }
        panel.Children.Add(buttons);

        window.Content = panel;
        return window.ShowDialog() == true ? chosen : -1;
    }
}

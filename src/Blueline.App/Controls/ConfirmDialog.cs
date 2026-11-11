using System.Windows;
using System.Windows.Controls;

namespace Blueline.App.Controls;

/// <summary>A themed yes/no with the action named on the button.</summary>
public static class ConfirmDialog
{
    public static bool Ask(string title, string message, string action)
    {
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
        var confirm = new Button { Content = action, Style = (Style)Application.Current.FindResource("Primary"), IsDefault = true };
        confirm.Click += (_, _) => window.DialogResult = true;
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);
        panel.Children.Add(buttons);

        window.Content = panel;
        return window.ShowDialog() == true;
    }
}

using System.Windows;
using System.Windows.Media;

namespace Blueline.App.Controls;

/// <summary>Adds file drag-and-drop to any panel, with an accent outline while files hover over it.</summary>
public static class DropTarget
{
    public static void Attach(FrameworkElement element, Func<string, bool> accepts, Action<string[]> dropped, System.Windows.Controls.Border? highlight = null)
    {
        element.AllowDrop = true;
        Thickness? thickness = null;
        Brush? brush = null;

        void Show(bool on)
        {
            if (highlight == null)
                return;
            if (on)
            {
                thickness ??= highlight.BorderThickness;
                brush ??= highlight.BorderBrush;
                highlight.BorderThickness = new Thickness(2);
                highlight.BorderBrush = (Brush)Application.Current.FindResource("AccentBrush");
            }
            else if (thickness != null)
            {
                highlight.BorderThickness = thickness.Value;
                highlight.BorderBrush = brush;
            }
        }

        string[] Files(DragEventArgs e) =>
            e.Data.GetDataPresent(DataFormats.FileDrop) ? ((string[])e.Data.GetData(DataFormats.FileDrop)).Where(accepts).ToArray() : [];

        element.PreviewDragOver += (_, e) =>
        {
            e.Effects = Files(e).Length > 0 ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        element.PreviewDragEnter += (_, e) => Show(Files(e).Length > 0);
        element.PreviewDragLeave += (_, _) => Show(false);
        element.PreviewDrop += (_, e) =>
        {
            Show(false);
            var files = Files(e);
            if (files.Length > 0)
                dropped(files);
            e.Handled = true;
        };
    }
}

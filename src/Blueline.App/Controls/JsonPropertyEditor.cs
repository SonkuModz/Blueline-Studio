using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Blueline.Core.Overlays;

namespace Blueline.App.Controls;

/// <summary>Edits every property of one JSON object, keeping each value's type. Nested values are edited as JSON text.</summary>
public sealed class JsonPropertyEditor : StackPanel
{
    private const double KeyWidth = 104;
    private JsonObject? _target;
    private string[] _hidden = [];

    /// <summary>Raised after a value was changed, with a short description of it.</summary>
    public event Action<string>? Edited;

    public void Show(JsonObject? target, params string[] hiddenKeys)
    {
        _target = target;
        _hidden = hiddenKeys;
        Rebuild();
    }

    private static Brush Resource(string key) => (Brush)Application.Current.FindResource(key);

    private void Rebuild()
    {
        Children.Clear();
        if (_target == null)
            return;

        foreach (var (key, value) in _target.Where(p => !_hidden.Contains(p.Key)).ToList())
            Children.Add(Row(key, value));
        Children.Add(AddRow());
    }

    private UIElement Row(string key, JsonNode? value)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(KeyWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(new TextBlock { Text = key, Foreground = Resource("DimBrush"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = key });

        var editor = value?.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? BoolEditor(key, value) : TextEditor(key, value);
        Grid.SetColumn(editor, 1);
        grid.Children.Add(editor);

        var remove = new Button { Content = "x", Width = 22, Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(0), ToolTip = $"Remove {key}", Tag = key };
        remove.Click += (_, _) =>
        {
            _target!.Remove(key);
            Rebuild();
            Edited?.Invoke($"Removed {key}");
        };
        Grid.SetColumn(remove, 2);
        grid.Children.Add(remove);
        return grid;
    }

    private FrameworkElement BoolEditor(string key, JsonNode value)
    {
        var box = new CheckBox { IsChecked = value.GetValue<bool>(), VerticalAlignment = VerticalAlignment.Center, Tag = key };
        box.Click += (_, _) =>
        {
            _target![key] = JsonValue.Create(box.IsChecked == true);
            Edited?.Invoke($"Set {key}");
        };
        return box;
    }

    private static string Display(JsonNode? value) => JsonEdit.String(value) ?? JsonEdit.ToText(value);

    private FrameworkElement TextEditor(string key, JsonNode? value)
    {
        var nested = value is JsonObject or JsonArray;
        var box = new TextBox { Text = Display(value), Tag = key, TextWrapping = nested ? TextWrapping.Wrap : TextWrapping.NoWrap, MaxHeight = 120, VerticalScrollBarVisibility = nested ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled };
        var normal = box.BorderBrush;

        void Commit()
        {
            if (_target == null || box.Text == Display(_target[key]))
                return;
            try
            {
                _target[key] = JsonEdit.Value(box.Text, _target[key]);
                box.BorderBrush = normal;
                box.ToolTip = null;
                Edited?.Invoke($"Set {key}");
            }
            catch (Exception e) when (e is JsonException or InvalidDataException)
            {
                box.BorderBrush = Resource("DangerBrush");
                box.ToolTip = e.Message;
            }
        }

        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
                Commit();
        };
        return box;
    }

    private UIElement AddRow()
    {
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(KeyWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = new TextBox { ToolTip = "New property name", Margin = new Thickness(0, 0, 4, 0) };
        var value = new TextBox { ToolTip = "Value: a number, true or false, text, or JSON" };
        var add = new Button { Content = "Add", Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(8, 0, 8, 0) };
        add.Click += (_, _) =>
        {
            var key = name.Text.Trim();
            if (_target == null || key.Length == 0 || _target.ContainsKey(key))
                return;
            try
            {
                _target[key] = JsonEdit.Value(value.Text, null);
                Rebuild();
                Edited?.Invoke($"Added {key}");
            }
            catch (Exception e) when (e is JsonException or InvalidDataException)
            {
                value.ToolTip = e.Message;
                value.BorderBrush = Resource("DangerBrush");
            }
        };

        grid.Children.Add(name);
        Grid.SetColumn(value, 1);
        grid.Children.Add(value);
        Grid.SetColumn(add, 2);
        grid.Children.Add(add);
        return grid;
    }

    /// <summary>The input box for a property, for automated walks.</summary>
    public TextBox? BoxFor(string key) => Children.OfType<Grid>().SelectMany(g => g.Children.OfType<TextBox>()).FirstOrDefault(b => Equals(b.Tag, key));
}

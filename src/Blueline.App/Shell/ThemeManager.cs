using System.Windows;
using System.Windows.Media;
using Blueline.Core;

namespace Blueline.App.Shell;

/// <summary>
/// Switches the app between its dark and light palettes. WPF freezes every brush that sits in a resource dictionary, so a
/// colour cannot be changed in place. Instead each named brush is replaced by a new one. Every control looks its colours up
/// by name (DynamicResource), so they all follow at once. The cross-fade is done by the window, which fades a picture of the
/// old look away (see MainWindow.SwitchTheme).
/// </summary>
public static class ThemeManager
{
    /// <summary>Brushes the built-in controls reach through system colours, and which palette colour each follows.</summary>
    private static readonly (object Key, string Colour)[] SystemBrushes =
    [
        (SystemColors.ControlBrushKey, "Panel"),
        (SystemColors.WindowBrushKey, "Panel"),
        (SystemColors.ControlLightBrushKey, "Panel"),
        (SystemColors.ControlLightLightBrushKey, "Panel"),
        (SystemColors.ControlDarkBrushKey, "Panel"),
        (SystemColors.ControlDarkDarkBrushKey, "Panel"),
        (SystemColors.ControlTextBrushKey, "Text"),
        (SystemColors.WindowTextBrushKey, "Text"),
    ];

    public static UiPalette Current { get; private set; } = UiPalette.Dark;

    public static bool IsDark => Current.Name == UiPalette.DarkName;

    /// <summary>Raised after the palette changed, for things that cannot follow a brush, such as the title bar.</summary>
    public static event Action? Changed;

    public static void Apply(UiPalette palette)
    {
        var resources = Application.Current.Resources;
        foreach (var (name, hex) in palette.Colours)
            resources[name + "Brush"] = Brush(hex);
        foreach (var (key, colour) in SystemBrushes)
            resources[key] = Brush(palette.Colours[colour]);

        Current = palette;
        Changed?.Invoke();
    }

    private static SolidColorBrush Brush(string hex)
    {
        var (r, g, b, a) = UiPalette.Parse(hex);
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}

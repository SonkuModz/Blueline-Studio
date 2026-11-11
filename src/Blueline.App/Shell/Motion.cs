using System.Windows;
using System.Windows.Media.Animation;

namespace Blueline.App.Shell;

/// <summary>
/// How much the interface moves. One place decides, so a setting, Windows' own "animation effects" switch and the screenshot tour
/// (which needs every page to be there at once) all turn movement off together.
/// </summary>
public static class Motion
{
    /// <summary>Set by the screenshot tour. Everything happens at once while this is true.</summary>
    public static bool ForceInstant { get; set; }

    /// <summary>The user's "reduce motion" setting.</summary>
    public static bool Reduce { get; set; }

    public static bool Enabled => !ForceInstant && !Reduce && SystemParameters.ClientAreaAnimation;

    /// <summary>Durations in milliseconds. Smooth and quick: nothing makes you wait.</summary>
    public const int Quick = 140;
    public const int Page = 220;
    public const int Slow = 360;

    public static Duration Of(int milliseconds) => new(Enabled ? TimeSpan.FromMilliseconds(milliseconds) : TimeSpan.Zero);

    public static IEasingFunction Out { get; } = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });
    public static IEasingFunction In { get; } = Frozen(new CubicEase { EasingMode = EasingMode.EaseIn });
    public static IEasingFunction InOut { get; } = Frozen(new CubicEase { EasingMode = EasingMode.EaseInOut });

    private static T Frozen<T>(T easing) where T : Freezable
    {
        easing.Freeze();
        return easing;
    }
}

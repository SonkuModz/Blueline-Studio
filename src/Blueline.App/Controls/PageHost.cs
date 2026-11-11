using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Blueline.App.Shell;

namespace Blueline.App.Controls;

/// <summary>
/// Shows one page at a time and moves between pages: the old page fades out the way you are travelling and the new one
/// eases in from the other side. Each page is one long-lived view, so nothing is rebuilt. While it moves a page is drawn
/// from a cached picture, which keeps heavy pages (overlay canvas, 3D viewer) smooth.
/// </summary>
public sealed class PageHost : Grid
{
    private const double SlideIn = 28;
    private const double SlideOut = 16;

    private FrameworkElement? _current;
    private readonly List<FrameworkElement> _leaving = [];
    private readonly List<AnimationClock> _clocks = [];

    public FrameworkElement? Current => _current;

    /// <summary>True while a page is still moving, so a test or a screenshot can wait for it.</summary>
    public bool IsMoving => _leaving.Count > 0 || _clocks.Any(c => c.CurrentState == ClockState.Active);

    /// <summary>
    /// Shows <paramref name="view"/>. <paramref name="direction"/> is 1 when the new page is further along than the old one,
    /// -1 when it is earlier, and 0 for no sideways movement.
    /// </summary>
    public void Show(FrameworkElement view, int direction)
    {
        if (ReferenceEquals(view, _current))
            return;

        var old = _current;
        _current = view;

        // A page still on its way out can be called back: stop that exit and take it from where it is.
        if (_leaving.Remove(view))
            Rest(view);

        // Anything else still leaving is finished now, so two quick clicks never stack pages.
        foreach (var other in _leaving.ToList())
        {
            _leaving.Remove(other);
            Rest(other);
            Children.Remove(other);
        }

        if (!Motion.Enabled || old == null)
        {
            if (old != null)
                Children.Remove(old);
            Add(view);
            return;
        }

        Add(view);
        Enter(view, direction);
        Leave(old, direction);
    }

    /// <summary>Stops every running movement where it is, so a screenshot can be taken at an exact moment.</summary>
    public void PauseMovement()
    {
        foreach (var clock in _clocks)
            clock.Controller?.Pause();
    }

    /// <summary>Jumps every paused movement to this many milliseconds from its start.</summary>
    public void SeekMovement(double milliseconds)
    {
        foreach (var clock in _clocks)
            clock.Controller?.Seek(TimeSpan.FromMilliseconds(milliseconds), TimeSeekOrigin.BeginTime);
    }

    public void ResumeMovement()
    {
        foreach (var clock in _clocks.ToList())
            clock.Controller?.Resume();
    }

    private void Add(FrameworkElement view)
    {
        if (view.Parent == this)
            Children.Remove(view);
        Children.Add(view);
    }

    private AnimationClock Clock(AnimationTimeline animation)
    {
        var clock = animation.CreateClock();
        _clocks.Add(clock);
        return clock;
    }

    private void Enter(FrameworkElement view, int direction)
    {
        Prepare(view);
        var slide = (TranslateTransform)view.RenderTransform;
        var fade = Clock(new DoubleAnimation(0, 1, Motion.Of(Motion.Page)) { EasingFunction = Motion.Out });
        fade.Completed += (_, _) =>
        {
            // Only the page that is still the current one is finished here; a page called back later restarts its own animation.
            if (ReferenceEquals(view, _current))
                Rest(view);
        };
        view.ApplyAnimationClock(OpacityProperty, fade);
        if (direction != 0)
            slide.ApplyAnimationClock(TranslateTransform.XProperty, Clock(new DoubleAnimation(direction * SlideIn, 0, Motion.Of(Motion.Page)) { EasingFunction = Motion.Out }));
    }

    private void Leave(FrameworkElement view, int direction)
    {
        _leaving.Add(view);
        Prepare(view);
        view.IsHitTestVisible = false;
        var slide = (TranslateTransform)view.RenderTransform;
        var fade = Clock(new DoubleAnimation(1, 0, Motion.Of(Motion.Quick)) { EasingFunction = Motion.In });
        fade.Completed += (_, _) =>
        {
            if (!_leaving.Remove(view))
                return;
            Rest(view);
            Children.Remove(view);
        };
        view.ApplyAnimationClock(OpacityProperty, fade);
        if (direction != 0)
            slide.ApplyAnimationClock(TranslateTransform.XProperty, Clock(new DoubleAnimation(0, -direction * SlideOut, Motion.Of(Motion.Quick)) { EasingFunction = Motion.In }));
    }

    /// <summary>Draws a page from a picture while it moves, and gives it a transform to slide with.</summary>
    private static void Prepare(FrameworkElement view)
    {
        view.RenderTransform = new TranslateTransform();
        view.CacheMode = new BitmapCache();
    }

    /// <summary>Puts a page back to normal: no animation, no cached picture, clickable.</summary>
    private void Rest(FrameworkElement view)
    {
        view.ApplyAnimationClock(OpacityProperty, null);
        if (view.RenderTransform is TranslateTransform slide)
            slide.ApplyAnimationClock(TranslateTransform.XProperty, null);
        view.ClearValue(RenderTransformProperty);
        view.ClearValue(CacheModeProperty);
        view.ClearValue(OpacityProperty);
        view.IsHitTestVisible = true;

        // Clocks that have finished are of no further use.
        _clocks.RemoveAll(c => c.CurrentState != ClockState.Active && c.CurrentProgress is null or 1);
    }
}

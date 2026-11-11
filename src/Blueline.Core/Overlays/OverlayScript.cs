using System.Text.Json.Nodes;

namespace Blueline.Core.Overlays;

public enum OverlayEase { Linear, QuadIn, QuadOut, QuadInOut }

/// <summary>Property values a running animation has given to widgets, on top of what their file says.</summary>
public sealed class OverlayPose
{
    private readonly Dictionary<LayoutWidget, Dictionary<string, JsonNode?>> _values = [];

    public JsonNode? Get(LayoutWidget widget, string key) => _values.TryGetValue(widget, out var keys) ? keys.GetValueOrDefault(key) : null;

    public bool Has(LayoutWidget widget, string key) => _values.TryGetValue(widget, out var keys) && keys.ContainsKey(key);

    public void Set(LayoutWidget widget, string key, JsonNode? value)
    {
        if (!_values.TryGetValue(widget, out var keys))
            _values[widget] = keys = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        keys[key] = value;
    }

    public OverlayPose Copy()
    {
        var copy = new OverlayPose();
        foreach (var (widget, keys) in _values)
            copy._values[widget] = new Dictionary<string, JsonNode?>(keys, StringComparer.Ordinal);
        return copy;
    }
}

/// <summary>
/// Builds an animation the way the game's zinc AnimScript does. Animations start at a cursor and run side by side.
/// Delay moves the cursor, and Wait moves it to the end of everything added so far. Times are milliseconds, and the
/// game's frames are 1/30 of a second.
/// </summary>
public sealed class OverlayScript(double timeScale = 1)
{
    private enum Kind { Set, To, FromRelative, Relative }

    private readonly record struct Entry(double At, double Length, LayoutWidget? Target, string Key, JsonNode? Value, double Number, Kind Kind, OverlayEase Ease, OverlayScript? Child, bool Separate);

    private readonly List<Entry> _entries = [];
    private double _cursor;

    public double Duration { get; private set; }
    public double TimeScale { get; } = timeScale;

    public static double Frames(double frames) => frames / 30 * 1000;

    public OverlayScript Set(LayoutWidget? target, string key, JsonNode? value)
    {
        _entries.Add(new Entry(_cursor, 0, target, key, value, 0, Kind.Set, OverlayEase.Linear, null, false));
        return this;
    }

    public OverlayScript To(LayoutWidget? target, string key, double value, double frames, OverlayEase ease) =>
        Animate(target, key, value, frames, ease, Kind.To);

    /// <summary>Starts offset by <paramref name="offset"/> and ends where the widget is now.</summary>
    public OverlayScript FromOffset(LayoutWidget? target, string key, double offset, double frames, OverlayEase ease) =>
        Animate(target, key, offset, frames, ease, Kind.FromRelative);

    public OverlayScript By(LayoutWidget? target, string key, double offset, double frames, OverlayEase ease) =>
        Animate(target, key, offset, frames, ease, Kind.Relative);

    private OverlayScript Animate(LayoutWidget? target, string key, double number, double frames, OverlayEase ease, Kind kind)
    {
        var length = Frames(frames);
        _entries.Add(new Entry(_cursor, length, target, key, null, number, kind, ease, null, false));
        Duration = Math.Max(Duration, _cursor + length);
        return this;
    }

    /// <summary>Delay in the game's frames.</summary>
    public OverlayScript Delay(double frames) => DelayMs(Frames(frames));

    public OverlayScript DelayMs(double milliseconds)
    {
        _cursor += milliseconds;
        Duration = Math.Max(Duration, _cursor);
        return this;
    }

    public OverlayScript Wait()
    {
        _cursor = Duration;
        return this;
    }

    /// <summary>Runs another script as a step of this one.</summary>
    public OverlayScript Add(OverlayScript script)
    {
        _entries.Add(new Entry(_cursor, 0, null, "", null, 0, Kind.Set, OverlayEase.Linear, script, false));
        Duration = Math.Max(Duration, _cursor + script.Duration);
        return this;
    }

    /// <summary>Starts another script that then runs on its own, the way a step that calls _startScript does.</summary>
    public OverlayScript Start(OverlayScript script)
    {
        _entries.Add(new Entry(_cursor, 0, null, "", null, 0, Kind.Set, OverlayEase.Linear, script, true));
        return this;
    }

    internal void Flatten(List<OverlayTimeline.Step> steps, double offset, double scale)
    {
        scale *= TimeScale;
        foreach (var entry in _entries)
        {
            var at = offset + entry.At / scale;
            if (entry.Child != null)
                entry.Child.Flatten(steps, at, entry.Separate ? 1 : scale);
            else if (entry.Target != null)
                steps.Add(new OverlayTimeline.Step(at, entry.Length / scale, entry.Target, entry.Key, entry.Value, entry.Number,
                    entry.Kind == Kind.Set ? OverlayTimeline.StepKind.Set : entry.Kind == Kind.To ? OverlayTimeline.StepKind.To : entry.Kind == Kind.FromRelative ? OverlayTimeline.StepKind.FromOffset : OverlayTimeline.StepKind.By, entry.Ease));
        }
    }
}

/// <summary>A finished script that can be asked for the state of every widget at any moment.</summary>
public sealed class OverlayTimeline
{
    internal enum StepKind { Set, To, FromOffset, By }

    internal readonly record struct Step(double At, double Length, LayoutWidget Target, string Key, JsonNode? Value, double Number, StepKind Kind, OverlayEase Ease);

    private readonly List<Step> _steps;
    private readonly OverlayPose _start;
    private readonly double[] _from;

    public OverlayTimeline(OverlayScript script, OverlayPose start)
    {
        var steps = new List<Step>();
        script.Flatten(steps, 0, 1);
        _steps = steps.OrderBy(s => s.At).ToList();
        _start = start;
        Duration = _steps.Count == 0 ? 0 : _steps.Max(s => s.At + s.Length);

        // An animation takes its starting value from the widget at the moment it starts, so overlapping animations do not feed each other.
        _from = new double[_steps.Count];
        for (var i = 0; i < _steps.Count; i++)
            if (_steps[i].Kind != StepKind.Set)
                _from[i] = Current(Evaluate(_steps[i].At, i, _steps[i].Target, _steps[i].Key), _steps[i].Target, _steps[i].Key);
    }

    public double Duration { get; }

    public int FrameCount => (int)Math.Ceiling(Duration / OverlayScript.Frames(1)) + 1;

    public OverlayPose At(double milliseconds) => Evaluate(milliseconds, _steps.Count, null, null);

    private OverlayPose Evaluate(double milliseconds, int stepsToUse, LayoutWidget? onlyTarget, string? onlyKey)
    {
        var pose = _start.Copy();
        for (var i = 0; i < stepsToUse; i++)
        {
            var step = _steps[i];
            if (step.At > milliseconds)
                break;
            if (onlyTarget != null && (!ReferenceEquals(step.Target, onlyTarget) || step.Key != onlyKey))
                continue;
            if (step.Kind == StepKind.Set)
            {
                pose.Set(step.Target, step.Key, step.Value);
                continue;
            }
            var now = _from[i];
            var (from, to) = step.Kind switch
            {
                StepKind.FromOffset => (now + step.Number, now),
                StepKind.By => (now, now + step.Number),
                _ => (now, step.Number),
            };
            var progress = step.Length <= 0 ? 1 : Math.Clamp((milliseconds - step.At) / step.Length, 0, 1);
            pose.Set(step.Target, step.Key, JsonValue.Create(from + (to - from) * Ease(step.Ease, progress)));
        }
        return pose;
    }

    private static double Current(OverlayPose pose, LayoutWidget widget, string key)
    {
        if (JsonEdit.Number(pose.Get(widget, key)) is { } posed)
            return posed;
        if (JsonEdit.Number(widget.Node[key]) is { } own)
            return own;
        return key is "alpha" or "scaleX" or "scaleY" ? 1 : 0;
    }

    /// <summary>The same curves as zinc.utils.Easing with its default power of 2.</summary>
    public static double Ease(OverlayEase ease, double t) => ease switch
    {
        OverlayEase.QuadIn => t * t,
        OverlayEase.QuadOut => 1 - (1 - t) * (1 - t),
        OverlayEase.QuadInOut => t < 0.5 ? 2 * t * t : 1 - 2 * (1 - t) * (1 - t),
        _ => t,
    };
}

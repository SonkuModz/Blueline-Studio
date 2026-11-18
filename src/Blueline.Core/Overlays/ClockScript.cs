using Blueline.Formats.Flash;

namespace Blueline.Core.Overlays;

/// <summary>A node the score clock's code can reach: one of its own fields, or a child found by name under one.</summary>
public sealed record ClockNode(string Field, string? Child = null)
{
    public override string ToString() => Child == null ? Field : $"{Field}/{Child}";
}

/// <summary>A value in a script: a number, or one of the clock's own number fields times a factor.</summary>
public sealed record ClockValue(double Number, string? Field = null, double Factor = 1)
{
    public static implicit operator ClockValue(double number) => new(number);
    public static ClockValue Of(string field, double factor = 1) => new(0, field, factor);
}

public abstract record ClockStep;

/// <summary>Values set when the script starts (AnimScript.addProps), so every run starts the same.</summary>
public sealed record ClockProps(ClockNode Node, IReadOnlyList<(string Property, ClockValue Value)> Values) : ClockStep;

/// <summary>An animation from the node's value at that moment to these, starting at the cursor without moving it.</summary>
public sealed record ClockAnim(ClockNode Node, IReadOnlyList<(string Property, ClockValue Value)> Values, double Frames, string Easing) : ClockStep;

/// <summary>Moves the cursor on by some frames.</summary>
public sealed record ClockDelay(double Frames) : ClockStep;

/// <summary>Moves the cursor to the end of everything so far.</summary>
public sealed record ClockWait : ClockStep;

/// <summary>Calls one of the clock's own methods at the cursor.</summary>
public sealed record ClockCall(string Method) : ClockStep;

/// <summary>
/// A score clock animation written as data, in the 30 per second frames the game's scripts use. The same steps are compiled
/// into the game's ActionScript (<see cref="Compile"/>) and played here for tests (<see cref="ValueAt"/>), so what was measured
/// on the broadcast video can be checked frame by frame without the game.
/// </summary>
public static class ClockScript
{
    public const string ClockClass = "nhl.overlays.scoreclock::ScoreClockOverlay";
    private const string Private = "nhl.overlays.scoreclock:ScoreClockOverlay::";

    /// <summary>Turns a list of steps that each carry a start frame into a script with delays between them. Steps at the same
    /// frame keep their order.</summary>
    public static List<ClockStep> FromTimeline(IEnumerable<(double Frame, ClockStep Step)> timeline)
    {
        var steps = new List<ClockStep>();
        var cursor = 0.0;
        foreach (var (frame, step) in timeline.OrderBy(t => t.Frame))
        {
            if (frame > cursor + 1e-9)
            {
                steps.Add(new ClockDelay(Math.Round(frame - cursor, 3)));
                cursor = frame;
            }
            steps.Add(step);
        }
        return steps;
    }

    /// <summary>When each step starts, by the engine's rules: an animation starts at the cursor without moving it, a delay
    /// moves it, a wait moves it to the end of everything so far.</summary>
    public static IEnumerable<(double Start, ClockStep Step)> Starts(IReadOnlyList<ClockStep> steps)
    {
        double cursor = 0, end = 0;
        foreach (var step in steps)
        {
            switch (step)
            {
                case ClockDelay d:
                    cursor += d.Frames;
                    end = Math.Max(end, cursor);
                    break;
                case ClockWait:
                    cursor = end;
                    break;
                case ClockAnim a:
                    end = Math.Max(end, cursor + a.Frames);
                    break;
            }
            yield return (cursor, step);
        }
    }

    public static double Length(IReadOnlyList<ClockStep> steps) =>
        Starts(steps).Select(s => s.Start + (s.Step is ClockAnim a ? a.Frames : 0)).DefaultIfEmpty(0).Max();

    public static double Ease(string easing, double t) => easing switch
    {
        "linear" => t,
        "quadIn" => t * t,
        "quadOut" => 1 - (1 - t) * (1 - t),
        "quadInOut" => t < 0.5 ? 2 * t * t : 1 - 2 * (1 - t) * (1 - t),
        _ => throw new ArgumentException("Unknown easing " + easing),
    };

    /// <summary>A property's value at a frame, given the clock's fields and the value it had before the script.</summary>
    public static double ValueAt(IReadOnlyList<ClockStep> steps, ClockNode node, string property, double frame, double before,
        Func<string, double>? field = null)
    {
        double Resolve(ClockValue v) => v.Field == null ? v.Number : (field ?? (_ => throw new InvalidOperationException("No field " + v.Field)))(v.Field) * v.Factor;
        var value = before;
        foreach (var (start, step) in Starts(steps))
        {
            switch (step)
            {
                case ClockProps p when p.Node == node && p.Values.FirstOrDefault(v => v.Property == property) is { Property: not null } set:
                    value = Resolve(set.Value);
                    break;
                case ClockAnim a when a.Node == node && a.Values.FirstOrDefault(v => v.Property == property) is { Property: not null } target && frame >= start:
                    var t = a.Frames <= 0 ? 1 : Math.Clamp((frame - start) / a.Frames, 0, 1);
                    value += (Resolve(target.Value) - value) * Ease(a.Easing, t);
                    break;
            }
        }
        return value;
    }

    public const string UiCode = "nhl/ui/lib/nhluidata.swf";

    /// <summary>The game's nhluidata.swf with one of the score clock's script methods replaced by these steps.</summary>
    public static byte[] CompileInto(byte[] swfBytes, string method, IReadOnlyList<ClockStep> steps)
    {
        var swf = SwfFile.Parse(swfBytes);
        foreach (var tag in swf.Tags.Where(t => t.Code == SwfTag.DoAbc))
        {
            var start = SwfFile.AbcStart(tag.Body).Start;
            var abc = AbcFile.Parse(tag.Body[start..]);
            if (!abc.Instances.Any(i => abc.FullNameOf(i.Name) == ClockClass))
                continue;
            Compile(abc, method, steps);
            tag.Body = [.. tag.Body[..start], .. abc.ToBytes()];
            return swf.ToBytes();
        }
        throw new InvalidDataException("This swf does not hold the score clock's code.");
    }

    /// <summary>
    /// Writes the steps as the body of one of the clock's methods that returns an AnimScript (like _createScriptAnimGoal):
    /// a new AnimScript in local 2, its time scale set to 1, then one call per step, then the script returned.
    /// </summary>
    public static void Compile(AbcFile abc, string method, IReadOnlyList<ClockStep> steps)
    {
        var clock = abc.Class(ClockClass);
        var body = abc.BodyOf(abc.MethodOf(clock, method));

        int Name(string fullName)
        {
            for (var i = 1; i < abc.Names.Count; i++)
                if (abc.Names[i].IsQName && abc.FullNameOf(i) == fullName)
                    return i;
            throw new KeyNotFoundException("The code has no name " + fullName);
        }
        int Field(string field) => Name(Private + field);
        var animScript = Name("zinc.ui.anim::AnimScript");
        var animDirect = Name("zinc.ui.anim::AnimDirect");
        var element = Name("zinc.ui.element::Element");
        var easing = Name("zinc.utils::Easing");
        var frames = Name("nhl.overlays:OverlayBase::_framesToMilliseconds");
        var getNode = abc.PublicName("getNodeByName");

        var asm = new AbcAssembler(abc);
        void Node(ClockNode node)
        {
            asm.GetLocal(0).GetProperty(Field(node.Field));
            if (node.Child != null)
                asm.PushString(node.Child).CallProperty(getNode, 1);
            asm.Coerce(element);
        }
        void Value(ClockValue v)
        {
            if (v.Field == null)
                asm.PushNumber(v.Number);
            else
            {
                asm.GetLocal(0).GetProperty(Field(v.Field));
                if (v.Factor != 1)
                    asm.PushNumber(v.Factor).Multiply();
            }
        }
        void Values(IReadOnlyList<(string Property, ClockValue Value)> values)
        {
            foreach (var (property, value) in values)
            {
                asm.PushString(property);
                Value(value);
            }
            asm.NewObject(values.Count);
        }
        void Milliseconds(double f) => asm.FindPropStrict(frames).PushNumber(f).CallProperty(frames, 1);

        asm.GetLocal(0).PushScope();
        asm.FindPropStrict(animScript).ConstructProp(animScript, 0).Coerce(animScript).SetLocal(2);
        asm.GetLocal(2).PushNumber(1).SetProperty(abc.PublicName("timeScale"));
        foreach (var step in steps)
        {
            asm.GetLocal(2);
            switch (step)
            {
                case ClockProps p:
                    Values(p.Values);
                    Node(p.Node);
                    asm.CallPropVoid(abc.PublicName("addProps"), 2);
                    break;
                case ClockAnim a:
                    asm.FindPropStrict(animDirect);
                    Values(a.Values);
                    Milliseconds(a.Frames);
                    asm.FindPropStrict(easing).GetProperty(easing).GetProperty(abc.PublicName(a.Easing));
                    asm.ConstructProp(animDirect, 3);
                    Node(a.Node);
                    asm.CallPropVoid(abc.PublicName("addAnim"), 2);
                    break;
                case ClockDelay d:
                    Milliseconds(d.Frames);
                    asm.CallPropVoid(abc.PublicName("addDelay"), 1);
                    break;
                case ClockWait:
                    asm.CallPropVoid(abc.PublicName("addWait"), 0);
                    break;
                case ClockCall c:
                    asm.GetLocal(0).GetProperty(Field(c.Method));
                    asm.CallPropVoid(abc.PublicName("addCall"), 1);
                    break;
            }
        }
        asm.GetLocal(2).ReturnValue();

        body.Code = asm.Code;
        body.MaxStack = asm.MaxStack;
        body.LocalCount = Math.Max(body.LocalCount, 3);
        abc.Changed(body);
    }
}

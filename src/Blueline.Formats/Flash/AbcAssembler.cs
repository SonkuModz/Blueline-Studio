namespace Blueline.Formats.Flash;

/// <summary>
/// Writes straight-line AVM2 code (no branches) and keeps the deepest stack it reaches, for the method body's max_stack.
/// Each method takes the stack effect it has, from the AVM2 overview, chapter 5.
/// </summary>
public sealed class AbcAssembler(AbcFile abc)
{
    private readonly AbcFile.Writer _out = new();
    private int _depth;

    public int MaxStack { get; private set; }
    public byte[] Code => _out.ToArray();

    private void Op(int opcode, int pushes, int pops)
    {
        _out.U8(opcode);
        _depth -= pops;
        if (_depth < 0)
            throw new InvalidOperationException("The stack would go below empty.");
        _depth += pushes;
        MaxStack = Math.Max(MaxStack, _depth);
    }

    private void U30(int value) => _out.U30((uint)value);

    public AbcAssembler GetLocal(int index)
    {
        if (index <= 3)
            Op(0xD0 + index, 1, 0);
        else
        {
            Op(0x62, 1, 0);
            U30(index);
        }
        return this;
    }

    public AbcAssembler SetLocal(int index)
    {
        if (index <= 3)
            Op(0xD4 + index, 0, 1);
        else
        {
            Op(0x63, 0, 1);
            U30(index);
        }
        return this;
    }

    public AbcAssembler PushScope() { Op(0x30, 0, 1); return this; }
    public AbcAssembler Pop() { Op(0x29, 0, 1); return this; }
    public AbcAssembler ReturnValue() { Op(0x48, 0, 1); return this; }
    public AbcAssembler Multiply() { Op(0xA2, 1, 2); return this; }
    public AbcAssembler Add() { Op(0xA0, 1, 2); return this; }

    public AbcAssembler PushString(string value)
    {
        Op(0x2C, 1, 0);
        U30(abc.AddString(value));
        return this;
    }

    public AbcAssembler PushNumber(double value)
    {
        if (value == Math.Floor(value) && value is >= -128 and <= 127)
        {
            Op(0x24, 1, 0);
            _out.U8((int)value & 0xFF);
        }
        else if (value == Math.Floor(value) && value is >= 0 and <= 32767)
        {
            Op(0x25, 1, 0);
            U30((int)value);
        }
        else
        {
            Op(0x2F, 1, 0);
            U30(abc.AddDouble(value));
        }
        return this;
    }

    public AbcAssembler PushBool(bool value) { Op(value ? 0x26 : 0x27, 1, 0); return this; }

    public AbcAssembler GetProperty(int name) { Op(0x66, 1, 1); U30(name); return this; }
    /// <summary>receiver, value → (nothing)</summary>
    public AbcAssembler SetProperty(int name) { Op(0x61, 0, 2); U30(name); return this; }
    public AbcAssembler FindPropStrict(int name) { Op(0x5D, 1, 0); U30(name); return this; }
    public AbcAssembler Coerce(int type) { Op(0x80, 1, 1); U30(type); return this; }
    public AbcAssembler NewObject(int pairs) { Op(0x55, 1, 2 * pairs); U30(pairs); return this; }

    /// <summary>receiver, args... → result</summary>
    public AbcAssembler CallProperty(int name, int args) { Op(0x46, 1, args + 1); U30(name); U30(args); return this; }

    /// <summary>receiver, args... → (nothing)</summary>
    public AbcAssembler CallPropVoid(int name, int args) { Op(0x4F, 0, args + 1); U30(name); U30(args); return this; }

    /// <summary>receiver (from findpropstrict), args... → new object</summary>
    public AbcAssembler ConstructProp(int name, int args) { Op(0x4A, 1, args + 1); U30(name); U30(args); return this; }
}

namespace Blueline.Formats.Flash;

/// <summary>One AVM2 instruction: where it starts, its opcode, its operands and its length in bytes.</summary>
public sealed record AbcInstruction(int Offset, int Opcode, string Name, int[] Operands, int Length);

/// <summary>Decodes AVM2 method code into instructions. Operand formats are from the AVM2 overview, chapter 5.</summary>
public static class AbcCode
{
    // u = u30, b = u8, j = s24 branch offset. "debug" is u8 u30 u8 u30.
    private static readonly Dictionary<int, (string Name, string Format)> Ops = new()
    {
        [0x01] = ("bkpt", ""), [0x02] = ("nop", ""), [0x03] = ("throw", ""), [0x04] = ("getsuper", "u"), [0x05] = ("setsuper", "u"),
        [0x06] = ("dxns", "u"), [0x07] = ("dxnslate", ""), [0x08] = ("kill", "u"), [0x09] = ("label", ""),
        [0x0C] = ("ifnlt", "j"), [0x0D] = ("ifnle", "j"), [0x0E] = ("ifngt", "j"), [0x0F] = ("ifnge", "j"), [0x10] = ("jump", "j"),
        [0x11] = ("iftrue", "j"), [0x12] = ("iffalse", "j"), [0x13] = ("ifeq", "j"), [0x14] = ("ifne", "j"), [0x15] = ("iflt", "j"),
        [0x16] = ("ifle", "j"), [0x17] = ("ifgt", "j"), [0x18] = ("ifge", "j"), [0x19] = ("ifstricteq", "j"), [0x1A] = ("ifstrictne", "j"),
        [0x1B] = ("lookupswitch", "*"), [0x1C] = ("pushwith", ""), [0x1D] = ("popscope", ""), [0x1E] = ("nextname", ""), [0x1F] = ("hasnext", ""),
        [0x20] = ("pushnull", ""), [0x21] = ("pushundefined", ""), [0x23] = ("nextvalue", ""), [0x24] = ("pushbyte", "b"), [0x25] = ("pushshort", "u"),
        [0x26] = ("pushtrue", ""), [0x27] = ("pushfalse", ""), [0x28] = ("pushnan", ""), [0x29] = ("pop", ""), [0x2A] = ("dup", ""), [0x2B] = ("swap", ""),
        [0x2C] = ("pushstring", "u"), [0x2D] = ("pushint", "u"), [0x2E] = ("pushuint", "u"), [0x2F] = ("pushdouble", "u"), [0x30] = ("pushscope", ""),
        [0x31] = ("pushnamespace", "u"), [0x32] = ("hasnext2", "uu"),
        [0x40] = ("newfunction", "u"), [0x41] = ("call", "u"), [0x42] = ("construct", "u"), [0x43] = ("callmethod", "uu"), [0x44] = ("callstatic", "uu"),
        [0x45] = ("callsuper", "uu"), [0x46] = ("callproperty", "uu"), [0x47] = ("returnvoid", ""), [0x48] = ("returnvalue", ""), [0x49] = ("constructsuper", "u"),
        [0x4A] = ("constructprop", "uu"), [0x4C] = ("callproplex", "uu"), [0x4E] = ("callsupervoid", "uu"), [0x4F] = ("callpropvoid", "uu"),
        [0x53] = ("applytype", "u"), [0x55] = ("newobject", "u"), [0x56] = ("newarray", "u"), [0x57] = ("newactivation", ""), [0x58] = ("newclass", "u"),
        [0x59] = ("getdescendants", "u"), [0x5A] = ("newcatch", "u"), [0x5D] = ("findpropstrict", "u"), [0x5E] = ("findproperty", "u"), [0x5F] = ("finddef", "u"),
        [0x60] = ("getlex", "u"), [0x61] = ("setproperty", "u"), [0x62] = ("getlocal", "u"), [0x63] = ("setlocal", "u"), [0x64] = ("getglobalscope", ""),
        [0x65] = ("getscopeobject", "b"), [0x66] = ("getproperty", "u"), [0x68] = ("initproperty", "u"), [0x6A] = ("deleteproperty", "u"),
        [0x6C] = ("getslot", "u"), [0x6D] = ("setslot", "u"), [0x6E] = ("getglobalslot", "u"), [0x6F] = ("setglobalslot", "u"),
        [0x70] = ("convert_s", ""), [0x71] = ("esc_xelem", ""), [0x72] = ("esc_xattr", ""), [0x73] = ("convert_i", ""), [0x74] = ("convert_u", ""),
        [0x75] = ("convert_d", ""), [0x76] = ("convert_b", ""), [0x77] = ("convert_o", ""), [0x78] = ("checkfilter", ""),
        [0x80] = ("coerce", "u"), [0x82] = ("coerce_a", ""), [0x85] = ("coerce_s", ""), [0x86] = ("astype", "u"), [0x87] = ("astypelate", ""),
        [0x90] = ("negate", ""), [0x91] = ("increment", ""), [0x92] = ("inclocal", "u"), [0x93] = ("decrement", ""), [0x94] = ("declocal", "u"),
        [0x95] = ("typeof", ""), [0x96] = ("not", ""), [0x97] = ("bitnot", ""),
        [0xA0] = ("add", ""), [0xA1] = ("subtract", ""), [0xA2] = ("multiply", ""), [0xA3] = ("divide", ""), [0xA4] = ("modulo", ""), [0xA5] = ("lshift", ""),
        [0xA6] = ("rshift", ""), [0xA7] = ("urshift", ""), [0xA8] = ("bitand", ""), [0xA9] = ("bitor", ""), [0xAA] = ("bitxor", ""), [0xAB] = ("equals", ""),
        [0xAC] = ("strictequals", ""), [0xAD] = ("lessthan", ""), [0xAE] = ("lessequals", ""), [0xAF] = ("greaterthan", ""), [0xB0] = ("greaterequals", ""),
        [0xB1] = ("instanceof", ""), [0xB2] = ("istype", "u"), [0xB3] = ("istypelate", ""), [0xB4] = ("in", ""),
        [0xC0] = ("increment_i", ""), [0xC1] = ("decrement_i", ""), [0xC2] = ("inclocal_i", "u"), [0xC3] = ("declocal_i", "u"), [0xC4] = ("negate_i", ""),
        [0xC5] = ("add_i", ""), [0xC6] = ("subtract_i", ""), [0xC7] = ("multiply_i", ""),
        [0xD0] = ("getlocal0", ""), [0xD1] = ("getlocal1", ""), [0xD2] = ("getlocal2", ""), [0xD3] = ("getlocal3", ""),
        [0xD4] = ("setlocal0", ""), [0xD5] = ("setlocal1", ""), [0xD6] = ("setlocal2", ""), [0xD7] = ("setlocal3", ""),
        [0xEF] = ("debug", "bubu"), [0xF0] = ("debugline", "u"), [0xF1] = ("debugfile", "u"), [0xF2] = ("bkptline", "u"), [0xF3] = ("timestamp", ""),
    };

    public static int OpcodeOf(string name) => Ops.First(o => o.Value.Name == name).Key;

    public static IReadOnlyList<AbcInstruction> Decode(byte[] code)
    {
        var list = new List<AbcInstruction>();
        var r = new AbcFile.Reader(code);
        while (r.At < code.Length)
        {
            var at = r.At;
            var op = r.U8();
            if (!Ops.TryGetValue(op, out var spec))
                throw new InvalidDataException($"Unknown opcode 0x{op:X2} at {at}.");
            var operands = new List<int>();
            if (spec.Format == "*")
            {
                operands.Add(r.S24());
                var cases = (int)r.U30();
                operands.Add(cases);
                for (var i = 0; i <= cases; i++)
                    operands.Add(r.S24());
            }
            else
                foreach (var f in spec.Format)
                    operands.Add(f switch { 'u' => (int)r.U30(), 'b' => r.U8(), 'j' => r.S24(), _ => throw new InvalidOperationException() });
            list.Add(new AbcInstruction(at, op, spec.Name, operands.ToArray(), r.At - at));
        }
        return list;
    }
}

using System.Text;
using Cameo.Runtime.Vm;

namespace Cameo.Runtime.Stdlib;

/// <summary>
/// string.pack / string.unpack / string.packsize: a port of Lua 5.4's lstrlib.c.
/// VM strings hold one byte per char, so packed data is built from chars 0..255.
/// </summary>
internal static class StringPack
{
    private const int MaxIntSize = 16;
    private const int SzInt = 8;          // sizeof(lua_Integer)
    private const int NativeAlign = 8;    // maximum alignment of the reference platform
    private const long MaxSize = int.MaxValue;

    private enum KOption { Int, Uint, Float, Number, Double, Char, String, Zstr, Padding, PaddAlign, Nop }

    private struct Header
    {
        public bool Little;
        public int MaxAlign;
        public string Fmt;
        public int Pos;
        public bool AtEnd => Pos >= Fmt.Length;
    }

    public static void Register(LuaState L, Table s)
    {
        L.RegisterInstance<string>(s, "pack", Pack);
        L.RegisterInstance<string>(s, "unpack", Unpack);
        L.RegisterInstance<string>(s, "packsize", PackSize);
    }

    private static bool Digit(ref Header h) => h.Pos < h.Fmt.Length && h.Fmt[h.Pos] >= '0' && h.Fmt[h.Pos] <= '9';

    private static int GetNum(ref Header h, int df)
    {
        if (!Digit(ref h)) return df;
        int a = 0;
        do
        {
            a = a * 10 + (h.Fmt[h.Pos++] - '0');
        } while (Digit(ref h) && a <= ((int)MaxSize - 9) / 10);
        return a;
    }

    private static int GetNumLimit(ref Header h, int df)
    {
        int sz = GetNum(ref h, df);
        if (sz > MaxIntSize || sz <= 0)
            throw new LuaException($"integral size ({sz}) out of limits [1,{MaxIntSize}]");
        return sz;
    }

    private static KOption GetOption(ref Header h, out int size)
    {
        char opt = h.Fmt[h.Pos++];
        size = 0;
        switch (opt)
        {
            case 'b': size = 1; return KOption.Int;
            case 'B': size = 1; return KOption.Uint;
            case 'h': size = 2; return KOption.Int;
            case 'H': size = 2; return KOption.Uint;
            case 'l': size = 8; return KOption.Int;
            case 'L': size = 8; return KOption.Uint;
            case 'j': size = 8; return KOption.Int;
            case 'J': size = 8; return KOption.Uint;
            case 'T': size = 8; return KOption.Uint;
            case 'f': size = 4; return KOption.Float;
            case 'n': size = 8; return KOption.Number;
            case 'd': size = 8; return KOption.Double;
            case 'i': size = GetNumLimit(ref h, 4); return KOption.Int;
            case 'I': size = GetNumLimit(ref h, 4); return KOption.Uint;
            case 's': size = GetNumLimit(ref h, 8); return KOption.String;
            case 'c':
                size = GetNum(ref h, -1);
                if (size == -1) throw new LuaException("missing size for format option 'c'");
                return KOption.Char;
            case 'z': return KOption.Zstr;
            case 'x': size = 1; return KOption.Padding;
            case 'X': return KOption.PaddAlign;
            case ' ': break;
            case '<': h.Little = true; break;
            case '>': h.Little = false; break;
            case '=': h.Little = true; break;
            case '!': h.MaxAlign = GetNumLimit(ref h, NativeAlign); break;
            default: throw new LuaException($"invalid format option '{opt}'");
        }
        return KOption.Nop;
    }

    private static KOption GetDetails(ref CallContext c, ref Header h, long totalSize, out int size, out int ntoalign)
    {
        KOption opt = GetOption(ref h, out size);
        int align = size;
        if (opt == KOption.PaddAlign)
        {
            if (h.AtEnd || GetOption(ref h, out align) == KOption.Char || align == 0)
                throw c.ArgError(0, "invalid next option for option 'X'");
        }
        if (align <= 1 || opt == KOption.Char) ntoalign = 0;
        else
        {
            if (align > h.MaxAlign) align = h.MaxAlign;
            if ((align & (align - 1)) != 0) throw c.ArgError(0, "format asks for alignment not power of 2");
            ntoalign = (align - (int)(totalSize & (align - 1))) & (align - 1);
        }
        return opt;
    }

    private static void PackInt(StringBuilder b, ulong n, bool little, int size, bool neg)
    {
        Span<char> buff = stackalloc char[MaxIntSize];
        buff[little ? 0 : size - 1] = (char)(n & 0xFF);
        for (int i = 1; i < size; i++)
        {
            n >>= 8;
            buff[little ? i : size - 1 - i] = (char)(n & 0xFF);
        }
        if (neg && size > SzInt)
            for (int i = SzInt; i < size; i++) buff[little ? i : size - 1 - i] = (char)0xFF;
        b.Append(buff.Slice(0, size));
    }

    private static void PackBytes(StringBuilder b, ulong bits, int size, bool little)
    {
        // `bits` is the value's bit pattern; byte 0 is the least significant
        for (int i = 0; i < size; i++)
        {
            int idx = little ? i : size - 1 - i;
            b.Append((char)((bits >> (8 * idx)) & 0xFF));
        }
    }

    private static int Pack(ref CallContext c)
    {
        var h = new Header { Little = true, MaxAlign = 1, Fmt = c.CheckString(0) };
        var b = new StringBuilder();
        int arg = 0;
        long totalSize = 0;
        while (!h.AtEnd)
        {
            KOption opt = GetDetails(ref c, ref h, totalSize, out int size, out int ntoalign);
            totalSize += ntoalign + size;
            while (ntoalign-- > 0) b.Append('\0');
            arg++;
            // lstrlib pushes a nil after the arguments, so a missing argument reads as nil
            if (arg >= c.Count && opt <= KOption.Zstr)
                throw c.ArgError(arg, (opt >= KOption.Char ? "string" : "number") + " expected, got nil");
            switch (opt)
            {
                case KOption.Int:
                    {
                        long n = c.CheckInt(arg);
                        if (size < SzInt)
                        {
                            long lim = 1L << (size * 8 - 1);
                            if (!(-lim <= n && n < lim)) throw c.ArgError(arg, "integer overflow");
                        }
                        PackInt(b, (ulong)n, h.Little, size, n < 0);
                        break;
                    }
                case KOption.Uint:
                    {
                        long n = c.CheckInt(arg);
                        if (size < SzInt && !((ulong)n < (1UL << (size * 8)))) throw c.ArgError(arg, "unsigned overflow");
                        PackInt(b, (ulong)n, h.Little, size, false);
                        break;
                    }
                case KOption.Float:
                    PackBytes(b, BitConverter.SingleToUInt32Bits((float)c.CheckNumber(arg)), 4, h.Little);
                    break;
                case KOption.Number:
                case KOption.Double:
                    PackBytes(b, BitConverter.DoubleToUInt64Bits(c.CheckNumber(arg)), 8, h.Little);
                    break;
                case KOption.Char:
                    {
                        string s = c.CheckString(arg);
                        if (s.Length > size) throw c.ArgError(arg, "string longer than given size");
                        b.Append(s);
                        b.Append('\0', size - s.Length);
                        break;
                    }
                case KOption.String:
                    {
                        string s = c.CheckString(arg);
                        if (!(size >= 8 || (ulong)s.Length < (1UL << (size * 8))))
                            throw c.ArgError(arg, "string length does not fit in given size");
                        PackInt(b, (ulong)s.Length, h.Little, size, false);
                        b.Append(s);
                        totalSize += s.Length;
                        break;
                    }
                case KOption.Zstr:
                    {
                        string s = c.CheckString(arg);
                        if (s.IndexOf('\0') >= 0) throw c.ArgError(arg, "string contains zeros");
                        b.Append(s).Append('\0');
                        totalSize += s.Length + 1;
                        break;
                    }
                case KOption.Padding:
                    b.Append('\0');
                    arg--;
                    break;
                default:
                    arg--;
                    break;
            }
        }
        return c.Return(b.ToString());
    }

    private static int PackSize(ref CallContext c)
    {
        var h = new Header { Little = true, MaxAlign = 1, Fmt = c.CheckString(0) };
        long totalSize = 0;
        while (!h.AtEnd)
        {
            KOption opt = GetDetails(ref c, ref h, totalSize, out int size, out int ntoalign);
            if (opt == KOption.String || opt == KOption.Zstr) throw c.ArgError(0, "variable-length format");
            size += ntoalign;
            if (!(totalSize <= MaxSize - size)) throw c.ArgError(0, "format result too large");
            totalSize += size;
        }
        return c.Return(Value.FromInt(totalSize));
    }

    private static long UnpackInt(string str, int at, bool little, int size, bool signed)
    {
        ulong res = 0;
        int limit = size <= SzInt ? size : SzInt;
        for (int i = limit - 1; i >= 0; i--)
        {
            res <<= 8;
            res |= (byte)str[at + (little ? i : size - 1 - i)];
        }
        if (size < SzInt)
        {
            if (signed)
            {
                ulong mask = 1UL << (size * 8 - 1);
                res = unchecked((res ^ mask) - mask);
            }
        }
        else if (size > SzInt)
        {
            int mask = (!signed || (long)res >= 0) ? 0 : 0xFF;
            for (int i = limit; i < size; i++)
                if ((byte)str[at + (little ? i : size - 1 - i)] != mask)
                    throw new LuaException($"{size}-byte integer does not fit into Lua Integer");
        }
        return (long)res;
    }

    private static ulong UnpackBytes(string str, int at, int size, bool little)
    {
        ulong bits = 0;
        for (int i = 0; i < size; i++)
        {
            int idx = little ? i : size - 1 - i;
            bits |= (ulong)(byte)str[at + i] << (8 * idx);
        }
        return bits;
    }

    private static int Unpack(ref CallContext c)
    {
        var h = new Header { Little = true, MaxAlign = 1, Fmt = c.CheckString(0) };
        string data = c.CheckString(1);
        long ld = data.Length;
        long init = c.OptInt(2, 1);
        long pos = (init > 0 ? init : init == 0 ? 1 : init < -ld ? 1 : ld + init + 1) - 1;
        if (!(pos <= ld)) throw c.ArgError(2, "initial position out of string");
        var results = new List<Value>();
        while (!h.AtEnd)
        {
            KOption opt = GetDetails(ref c, ref h, pos, out int size, out int ntoalign);
            if (!((long)ntoalign + size <= ld - pos)) throw c.ArgError(1, "data string too short");
            pos += ntoalign;
            int at = (int)pos;
            switch (opt)
            {
                case KOption.Int:
                case KOption.Uint:
                    results.Add(Value.FromInt(UnpackInt(data, at, h.Little, size, opt == KOption.Int)));
                    break;
                case KOption.Float:
                    results.Add(Value.FromFloat(BitConverter.UInt32BitsToSingle((uint)UnpackBytes(data, at, 4, h.Little))));
                    break;
                case KOption.Number:
                case KOption.Double:
                    results.Add(Value.FromFloat(BitConverter.UInt64BitsToDouble(UnpackBytes(data, at, 8, h.Little))));
                    break;
                case KOption.Char:
                    results.Add(Value.FromString(data.Substring(at, size)));
                    break;
                case KOption.String:
                    {
                        ulong len = (ulong)UnpackInt(data, at, h.Little, size, false);
                        if (!(len <= (ulong)(ld - pos - size))) throw c.ArgError(1, "data string too short");
                        results.Add(Value.FromString(data.Substring(at + size, (int)len)));
                        pos += (long)len;
                        break;
                    }
                case KOption.Zstr:
                    {
                        int end = data.IndexOf('\0', at);
                        long len = end < 0 ? ld - pos : end - at;
                        if (!(pos + len < ld)) throw c.ArgError(1, "unfinished string for format 'z'");
                        results.Add(Value.FromString(data.Substring(at, (int)len)));
                        pos += len + 1;
                        break;
                    }
            }
            pos += size;
        }
        results.Add(Value.FromInt(pos + 1));
        return c.Return(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(results));
    }
}

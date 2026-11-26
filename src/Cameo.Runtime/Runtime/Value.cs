using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Cameo.Runtime.Vm;

public enum ValueKind : byte
{
    Nil = 0,
    Boolean,
    Integer,
    Float,
    String,
    Table,
    Function,
    Userdata,
    Thread,
    /// <summary>Internal: a heap cell holding a captured local. Never visible to programs.</summary>
    Box,
}

/// <summary>
/// A dynamically typed VM value: three machine words (payload, object reference, kind).
/// The fields do not overlap and are all word-sized on purpose: it lets the JIT keep values
/// in registers and copy them with plain word moves (an overlapping/byte-tagged layout
/// causes store-forwarding stalls on every copy).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct Value : IEquatable<Value>
{
    private readonly long _bits;   // integer, boolean (0/1) or the bits of a double
    public readonly object? O;
    private readonly long _kind;

    public ValueKind Kind { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => (ValueKind)_kind; }
    /// <summary>Integer payload (valid for Integer and Boolean kinds).</summary>
    public long I { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => _bits; }
    /// <summary>Float payload (valid for the Float kind).</summary>
    public double N { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => BitConverter.Int64BitsToDouble(_bits); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Value(ValueKind kind, long i, object? o) { _bits = i; O = o; _kind = (long)kind; }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Value(double n) { _bits = BitConverter.DoubleToInt64Bits(n); O = null; _kind = (long)ValueKind.Float; }

    public static readonly Value Nil = default;
    public static readonly Value True = new(ValueKind.Boolean, 1, null);
    public static readonly Value False = new(ValueKind.Boolean, 0, null);

    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Value FromBool(bool b) => new(ValueKind.Boolean, b ? 1 : 0, null);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Value FromInt(long i) => new(ValueKind.Integer, i, null);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Value FromFloat(double d) => new(d);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Value FromString(string s) => new(ValueKind.String, 0, s);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Value FromTable(Table t) => new(ValueKind.Table, 0, t);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Value FromFunction(LuaFunction f) => new(ValueKind.Function, 0, f);
    public static Value FromFunction(NativeFn fn, string name = "?") => new(ValueKind.Function, 0, new NativeFunction(name, fn));
    public static Value FromUserdata(object o) => new(ValueKind.Userdata, 0, o);

    /// <summary>A VM string from .NET text (encoded as UTF-8). Note that <see cref="FromString"/> and
    /// the implicit conversion from <see cref="string"/> take a string that is ALREADY in the VM's
    /// byte-per-char form; they are equivalent only for ASCII.</summary>
    public static Value FromText(string text) => FromString(LuaText.Encode(text));
    /// <summary>A <c>utf16</c> value wrapping a .NET string as is.</summary>
    public static Value FromUtf16(LuaState state, string text) => new(ValueKind.Userdata, 0, new Utf16String(state, text));
    public bool IsUtf16 => Kind == ValueKind.Userdata && O is Utf16String;
    /// <summary>.NET text of a VM string (decoded from UTF-8) or of a <c>utf16</c> value.</summary>
    public string ToText() => O is Utf16String u ? u.Text : Kind == ValueKind.String ? LuaText.Decode((string)O!) : LuaText.Decode(ToString());
    public static Value FromThread(LuaThread t) => new(ValueKind.Thread, 0, t);
    internal static Value FromBox(Box b) => new(ValueKind.Box, 0, b);

    // In-place stores used by the interpreter's hot paths.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SetInt(ref Value slot, long v)
    {
        Unsafe.AsRef(in slot._bits) = v;
        Unsafe.AsRef(in slot.O) = null;
        Unsafe.AsRef(in slot._kind) = (long)ValueKind.Integer;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SetFloat(ref Value slot, double v)
    {
        Unsafe.AsRef(in slot._bits) = BitConverter.DoubleToInt64Bits(v);
        Unsafe.AsRef(in slot.O) = null;
        Unsafe.AsRef(in slot._kind) = (long)ValueKind.Float;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SetBool(ref Value slot, bool b)
    {
        Unsafe.AsRef(in slot._bits) = b ? 1 : 0;
        Unsafe.AsRef(in slot.O) = null;
        Unsafe.AsRef(in slot._kind) = (long)ValueKind.Boolean;
    }
    /// <summary>Copies a value into a slot, skipping the GC write barrier when neither the old
    /// nor the new value holds an object reference (numbers, booleans, nil).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Copy(ref Value dst, in Value src)
    {
        object? o = src.O;
        Unsafe.AsRef(in dst._bits) = src._bits;
        Unsafe.AsRef(in dst._kind) = src._kind;
        if (o != null || dst.O != null) Unsafe.AsRef(in dst.O) = o;
    }

    /// <summary>Overwrites only the integer payload of a slot already known to hold an integer.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SetIntPayload(ref Value slot, long v) => Unsafe.AsRef(in slot._bits) = v;

    public static implicit operator Value(bool b) => FromBool(b);
    public static implicit operator Value(long i) => FromInt(i);
    public static implicit operator Value(int i) => FromInt(i);
    public static implicit operator Value(double d) => FromFloat(d);
    public static implicit operator Value(string? s) => s is null ? Nil : FromString(s);
    public static implicit operator Value(Table? t) => t is null ? Nil : FromTable(t);
    public static implicit operator Value(LuaFunction? f) => f is null ? Nil : FromFunction(f);

    public bool IsNil { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => Kind == ValueKind.Nil; }
    public bool IsBoolean => Kind == ValueKind.Boolean;
    public bool IsInteger => Kind == ValueKind.Integer;
    public bool IsFloat => Kind == ValueKind.Float;
    public bool IsNumber => Kind == ValueKind.Integer || Kind == ValueKind.Float;
    public bool IsString => Kind == ValueKind.String;
    public bool IsTable => Kind == ValueKind.Table;
    public bool IsFunction => Kind == ValueKind.Function;
    public bool IsUserdata => Kind == ValueKind.Userdata;
    public bool IsThread => Kind == ValueKind.Thread;

    /// <summary>Lua truthiness: everything except nil and false.</summary>
    public bool IsTruthy { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => Kind > ValueKind.Boolean || (Kind == ValueKind.Boolean && I != 0); }
    public bool IsFalsy { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => !IsTruthy; }

    public bool AsBool => IsTruthy;
    public string AsString => (string)O!;
    public Table AsTable => (Table)O!;
    public LuaFunction AsFunction => (LuaFunction)O!;
    public LuaThread AsThread => (LuaThread)O!;
    /// <summary>Numeric value as a double (valid for Integer and Float kinds).</summary>
    public double AsDouble => Kind == ValueKind.Integer ? I : N;

    /// <summary>Converts a number (or numeric string) to a float.</summary>
    public bool TryToDouble(out double d)
    {
        switch (Kind)
        {
            case ValueKind.Float: d = N; return true;
            case ValueKind.Integer: d = I; return true;
            case ValueKind.String:
                if (LuaNumber.TryParse(AsString, out var v)) { d = v.AsDouble; return true; }
                break;
        }
        d = 0; return false;
    }

    /// <summary>Converts to an integer if the value has an exact integer representation
    /// (integers, integral floats and strings convertible to those).</summary>
    public bool TryToInteger(out long i)
    {
        switch (Kind)
        {
            case ValueKind.Integer: i = I; return true;
            case ValueKind.Float: return LuaNumber.FloatToInteger(N, out i);
            case ValueKind.String:
                if (LuaNumber.TryParse(AsString, out var v)) return v.TryToInteger(out i);
                break;
        }
        i = 0; return false;
    }

    /// <summary>The name returned by the <c>type</c> builtin.</summary>
    public string TypeName => Kind switch
    {
        ValueKind.Nil => "nil",
        ValueKind.Boolean => "boolean",
        ValueKind.Integer or ValueKind.Float => "number",
        ValueKind.String => "string",
        ValueKind.Table => "table",
        ValueKind.Function => "function",
        ValueKind.Userdata => "userdata",
        ValueKind.Thread => "thread",
        _ => "?",
    };

    /// <summary>Raw (primitive) equality, as <c>rawequal</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool RawEquals(in Value o)
    {
        if (Kind == o.Kind)
        {
            switch (Kind)
            {
                case ValueKind.Nil: return true;
                case ValueKind.Boolean:
                case ValueKind.Integer: return I == o.I;
                case ValueKind.Float: return N == o.N;
                case ValueKind.String: return ReferenceEquals(O, o.O) || string.Equals((string)O!, (string)o.O!);
                default: return ReferenceEquals(O, o.O);
            }
        }
        if (Kind == ValueKind.Integer && o.Kind == ValueKind.Float) return I == o.N && LuaNumber.IntFloatEq(I, o.N);
        if (Kind == ValueKind.Float && o.Kind == ValueKind.Integer) return N == o.I && LuaNumber.IntFloatEq(o.I, N);
        return false;
    }

    public bool Equals(Value other) => RawEquals(other);
    public override bool Equals(object? obj) => obj is Value v && RawEquals(v);

    public override int GetHashCode()
    {
        switch (Kind)
        {
            case ValueKind.Nil: return 0;
            case ValueKind.Boolean: return I == 0 ? 1 : 2;
            case ValueKind.Integer: return I.GetHashCode();
            case ValueKind.Float:
                // Floats with integral values must hash like the integer (1 == 1.0).
                if (LuaNumber.FloatToInteger(N, out long i)) return i.GetHashCode();
                return N.GetHashCode();
            case ValueKind.String: return ((string)O!).GetHashCode();
            default: return RuntimeHelpers.GetHashCode(O);
        }
    }

    /// <summary>Basic string conversion without metamethods (see <c>LuaState.ToStringMeta</c>).</summary>
    public override string ToString() => Kind switch
    {
        ValueKind.Nil => "nil",
        ValueKind.Boolean => I != 0 ? "true" : "false",
        ValueKind.Integer => I.ToString(CultureInfo.InvariantCulture),
        ValueKind.Float => LuaNumber.FloatToString(N),
        ValueKind.String => (string)O!,
        ValueKind.Table => "table: 0x" + RuntimeHelpers.GetHashCode(O).ToString("x8"),
        ValueKind.Function => (O is NativeFunction ? "builtin: 0x" : "function: 0x") + RuntimeHelpers.GetHashCode(O).ToString("x8"),
        ValueKind.Thread => "thread: 0x" + RuntimeHelpers.GetHashCode(O).ToString("x8"),
        ValueKind.Userdata => O is Utf16String w ? LuaText.Encode(w.Text) : (O is IUserdata u ? u.TypeName : "userdata") + ": 0x" + RuntimeHelpers.GetHashCode(O).ToString("x8"),
        _ => "?",
    };
}

/// <summary>Heap cell for a local variable captured by a closure.</summary>
public sealed class Box
{
    public Value Value;
    public Box() { }
    public Box(Value v) { Value = v; }
}

/// <summary>Optional interface for host objects exposed as userdata.</summary>
public interface IUserdata
{
    /// <summary>Name used by tostring and error messages (e.g. "FILE*").</summary>
    string TypeName { get; }
    /// <summary>Metatable for this userdata, or null.</summary>
    Table? Metatable { get; set; }
}

/// <summary>Optional interface for userdata types that support deep Starlark-style freezing.</summary>
public interface IFreezable
{
    /// <summary>True once this value has been frozen and rejects further mutation.</summary>
    bool IsFrozen { get; }
    /// <summary>Recursively freezes this value and any nested mutable values.</summary>
    void Freeze(HashSet<object> visited);
}

/// <summary>Optional interface for userdata types that can copy/transfer across VMs (parallel workers and sandboxes).</summary>
public interface IValueTransferable
{
    /// <summary>Produces a value belonging to <paramref name="target"/> using <paramref name="copyChild"/> for nested values.</summary>
    Value TransferTo(LuaState target, Func<Value, Value> copyChild);
}

/// <summary>Number parsing/formatting helpers following Lua 5.4 rules.</summary>
public static class LuaNumber
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    // Verified: verification/IntFloat.dfy, method FloatToInteger
    public static bool FloatToInteger(double d, out long i)
    {
        // -2^63 <= d < 2^63 and integral
        if (d >= -9223372036854775808.0 && d < 9223372036854775808.0)
        {
            i = (long)d;
            return i == d;
        }
        i = 0;
        return false;
    }

    // Verified: verification/IntFloat.dfy, method IntFloatEq
    internal static bool IntFloatEq(long i, double d) => FloatToInteger(d, out long j) && i == j;

    public static string FloatToString(double d)
    {
        if (double.IsNaN(d)) return double.IsNegative(d) ? "-nan" : "nan";
        if (double.IsPositiveInfinity(d)) return "inf";
        if (double.IsNegativeInfinity(d)) return "-inf";
        string s = FormatG(d, 14, false);
        // Lua adds ".0" to floats that look like integers.
        bool looksInt = true;
        foreach (char c in s) if (!(char.IsAsciiDigit(c) || c == '-')) { looksInt = false; break; }
        return looksInt ? s + ".0" : s;
    }

    /// <summary>C-style <c>%.{precision}g</c> formatting.</summary>
    public static string FormatG(double d, int precision, bool alternate, bool upper = false)
    {
        if (double.IsNaN(d)) return upper ? "NAN" : "nan";
        if (double.IsInfinity(d)) return (d < 0 ? "-" : "") + (upper ? "INF" : "inf");
        if (precision == 0) precision = 1;
        if (d == 0)
        {
            string z = double.IsNegative(d) ? "-0" : "0";
            if (alternate && precision > 1) z += "." + new string('0', precision - 1);
            return z;
        }
        // Scientific form with `precision` significant digits gives digits + decimal exponent.
        string e = d.ToString("E" + (precision - 1).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        int ePos = e.IndexOf('E');
        int exp = int.Parse(e.AsSpan(ePos + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        string mant = e.Substring(0, ePos); // like -d.ddddd
        if (exp < -4 || exp >= precision)
        {
            if (!alternate) mant = StripZeros(mant);
            string es = Math.Abs(exp).ToString("00", CultureInfo.InvariantCulture);
            return mant + (upper ? "E" : "e") + (exp < 0 ? "-" : "+") + es;
        }
        string f = d.ToString("F" + Math.Max(0, precision - 1 - exp).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        return alternate ? f : StripZeros(f);
    }

    private static string StripZeros(string s)
    {
        if (s.IndexOf('.') < 0) return s;
        int end = s.Length;
        while (end > 0 && s[end - 1] == '0') end--;
        if (end > 0 && s[end - 1] == '.') end--;
        return s.Substring(0, end);
    }

    /// <summary>Parses a Lua numeral (decimal or hex, integer or float), with optional
    /// surrounding whitespace and sign, as <c>tonumber</c> does.</summary>
    public static bool TryParse(string text, out Value result)
    {
        result = default;
        ReadOnlySpan<char> s = text.AsSpan().Trim(" \t\n\r\f\v");
        if (s.Length == 0) return false;
        bool neg = false;
        int p = 0;
        if (s[0] == '-') { neg = true; p = 1; }
        else if (s[0] == '+') p = 1;
        if (p >= s.Length) return false;
        ReadOnlySpan<char> body = s.Slice(p);
        if (body.Length >= 2 && body[0] == '0' && (body[1] == 'x' || body[1] == 'X'))
            return ParseHex(body.Slice(2), neg, out result);
        // decimal
        bool isFloat = false;
        bool anyDigit = false;
        for (int i = 0; i < body.Length; i++)
        {
            char c = body[i];
            if (char.IsAsciiDigit(c)) anyDigit = true;
            else if (c == '.' ) isFloat = true;
            else if (c == 'e' || c == 'E')
            {
                isFloat = true;
                if (i + 1 < body.Length && (body[i + 1] == '+' || body[i + 1] == '-')) i++;
            }
            else return false;
        }
        if (!anyDigit) return false;
        if (!isFloat)
        {
            // Integer; falls back to float on overflow.
            ulong acc = 0;
            bool overflow = false;
            foreach (char c in body)
            {
                ulong dgt = (ulong)(c - '0');
                if (acc > (ulong.MaxValue - dgt) / 10) { overflow = true; break; }
                acc = acc * 10 + dgt;
            }
            if (!overflow && (acc <= long.MaxValue || (neg && acc == 9223372036854775808UL)))
            {
                result = Value.FromInt(neg ? unchecked(-(long)acc) : (long)acc);
                return true;
            }
        }
        if (!double.TryParse(body, NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out double d))
            return false;
        result = Value.FromFloat(neg ? -d : d);
        return true;
    }

    private static bool ParseHex(ReadOnlySpan<char> s, bool neg, out Value result)
    {
        result = default;
        ulong mant = 0;
        double fmant = 0;
        bool any = false, isFloat = false, seenDot = false, big = false;
        int exp = 0;
        int i = 0;
        for (; i < s.Length; i++)
        {
            char c = s[i];
            int dv = HexVal(c);
            if (dv >= 0)
            {
                any = true;
                mant = unchecked(mant * 16 + (ulong)dv); // integers wrap around, as in Lua
                if (fmant > 1e300) big = true; else fmant = fmant * 16 + dv;
                if (seenDot) exp -= 4;
            }
            else if (c == '.' && !seenDot) { seenDot = true; isFloat = true; }
            else break;
        }
        if (!any) return false;
        if (i < s.Length && (s[i] == 'p' || s[i] == 'P'))
        {
            isFloat = true;
            i++;
            bool eneg = false;
            if (i < s.Length && (s[i] == '+' || s[i] == '-')) { eneg = s[i] == '-'; i++; }
            if (i >= s.Length) return false;
            int e = 0;
            for (; i < s.Length; i++)
            {
                if (!char.IsAsciiDigit(s[i])) return false;
                if (e < 100000) e = e * 10 + (s[i] - '0');
            }
            exp += eneg ? -e : e;
        }
        if (i != s.Length) return false;
        if (isFloat)
        {
            double d = big ? double.PositiveInfinity : fmant * Math.Pow(2, exp);
            result = Value.FromFloat(neg ? -d : d);
        }
        else
        {
            long v = unchecked((long)mant);
            result = Value.FromInt(neg ? unchecked(-v) : v);
        }
        return true;
    }

    public static int HexVal(char c) =>
        c >= '0' && c <= '9' ? c - '0' :
        c >= 'a' && c <= 'f' ? c - 'a' + 10 :
        c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1;
}

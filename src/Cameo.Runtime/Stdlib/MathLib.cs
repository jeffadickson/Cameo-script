using System.Numerics;
using Cameo.Runtime.Vm;

namespace Cameo.Runtime.Stdlib;

public static class MathLib
{
    public static void Open(LuaState L, bool hermetic = false)
    {
        var m = new Table();
        L.Register(m, "abs", Abs);
        L.Register(m, "ceil", Ceil);
        L.Register(m, "floor", Floor);
        L.Register(m, "sqrt", static (ref CallContext c) => c.Return(Math.Sqrt(c.CheckNumber(0))));
        L.Register(m, "sin", static (ref CallContext c) => c.Return(Math.Sin(c.CheckNumber(0))));
        L.Register(m, "cos", static (ref CallContext c) => c.Return(Math.Cos(c.CheckNumber(0))));
        L.Register(m, "tan", static (ref CallContext c) => c.Return(Math.Tan(c.CheckNumber(0))));
        L.Register(m, "asin", static (ref CallContext c) => c.Return(Math.Asin(c.CheckNumber(0))));
        L.Register(m, "acos", static (ref CallContext c) => c.Return(Math.Acos(c.CheckNumber(0))));
        L.Register(m, "atan", static (ref CallContext c) => c.Return(Math.Atan2(c.CheckNumber(0), c.OptNumber(1, 1.0))));
        L.Register(m, "exp", static (ref CallContext c) => c.Return(Math.Exp(c.CheckNumber(0))));
        L.Register(m, "pow", static (ref CallContext c) => c.Return(Math.Pow(c.CheckNumber(0), c.CheckNumber(1))));
        L.Register(m, "log", Log);
        L.Register(m, "fmod", FMod);
        L.Register(m, "modf", Modf);
        L.Register(m, "tointeger", ToInteger);
        L.Register(m, "type", Type);
        L.Register(m, "max", Max);
        L.Register(m, "min", Min);
        L.Register(m, "ult", static (ref CallContext c) => c.Return((ulong)c.CheckInt(0) < (ulong)c.CheckInt(1)));

        if (!hermetic)
        {
            // xoshiro256** generator, as in Lua 5.4 (same sequences for the same seed).
            var state = new ulong[4];
            SetSeed(state, (ulong)DateTime.UtcNow.Ticks, (ulong)Environment.TickCount64 ^ 0x9E3779B97F4A7C15UL);
            L.Register(m, "random", (ref CallContext c) =>
            {
                ulong rv = NextRand(state);
                long low, up;
                switch (c.Count)
                {
                    case 0:
                        return c.Return(Value.FromFloat((rv >> 11) * (0.5 / (1UL << 52))));
                    case 1:
                        low = 1;
                        up = c.CheckInt(0);
                        if (up == 0) return c.Return(Value.FromInt(unchecked((long)rv)));
                        break;
                    case 2:
                        low = c.CheckInt(0);
                        up = c.CheckInt(1);
                        break;
                    default:
                        throw new LuaException("wrong number of arguments");
                }
                if (low > up) throw c.ArgError(0, "interval is empty");
                ulong r = Project(rv, (ulong)up - (ulong)low, state);
                return c.Return(Value.FromInt(unchecked((long)(r + (ulong)low))));
            });
            L.Register(m, "randomseed", (ref CallContext c) =>
            {
                long n1, n2;
                if (c.Count == 0)
                {
                    n1 = DateTime.UtcNow.Ticks;
                    n2 = Environment.TickCount64 ^ unchecked((long)0x9E3779B97F4A7C15UL);
                }
                else
                {
                    n1 = c.CheckInt(0);
                    n2 = c.OptInt(1, 0);
                }
                SetSeed(state, (ulong)n1, (ulong)n2);
                return c.Return(Value.FromInt(n1), Value.FromInt(n2));
            });
        }

        m.SetStr("pi", Value.FromFloat(Math.PI));
        m.SetStr("huge", Value.FromFloat(double.PositiveInfinity));
        m.SetStr("maxinteger", Value.FromInt(long.MaxValue));
        m.SetStr("mininteger", Value.FromInt(long.MinValue));
        // The unary float functions: in `math` and as global built-ins (sin(x), log2(x), ...).
        for (int i = 0; i < MathBuiltins.Names.Length; i++)
        {
            var fn = (MathFn)i;
            string name = MathBuiltins.Names[i];
            var native = Value.FromFunction(new NativeFunction(name, (ref CallContext c) => c.Return(MathBuiltins.Apply(fn, c.CheckNumber(0)))));
            if (fn != MathFn.Log && fn != MathFn.Atan) m.SetStr(name, native); // math.log / math.atan keep their optional 2nd argument
            L.Globals.SetStr(name, fn == MathFn.Log || fn == MathFn.Atan ? m.GetStr(name) : native);
        }
        L.Register(m, "atan2", static (ref CallContext c) => c.Return(Math.Atan2(c.CheckNumber(0), c.CheckNumber(1))));
        L.Register(m, "hypot", static (ref CallContext c) => c.Return(Math.Sqrt(c.CheckNumber(0) * c.CheckNumber(0) + c.CheckNumber(1) * c.CheckNumber(1))));
        L.Register(m, "ldexp", static (ref CallContext c) => c.Return(Math.ScaleB(c.CheckNumber(0), (int)Math.Clamp(c.CheckInt(1), int.MinValue, int.MaxValue))));
        L.Register(m, "frexp", static (ref CallContext c) =>
        {
            double x = c.CheckNumber(0);
            if (x == 0 || double.IsNaN(x) || double.IsInfinity(x)) return c.Return(Value.FromFloat(x), Value.FromInt(0));
            int e = Math.ILogB(x) + 1;
            return c.Return(Value.FromFloat(Math.ScaleB(x, -e)), Value.FromInt(e));
        });
        L.Register(m, "round", static (ref CallContext c) => FloatToIntResult(ref c, Math.Round(c.CheckNumber(0), MidpointRounding.AwayFromZero)));
        L.Register(m, "trunc", static (ref CallContext c) => FloatToIntResult(ref c, Math.Truncate(c.CheckNumber(0))));
        L.Register(m, "sign", static (ref CallContext c) =>
        {
            Value v = c[0];
            if (v.IsInteger) return c.Return(Value.FromInt(Math.Sign(v.I)));
            double d = c.CheckNumber(0);
            return c.Return(Value.FromInt(double.IsNaN(d) ? 0 : Math.Sign(d)));
        });
        L.Register(m, "isnan", static (ref CallContext c) => c.Return(double.IsNaN(c.CheckNumber(0))));
        L.Register(m, "isinf", static (ref CallContext c) => c.Return(double.IsInfinity(c.CheckNumber(0))));
        L.Register(m, "isfinite", static (ref CallContext c) => c.Return(double.IsFinite(c.CheckNumber(0))));
        L.Register(m, "isclose", static (ref CallContext c) =>
        {
            double a = c.CheckNumber(0), b = c.CheckNumber(1);
            double relTol = c.OptNumber(2, 1e-9), absTol = c.OptNumber(3, 0.0);
            if (relTol < 0 || absTol < 0 || double.IsNaN(relTol) || double.IsNaN(absTol))
                throw c.ArgError(2, "tolerances must be non-negative");
            if (a == b) return c.Return(true); // handles +inf == +inf, -inf == -inf, and exact equality
            if (double.IsInfinity(a) || double.IsInfinity(b) || double.IsNaN(a) || double.IsNaN(b))
                return c.Return(false);
            double diff = Math.Abs(a - b);
            double limit = Math.Max(relTol * Math.Max(Math.Abs(a), Math.Abs(b)), absTol);
            return c.Return(diff <= limit);
        });
        Table rngMeta = OpenRngMeta(L);
        L.Register(m, "rng", (ref CallContext c) =>
        {
            long s1 = c.CheckInt(0);
            long s2 = c.OptInt(1, 0);
            var st = new ulong[4];
            SetSeed(st, unchecked((ulong)s1), unchecked((ulong)s2));
            return c.Return(Value.FromUserdata(new LuaRng(rngMeta, st)));
        });
        // Further global built-ins: the same functions as their math.* counterparts.
        foreach (string name in new[] { "abs", "floor", "ceil", "pow", "fmod", "min", "max", "atan2", "hypot", "round", "trunc", "sign" })
            L.Globals.SetStr(name, m.GetStr(name));
        L.Globals.SetStr("math", m);
        L.LoadedModules.SetStr("math", m);
    }

    internal static Table OpenRngMeta(LuaState L)
    {
        Table mt = L.MetaFor("Rng");
        if (!mt.GetStr("__index").IsNil) return mt;
        var methods = new Table();
        L.RegisterInstance<LuaRng>(methods, "float", static (ref CallContext c) =>
        {
            LuaRng r = CheckRng(ref c);
            return c.Return(Value.FromFloat(r.NextFloat()));
        });
        L.RegisterInstance<LuaRng>(methods, "int", static (ref CallContext c) =>
        {
            LuaRng r = CheckRng(ref c);
            long low = c.CheckInt(1), up = c.CheckInt(2);
            if (low > up) throw c.ArgError(1, "interval is empty");
            return c.Return(Value.FromInt(r.NextInt(low, up)));
        });
        L.RegisterInstance<LuaRng>(methods, "normal", static (ref CallContext c) =>
        {
            LuaRng r = CheckRng(ref c);
            double mean = c.OptNumber(1, 0.0), std = c.OptNumber(2, 1.0);
            if (double.IsNaN(std) || std < 0.0) throw c.ArgError(2, "standard deviation must be non-negative");
            return c.Return(Value.FromFloat(r.NextNormal(mean, std)));
        });
        L.RegisterInstance<LuaRng>(methods, "floats", static (ref CallContext c) =>
        {
            LuaRng r = CheckRng(ref c);
            long n = c.CheckInt(1);
            if (n < 0) throw c.ArgError(1, "length must not be negative");
            c.L.CheckArrayLength(n);
            r.CheckMutable();
            var data = new double[(int)n];
            for (int i = 0; i < data.Length; i++) data[i] = r.NextFloat();
            return c.Return(Value.FromUserdata(new FloatArray(c.L, data)));
        });
        L.RegisterInstance<LuaRng>(methods, "normals", static (ref CallContext c) =>
        {
            LuaRng r = CheckRng(ref c);
            long n = c.CheckInt(1);
            if (n < 0) throw c.ArgError(1, "length must not be negative");
            c.L.CheckArrayLength(n);
            double mean = c.OptNumber(2, 0.0), std = c.OptNumber(3, 1.0);
            if (double.IsNaN(std) || std < 0.0) throw c.ArgError(3, "standard deviation must be non-negative");
            r.CheckMutable();
            var data = new double[(int)n];
            for (int i = 0; i < data.Length; i++) data[i] = r.NextNormal(mean, std);
            return c.Return(Value.FromUserdata(new FloatArray(c.L, data)));
        });
        L.RegisterInstance<LuaRng>(methods, "clone", (ref CallContext c) =>
        {
            LuaRng r = CheckRng(ref c);
            return c.Return(Value.FromUserdata(r.Clone(mt)));
        });
        mt.SetStr("__name", "Rng");
        mt.SetStr("__index", Value.FromTable(methods));
        mt.SetStr("__tostring", Value.FromFunction(static (ref CallContext c) => c.Return("Rng"), "Rng.__tostring"));
        mt.IsCameoInstanceMeta = true;
        return mt;
    }

    private static LuaRng CheckRng(ref CallContext c) =>
        c[0].O as LuaRng ?? throw c.TypeError(0, "Rng");

    internal static ulong NextRandInternal(ulong[] s) => NextRand(s);
    internal static ulong ProjectInternal(ulong ran, ulong n, ulong[] s) => Project(ran, n, s);

    // An integral float as an integer when it fits (like math.floor), else as a float.
    private static int FloatToIntResult(ref CallContext c, double d) =>
        LuaNumber.FloatToInteger(d, out long i) ? c.Return(Value.FromInt(i)) : c.Return(Value.FromFloat(d));

    private static ulong NextRand(ulong[] s)
    {
        ulong s0 = s[0], s1 = s[1], s2 = s[2] ^ s0, s3 = s[3] ^ s1;
        ulong res = BitOperations.RotateLeft(s1 * 5, 7) * 9;
        s[0] = s0 ^ s3;
        s[1] = s1 ^ s2;
        s[2] = s2 ^ (s1 << 17);
        s[3] = BitOperations.RotateLeft(s3, 45);
        return res;
    }

    private static void SetSeed(ulong[] s, ulong n1, ulong n2)
    {
        s[0] = n1;
        s[1] = 0xff;
        s[2] = n2;
        s[3] = 0;
        for (int i = 0; i < 16; i++) NextRand(s);
    }

    private static ulong Project(ulong ran, ulong n, ulong[] state)
    {
        if ((n & (n + 1)) == 0) return ran & n;
        ulong lim = n;
        lim |= lim >> 1;
        lim |= lim >> 2;
        lim |= lim >> 4;
        lim |= lim >> 8;
        lim |= lim >> 16;
        lim |= lim >> 32;
        while ((ran &= lim) > n) ran = NextRand(state);
        return ran;
    }

    private static Value FloatToIntOrFloat(double f) =>
        LuaNumber.FloatToInteger(f, out long i) ? Value.FromInt(i) : Value.FromFloat(f);

    private static int Abs(ref CallContext c)
    {
        Value v = c[0];
        if (v.Kind == ValueKind.Integer) return c.Return(Value.FromInt(v.I < 0 ? unchecked(-v.I) : v.I));
        return c.Return(Value.FromFloat(Math.Abs(c.CheckNumber(0))));
    }

    private static int Floor(ref CallContext c)
    {
        Value v = c[0];
        if (v.Kind == ValueKind.Integer) return c.Return(v);
        return c.Return(FloatToIntOrFloat(Math.Floor(c.CheckNumber(0))));
    }

    private static int Ceil(ref CallContext c)
    {
        Value v = c[0];
        if (v.Kind == ValueKind.Integer) return c.Return(v);
        return c.Return(FloatToIntOrFloat(Math.Ceiling(c.CheckNumber(0))));
    }

    private static int Log(ref CallContext c)
    {
        double x = c.CheckNumber(0);
        if (c.IsNoneOrNil(1)) return c.Return(Math.Log(x));
        double b = c.CheckNumber(1);
        if (b == 2.0) return c.Return(Math.Log2(x));
        if (b == 10.0) return c.Return(Math.Log10(x));
        return c.Return(Math.Log(x) / Math.Log(b));
    }

    private static int FMod(ref CallContext c)
    {
        Value a = c[0], b = c[1];
        if (a.Kind == ValueKind.Integer && b.Kind == ValueKind.Integer)
        {
            long d = b.I;
            if ((ulong)d + 1UL <= 1UL)
            {
                // d is 0 or -1
                if (d == 0) throw c.ArgError(1, "zero");
                return c.Return(Value.FromInt(0));
            }
            return c.Return(Value.FromInt(a.I % d));
        }
        return c.Return(Value.FromFloat(c.CheckNumber(0) % c.CheckNumber(1)));
    }

    private static int Modf(ref CallContext c)
    {
        Value v = c[0];
        if (v.Kind == ValueKind.Integer) return c.Return(v, Value.FromFloat(0.0));
        double n = c.CheckNumber(0);
        double ip = n < 0 ? Math.Ceiling(n) : Math.Floor(n);
        double frac = double.IsInfinity(n) ? 0.0 : n - ip;
        return c.Return(FloatToIntOrFloat(ip), Value.FromFloat(frac));
    }

    private static int ToInteger(ref CallContext c)
    {
        Value v = c[0];
        if (v.Kind == ValueKind.Integer) return c.Return(v);
        if (v.Kind == ValueKind.Float && LuaNumber.FloatToInteger(v.N, out long i)) return c.Return(Value.FromInt(i));
        if (v.Kind == ValueKind.String && v.TryToInteger(out long si)) return c.Return(Value.FromInt(si));
        c.CheckAny(0);
        return c.Return(Value.Nil);
    }

    private static int Type(ref CallContext c)
    {
        Value v = c[0];
        if (v.Kind == ValueKind.Integer) return c.Return("integer");
        if (v.Kind == ValueKind.Float) return c.Return("float");
        c.CheckAny(0);
        return c.Return(Value.Nil);
    }

    private static int Max(ref CallContext c)
    {
        if (c.Count < 1) throw c.ArgError(0, "value expected");
        Value best = ToNum(ref c, 0);
        for (int i = 1; i < c.Count; i++)
        {
            Value v = ToNum(ref c, i);
            if (c.L.LessThan(best, v)) best = v;
        }
        return c.Return(best);
    }

    private static int Min(ref CallContext c)
    {
        if (c.Count < 1) throw c.ArgError(0, "value expected");
        Value best = ToNum(ref c, 0);
        for (int i = 1; i < c.Count; i++)
        {
            Value v = ToNum(ref c, i);
            if (c.L.LessThan(v, best)) best = v;
        }
        return c.Return(best);
    }

    private static Value ToNum(ref CallContext c, int i)
    {
        Value v = c[i];
        if (v.IsNumber) return v;
        if (v.IsString && LuaNumber.TryParse(v.AsString, out Value n)) return n;
        throw c.TypeError(i, "number");
    }
}

/// <summary>
/// Explicitly seeded, strictly deterministic xoshiro256** PRNG (<c>math.rng(seed)</c>) with zero
/// global state. Available in all profiles including <c>Hermetic</c>, supports Box-Muller normals,
/// direct <see cref="FloatArray"/> sampling, <see cref="IFreezable"/>, and cross-worker transfer.
/// </summary>
public sealed class LuaRng : IUserdata, IFreezable, IValueTransferable
{
    private readonly ulong[] _state;
    private bool _hasSpare;
    private double _spare;
    internal bool Frozen;

    internal LuaRng(Table metatable, ulong[] state, bool hasSpare = false, double spare = 0.0, bool frozen = false)
    {
        Metatable = metatable;
        _state = state;
        _hasSpare = hasSpare;
        _spare = spare;
        Frozen = frozen;
    }

    public string TypeName => "Rng";
    public Table? Metatable { get; set; }
    public bool IsFrozen => Frozen;

    public void Freeze(HashSet<object> visited) => Frozen = true;

    internal void CheckMutable()
    {
        if (Frozen) throw new LuaException("cannot advance frozen Rng; call clone() first");
    }

    internal double NextFloat()
    {
        CheckMutable();
        ulong rv = MathLib.NextRandInternal(_state);
        return (rv >> 11) * (0.5 / (1UL << 52));
    }

    internal long NextInt(long low, long up)
    {
        CheckMutable();
        ulong rv = MathLib.NextRandInternal(_state);
        ulong r = MathLib.ProjectInternal(rv, unchecked((ulong)up - (ulong)low), _state);
        return unchecked((long)(r + (ulong)low));
    }

    internal double NextNormal(double mean, double std)
    {
        CheckMutable();
        if (_hasSpare)
        {
            _hasSpare = false;
            return mean + std * _spare;
        }
        double u1 = NextFloat();
        if (u1 < 1e-300) u1 = 1e-300;
        double u2 = NextFloat();
        double mag = Math.Sqrt(-2.0 * Math.Log(u1));
        double theta = 2.0 * Math.PI * u2;
        _spare = mag * Math.Sin(theta);
        _hasSpare = true;
        return mean + std * (mag * Math.Cos(theta));
    }

    internal LuaRng Clone(Table metatable) =>
        new(metatable, (ulong[])_state.Clone(), _hasSpare, _spare, frozen: false);

    public Value TransferTo(LuaState target, Func<Value, Value> copyChild) =>
        Value.FromUserdata(new LuaRng(MathLib.OpenRngMeta(target), (ulong[])_state.Clone(), _hasSpare, _spare, Frozen));
}


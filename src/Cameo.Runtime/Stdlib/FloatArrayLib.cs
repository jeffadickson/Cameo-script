using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using Cameo.Runtime.Vm;

namespace Cameo.Runtime.Stdlib;

/// <summary>
/// A fixed-length array of 64-bit floats backed by one .NET array (userdata). A Lua table
/// spends three words per element and goes through the interpreter for every access; this
/// spends eight bytes and does whole-array arithmetic in vectorised C#.
/// </summary>
public sealed class FloatArray : IUserdata, IFreezable
{
    public readonly double[] Data;
    private readonly LuaState _state;
    internal bool Frozen;

    public FloatArray(LuaState state, double[] data, bool frozen = false) { _state = state; Data = data; Frozen = frozen; }
    public string TypeName => "FloatArray";
    public Table? Metatable { get => _state.FloatArrayMeta; set { } }
    public int Length => Data.Length;
    public bool IsFrozen => Frozen;

    public void Freeze(HashSet<object> visited) => Frozen = true;

    internal void CheckMutable()
    {
        if (Frozen) throw new LuaException("cannot mutate frozen FloatArray");
    }
}

/// <summary>
/// <c>FloatArray</c>: numeric arrays for signal data. Indexes are 1-based, the length is fixed,
/// reading outside the array gives nil (so <c>ipairs</c> works) and writing outside it is an
/// error. Operators (<c>+ - * / unary -</c>) work element by element between two arrays of the
/// same length or an array and a number, and return a new array; the <c>...InPlace</c> methods
/// change the array instead and return it.
/// </summary>
public static class FloatArrayLib
{
    public static void Open(LuaState L)
    {
        var lib = new Table();
        L.Register(lib, "new", static (ref CallContext c) =>
        {
            int n = CheckLength(ref c, 0);
            var a = new double[n];
            if (!c.IsNoneOrNil(1)) Array.Fill(a, c.CheckNumber(1));
            return Ret(ref c, a);
        });
        L.Register(lib, "from", static (ref CallContext c) =>
        {
            if (c[0].O is FloatArray src) return Ret(ref c, (double[])src.Data.Clone());
            if (c[0].O is LuaList list)
            {
                int n = list.Items.Count;
                c.L.CheckArrayLength(n);
                var arr = new double[n];
                for (int i = 0; i < n; i++)
                {
                    Value item = list.Items[i];
                    if (!item.TryToDouble(out arr[i]) || item.IsString)
                        throw c.ArgError(0, $"element {i + 1} is a {LuaState.TypeNameOf(item)}, not a number");
                }
                return Ret(ref c, arr);
            }
            Table t = c.CheckTable(0);
            int len = t.Length;
            c.L.CheckArrayLength(len);
            var a = new double[len];
            for (int i = 0; i < len; i++)
            {
                Value item = t.GetInt(i + 1);
                if (!item.TryToDouble(out a[i]) || item.IsString)
                    throw c.ArgError(0, $"element {i + 1} is a {LuaState.TypeNameOf(item)}, not a number");
            }
            return Ret(ref c, a);
        });
        // linspace(first, last, n): n evenly spaced values, both ends included
        L.Register(lib, "linspace", static (ref CallContext c) =>
        {
            double first = c.CheckNumber(0), last = c.CheckNumber(1);
            int n = CheckLength(ref c, 2);
            var a = new double[n];
            double step = n > 1 ? (last - first) / (n - 1) : 0;
            for (int i = 0; i < n; i++) a[i] = first + step * i;
            if (n > 1) a[n - 1] = last; // exactly, whatever the rounding
            return Ret(ref c, a);
        });
        // frombytes(s [, format [, bigEndian]]): the reverse of tobytes
        L.Register(lib, "frombytes", FromBytes);
        // interp(x, xp, fp): 1D piecewise-linear interpolation (clamped endpoints)
        L.Register(lib, "interp", Interp);

        var m = lib; // methods live in the same table: FloatArray.sum(a) and a:sum() are the same
        L.RegisterInstance<FloatArray>(m, "get", static (ref CallContext c) =>
        {
            double[] a = Check(ref c, 0).Data;
            long i = c.CheckInt(1);
            return (ulong)(i - 1) < (ulong)a.Length ? c.Return(a[i - 1]) : c.Return(Value.Nil);
        });
        L.RegisterInstance<FloatArray>(m, "set", static (ref CallContext c) =>
        {
            FloatArray fa = Check(ref c, 0);
            fa.CheckMutable();
            double[] a = fa.Data;
            long i = c.CheckInt(1);
            if ((ulong)(i - 1) >= (ulong)a.Length) throw c.ArgError(1, $"index {i} is outside the array (length {a.Length})");
            a[i - 1] = c.CheckNumber(2);
            return c.Return();
        });
        L.RegisterInstance<FloatArray>(m, "fill", static (ref CallContext c) =>
        {
            FloatArray fa = Check(ref c, 0);
            fa.CheckMutable();
            double[] a = fa.Data;
            double v = c.CheckNumber(1);
            var (from, count) = Range(ref c, 2, a.Length);
            a.AsSpan(from, count).Fill(v);
            return c.Return(c[0]);
        });
        L.RegisterInstance<FloatArray>(m, "copy", static (ref CallContext c) => Ret(ref c, (double[])Check(ref c, 0).Data.Clone()));
        L.RegisterInstance<FloatArray>(m, "slice", static (ref CallContext c) =>
        {
            double[] a = Check(ref c, 0).Data;
            var (from, count) = Range(ref c, 1, a.Length);
            return Ret(ref c, a.AsSpan(from, count).ToArray());
        });
        L.RegisterInstance<FloatArray>(m, "totable", static (ref CallContext c) =>
        {
            double[] a = Check(ref c, 0).Data;
            var t = new Table(a.Length, 0);
            foreach (double d in a) t.Append(d);
            return c.Return(Value.FromTable(t));
        });
        L.RegisterInstance<FloatArray>(m, "toList", static (ref CallContext c) =>
        {
            double[] a = Check(ref c, 0).Data;
            var list = new LuaList(c.L, a.Length);
            for (int i = 0; i < a.Length; i++) list.Items.Add(Value.FromFloat(a[i]));
            return c.Return(Value.FromUserdata(list));
        });
        L.RegisterInstance<FloatArray>(m, "tobytes", ToBytes);

        // element-wise maths, each returning a new array
        L.RegisterInstance<FloatArray>(m, "abs", static (ref CallContext c) => Map(ref c, Math.Abs));
        L.RegisterInstance<FloatArray>(m, "sqrt", static (ref CallContext c) => Map(ref c, Math.Sqrt));
        L.RegisterInstance<FloatArray>(m, "sin", static (ref CallContext c) => Map(ref c, Math.Sin));
        L.RegisterInstance<FloatArray>(m, "cos", static (ref CallContext c) => Map(ref c, Math.Cos));
        L.RegisterInstance<FloatArray>(m, "exp", static (ref CallContext c) => Map(ref c, Math.Exp));
        L.RegisterInstance<FloatArray>(m, "log", static (ref CallContext c) => Map(ref c, Math.Log));
        L.RegisterInstance<FloatArray>(m, "clamp", static (ref CallContext c) =>
        {
            double[] a = Check(ref c, 0).Data;
            double lo = c.CheckNumber(1), hi = c.CheckNumber(2);
            if (!(lo <= hi)) throw c.ArgError(2, "upper bound is below the lower bound");
            var r = new double[a.Length];
            for (int i = 0; i < a.Length; i++) r[i] = Math.Clamp(a[i], lo, hi);
            return Ret(ref c, r);
        });
        // map(a, fn): a new array of fn(value, index)
        L.RegisterInstance<FloatArray>(m, "map", static (ref CallContext c) =>
        {
            double[] a = Check(ref c, 0).Data;
            Value fn = c.CheckFunction(1);
            var r = new double[a.Length];
            for (int i = 0; i < a.Length; i++)
            {
                Value v = c.L.Call1(fn, a[i], i + 1);
                if (v.IsString || !v.TryToDouble(out r[i])) throw new LuaException($"FloatArray.map: the function returned a {LuaState.TypeNameOf(v)} for element {i + 1}, not a number");
            }
            return Ret(ref c, r);
        });

        // reductions
        L.RegisterInstance<FloatArray>(m, "sum", static (ref CallContext c) => c.Return(Sum(Check(ref c, 0).Data)));
        L.RegisterInstance<FloatArray>(m, "mean", static (ref CallContext c) =>
        {
            double[] a = NonEmpty(ref c);
            return c.Return(Sum(a) / a.Length);
        });
        L.RegisterInstance<FloatArray>(m, "min", static (ref CallContext c) => Extreme(ref c, min: true));
        L.RegisterInstance<FloatArray>(m, "max", static (ref CallContext c) => Extreme(ref c, min: false));
        L.RegisterInstance<FloatArray>(m, "rms", static (ref CallContext c) =>
        {
            double[] a = NonEmpty(ref c);
            return c.Return(Math.Sqrt(Dot(a, a) / a.Length));
        });
        // variance(a): population variance (divides by n)
        L.RegisterInstance<FloatArray>(m, "variance", static (ref CallContext c) =>
        {
            double[] a = NonEmpty(ref c);
            double mean = Sum(a) / a.Length, acc = 0;
            foreach (double d in a) acc += (d - mean) * (d - mean);
            return c.Return(acc / a.Length);
        });
        L.RegisterInstance<FloatArray>(m, "dot", static (ref CallContext c) =>
        {
            double[] a = Check(ref c, 0).Data, b = Check(ref c, 1).Data;
            if (a.Length != b.Length) throw c.ArgError(1, $"arrays have different lengths ({a.Length} and {b.Length})");
            return c.Return(Dot(a, b));
        });
        L.RegisterInstance<FloatArray>(m, "std", static (ref CallContext c) =>
        {
            double[] a = NonEmpty(ref c);
            bool sample = !c.IsNoneOrNil(1) && c[1].IsTruthy;
            if (sample && a.Length < 2) throw c.ArgError(0, "sample std requires at least 2 elements");
            double mean = Sum(a) / a.Length, acc = 0;
            foreach (double d in a) acc += (d - mean) * (d - mean);
            return c.Return(Math.Sqrt(acc / (sample ? a.Length - 1 : a.Length)));
        });
        L.RegisterInstance<FloatArray>(m, "median", static (ref CallContext c) => c.Return(ComputeQuantile(NonEmpty(ref c), 0.5)));
        L.RegisterInstance<FloatArray>(m, "quantile", static (ref CallContext c) =>
        {
            double[] a = NonEmpty(ref c);
            double p = c.CheckNumber(1);
            if (double.IsNaN(p) || p < 0.0 || p > 1.0) throw c.ArgError(1, "quantile probability must be in [0, 1]");
            return c.Return(ComputeQuantile(a, p));
        });
        L.RegisterInstance<FloatArray>(m, "cumsum", static (ref CallContext c) =>
        {
            double[] a = Check(ref c, 0).Data;
            var r = new double[a.Length];
            double acc = 0;
            for (int i = 0; i < a.Length; i++) { acc += a[i]; r[i] = acc; }
            return Ret(ref c, r);
        });
        L.RegisterInstance<FloatArray>(m, "diff", static (ref CallContext c) =>
        {
            double[] a = Check(ref c, 0).Data;
            if (a.Length <= 1) return Ret(ref c, Array.Empty<double>());
            var r = new double[a.Length - 1];
            for (int i = 0; i < r.Length; i++) r[i] = a[i + 1] - a[i];
            return Ret(ref c, r);
        });

        // in-place arithmetic: changes the array and returns it
        L.RegisterInstance<FloatArray>(m, "addInPlace", static (ref CallContext c) => InPlace(ref c, Op.Add));
        L.RegisterInstance<FloatArray>(m, "subInPlace", static (ref CallContext c) => InPlace(ref c, Op.Sub));
        L.RegisterInstance<FloatArray>(m, "mulInPlace", static (ref CallContext c) => InPlace(ref c, Op.Mul));
        L.RegisterInstance<FloatArray>(m, "divInPlace", static (ref CallContext c) => InPlace(ref c, Op.Div));

        Table mt = L.FloatArrayMeta;
        mt.SetStr("__name", "FloatArray");
        // a[i] and a.method: the VM answers integer keys itself (LuaState.Index); this serves the rest
        L.Register(mt, "__index", (ref CallContext c) =>
        {
            double[] a = Check(ref c, 0).Data;
            Value k = c[1];
            if (k.TryToInteger(out long i) && !k.IsString) return (ulong)(i - 1) < (ulong)a.Length ? c.Return(a[i - 1]) : c.Return(Value.Nil);
            return c.Return(lib.Get(k));
        });
        L.Register(mt, "__newindex", static (ref CallContext c) =>
        {
            FloatArray fa = Check(ref c, 0);
            fa.CheckMutable();
            double[] a = fa.Data;
            Value k = c[1];
            if (k.IsString || !k.TryToInteger(out long i)) throw new LuaException($"attempt to set field '{k}' of a FloatArray");
            if ((ulong)(i - 1) >= (ulong)a.Length) throw new LuaException($"index {i} is outside the FloatArray (length {a.Length})");
            if (c[2].IsString || !c[2].TryToDouble(out a[i - 1])) throw new LuaException($"a FloatArray holds numbers, not a {LuaState.TypeNameOf(c[2])}");
            return c.Return();
        });
        L.Register(mt, "__len", static (ref CallContext c) => c.Return(Check(ref c, 0).Data.Length));
        L.Register(mt, "__add", static (ref CallContext c) => Binary(ref c, Op.Add));
        L.Register(mt, "__sub", static (ref CallContext c) => Binary(ref c, Op.Sub));
        L.Register(mt, "__mul", static (ref CallContext c) => Binary(ref c, Op.Mul));
        L.Register(mt, "__div", static (ref CallContext c) => Binary(ref c, Op.Div));
        L.Register(mt, "__unm", static (ref CallContext c) =>
        {
            double[] a = Check(ref c, 0).Data;
            var r = new double[a.Length];
            Apply(Op.Mul, a, -1.0, r);
            return Ret(ref c, r);
        });
        // == compares contents, element by element with the rules of numbers (so an array
        // holding nan is not equal to a copy of itself, and -0.0 equals 0.0)
        L.Register(mt, "__eq", static (ref CallContext c) =>
        {
            if (c[0].O is not FloatArray x || c[1].O is not FloatArray y || x.Length != y.Length) return c.Return(false);
            double[] a = x.Data, b = y.Data;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return c.Return(false);
            return c.Return(true);
        });
        L.Register(mt, "__tostring", static (ref CallContext c) =>
        {
            double[] a = Check(ref c, 0).Data;
            var shown = a.Take(8).Select(d => Value.FromFloat(d).ToString());
            return c.Return($"FloatArray({a.Length}){{{string.Join(", ", shown)}{(a.Length > 8 ? ", ..." : "")}}}");
        });

        L.Globals.SetStr("FloatArray", lib);
        L.LoadedModules.SetStr("FloatArray", lib);
    }

    // ------------------------------------------------------------------ helpers

    private static FloatArray Check(ref CallContext c, int i) => c[i].O as FloatArray ?? throw c.TypeError(i, "FloatArray");

    private static int Ret(ref CallContext c, double[] data) => c.Return(Value.FromUserdata(new FloatArray(c.L, data)));

    private static int CheckLength(ref CallContext c, int arg)
    {
        long n = c.CheckInt(arg);
        if (n < 0) throw c.ArgError(arg, "length must not be negative");
        c.L.CheckArrayLength(n);
        return (int)n;
    }

    private static double[] NonEmpty(ref CallContext c)
    {
        double[] a = Check(ref c, 0).Data;
        if (a.Length == 0) throw c.ArgError(0, "array is empty");
        return a;
    }

    // Optional (i, j) arguments with string.sub's rules -> 0-based start and count.
    private static (int From, int Count) Range(ref CallContext c, int arg, int n)
    {
        long i = c.OptInt(arg, 1), j = c.OptInt(arg + 1, -1);
        if (i < 0) i = Math.Max(n + i + 1, 1); else if (i == 0) i = 1;
        if (j < 0) j = n + j + 1; else if (j > n) j = n;
        return i > j ? (0, 0) : ((int)i - 1, (int)(j - i + 1));
    }

    private static int Map(ref CallContext c, Func<double, double> f)
    {
        double[] a = Check(ref c, 0).Data;
        var r = new double[a.Length];
        for (int i = 0; i < a.Length; i++) r[i] = f(a[i]);
        return Ret(ref c, r);
    }

    private static int Extreme(ref CallContext c, bool min)
    {
        double[] a = NonEmpty(ref c);
        int at = 0;
        for (int i = 1; i < a.Length; i++)
            if (min ? a[i] < a[at] : a[i] > a[at]) at = i;
        return c.Return(a[at], at + 1); // the value and where it first occurs
    }

    // ------------------------------------------------------------------ arithmetic

    private enum Op { Add, Sub, Mul, Div }

    // Element-wise a op b with Vector<double> (the JIT turns this into AVX/NEON), then a scalar tail.
    private static void Apply(Op op, ReadOnlySpan<double> a, ReadOnlySpan<double> b, Span<double> r)
    {
        int i = 0, w = Vector<double>.Count;
        if (Vector.IsHardwareAccelerated)
        {
            for (; i <= a.Length - w; i += w)
            {
                var x = new Vector<double>(a.Slice(i));
                var y = new Vector<double>(b.Slice(i));
                (op switch { Op.Add => x + y, Op.Sub => x - y, Op.Mul => x * y, _ => x / y }).CopyTo(r.Slice(i));
            }
        }
        for (; i < a.Length; i++)
            r[i] = op switch { Op.Add => a[i] + b[i], Op.Sub => a[i] - b[i], Op.Mul => a[i] * b[i], _ => a[i] / b[i] };
    }

    private static void Apply(Op op, ReadOnlySpan<double> a, double k, Span<double> r)
    {
        int i = 0, w = Vector<double>.Count;
        if (Vector.IsHardwareAccelerated)
        {
            var y = new Vector<double>(k);
            for (; i <= a.Length - w; i += w)
            {
                var x = new Vector<double>(a.Slice(i));
                (op switch { Op.Add => x + y, Op.Sub => x - y, Op.Mul => x * y, _ => x / y }).CopyTo(r.Slice(i));
            }
        }
        for (; i < a.Length; i++)
            r[i] = op switch { Op.Add => a[i] + k, Op.Sub => a[i] - k, Op.Mul => a[i] * k, _ => a[i] / k };
    }

    // number op array, for the two operators that do not commute
    private static void ApplyReversed(Op op, double k, ReadOnlySpan<double> a, Span<double> r)
    {
        for (int i = 0; i < a.Length; i++) r[i] = op == Op.Sub ? k - a[i] : k / a[i];
    }

    private static double Sum(ReadOnlySpan<double> a)
    {
        // plain left-to-right addition: the same answer as a Lua loop gives, on every machine
        double s = 0;
        foreach (double d in a) s += d;
        return s;
    }

    private static double Dot(ReadOnlySpan<double> a, ReadOnlySpan<double> b)
    {
        double s = 0;
        for (int i = 0; i < a.Length; i++) s += a[i] * b[i];
        return s;
    }

    private static bool Number(in Value v, out double d)
    {
        d = 0;
        return !v.IsString && v.TryToDouble(out d);
    }

    private static int Binary(ref CallContext c, Op op)
    {
        Value x = c[0], y = c[1];
        if (x.O is FloatArray a && y.O is FloatArray b)
        {
            if (a.Length != b.Length) throw new LuaException($"FloatArray arithmetic on different lengths ({a.Length} and {b.Length})");
            var r = new double[a.Length];
            Apply(op, a.Data, b.Data, r);
            return Ret(ref c, r);
        }
        if (x.O is FloatArray left && Number(y, out double k))
        {
            var r = new double[left.Length];
            Apply(op, left.Data, k, r);
            return Ret(ref c, r);
        }
        if (y.O is FloatArray right && Number(x, out k))
        {
            var r = new double[right.Length];
            if (op is Op.Add or Op.Mul) Apply(op, right.Data, k, r);
            else ApplyReversed(op, k, right.Data, r);
            return Ret(ref c, r);
        }
        Value other = x.O is FloatArray ? y : x;
        throw new LuaException($"attempt to perform arithmetic on a FloatArray and a {LuaState.TypeNameOf(other)} value");
    }

    private static int InPlace(ref CallContext c, Op op)
    {
        FloatArray fa = Check(ref c, 0);
        fa.CheckMutable();
        double[] a = fa.Data;
        if (c[1].O is FloatArray b)
        {
            if (a.Length != b.Length) throw c.ArgError(1, $"arrays have different lengths ({a.Length} and {b.Length})");
            Apply(op, a, b.Data, a);
        }
        else Apply(op, a, c.CheckNumber(1), a);
        return c.Return(c[0]);
    }

    private static double ComputeQuantile(double[] a, double p)
    {
        for (int i = 0; i < a.Length; i++)
            if (double.IsNaN(a[i])) return double.NaN;
        var sorted = (double[])a.Clone();
        Array.Sort(sorted);
        if (sorted.Length == 1) return sorted[0];
        double idx = p * (sorted.Length - 1);
        int lo = (int)Math.Floor(idx);
        int hi = (int)Math.Ceiling(idx);
        if (lo == hi) return sorted[lo];
        double frac = idx - lo;
        return sorted[lo] + frac * (sorted[hi] - sorted[lo]);
    }

    private static int Interp(ref CallContext c)
    {
        double[] xp = Check(ref c, 1).Data;
        double[] fp = Check(ref c, 2).Data;
        if (xp.Length == 0) throw c.ArgError(1, "xp must not be empty");
        if (xp.Length != fp.Length) throw c.ArgError(2, $"xp and fp have different lengths ({xp.Length} and {fp.Length})");
        for (int i = 0; i < xp.Length; i++)
        {
            if (double.IsNaN(xp[i])) throw c.ArgError(1, "xp must not contain NaN");
            if (i > 0 && xp[i] < xp[i - 1]) throw c.ArgError(1, "xp must be sorted in non-decreasing order");
        }

        if (c[0].O is FloatArray xs)
        {
            var r = new double[xs.Data.Length];
            for (int i = 0; i < xs.Data.Length; i++)
                r[i] = InterpScalar(xs.Data[i], xp, fp);
            return Ret(ref c, r);
        }
        return c.Return(InterpScalar(c.CheckNumber(0), xp, fp));
    }

    private static double InterpScalar(double x, double[] xp, double[] fp)
    {
        if (double.IsNaN(x)) return double.NaN;
        if (x <= xp[0]) return fp[0];
        if (x >= xp[^1]) return fp[^1];
        int lo = 0, hi = xp.Length - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) >>> 1;
            if (xp[mid] <= x) lo = mid;
            else hi = mid;
        }
        double dx = xp[hi] - xp[lo];
        if (dx == 0) return fp[lo];
        double t = (x - xp[lo]) / dx;
        return fp[lo] + t * (fp[hi] - fp[lo]);
    }

    // ------------------------------------------------------------------ bytes

    private static (int Size, string Name) Format(ref CallContext c, int arg)
    {
        string f = c.OptString(arg, "f64");
        return f switch
        {
            "f64" => (8, f),
            "f32" => (4, f),
            "i32" => (4, f),
            "i16" => (2, f),
            "u8" => (1, f),
            _ => throw c.ArgError(arg, "format must be 'f64', 'f32', 'i32', 'i16' or 'u8'"),
        };
    }

    // tobytes(a [, format [, bigEndian]]): the samples as packed binary (little-endian unless
    // asked otherwise). Integer formats round to nearest (halves away from zero) and saturate at the type's range, as
    // an instrument's converter does; nan becomes 0.
    private static int ToBytes(ref CallContext c)
    {
        double[] a = Check(ref c, 0).Data;
        var (size, format) = Format(ref c, 1);
        bool big = c[2].IsTruthy;
        c.L.CheckStringLength((long)a.Length * size);
        var bytes = new byte[a.Length * size];
        Span<byte> span = bytes;
        if (format == "f64" && !big && BitConverter.IsLittleEndian)
            MemoryMarshal.AsBytes(a.AsSpan()).CopyTo(span);
        else
        {
            for (int i = 0; i < a.Length; i++)
            {
                Span<byte> at = span.Slice(i * size, size);
                double d = a[i];
                switch (format)
                {
                    case "f64": if (big) BinaryPrimitives.WriteDoubleBigEndian(at, d); else BinaryPrimitives.WriteDoubleLittleEndian(at, d); break;
                    case "f32": if (big) BinaryPrimitives.WriteSingleBigEndian(at, (float)d); else BinaryPrimitives.WriteSingleLittleEndian(at, (float)d); break;
                    case "i32":
                        {
                            int v = double.IsNaN(d) ? 0 : (int)Math.Clamp(Math.Round(d, MidpointRounding.AwayFromZero), int.MinValue, int.MaxValue);
                            if (big) BinaryPrimitives.WriteInt32BigEndian(at, v); else BinaryPrimitives.WriteInt32LittleEndian(at, v);
                            break;
                        }
                    case "i16":
                        {
                            short v = double.IsNaN(d) ? (short)0 : (short)Math.Clamp(Math.Round(d, MidpointRounding.AwayFromZero), short.MinValue, short.MaxValue);
                            if (big) BinaryPrimitives.WriteInt16BigEndian(at, v); else BinaryPrimitives.WriteInt16LittleEndian(at, v);
                            break;
                        }
                    default:
                        at[0] = double.IsNaN(d) ? (byte)0 : (byte)Math.Clamp(Math.Round(d, MidpointRounding.AwayFromZero), 0, 255);
                        break;
                }
            }
        }
        return c.Return(LuaText.FromBytes(bytes));
    }

    private static int FromBytes(ref CallContext c)
    {
        byte[] bytes = LuaText.ToBytes(c.CheckString(0));
        var (size, format) = Format(ref c, 1);
        bool big = c[2].IsTruthy;
        if (bytes.Length % size != 0) throw c.ArgError(0, $"length {bytes.Length} is not a multiple of {size} bytes ('{format}')");
        int n = bytes.Length / size;
        c.L.CheckArrayLength(n);
        var a = new double[n];
        ReadOnlySpan<byte> span = bytes;
        for (int i = 0; i < n; i++)
        {
            ReadOnlySpan<byte> at = span.Slice(i * size, size);
            a[i] = format switch
            {
                "f64" => big ? BinaryPrimitives.ReadDoubleBigEndian(at) : BinaryPrimitives.ReadDoubleLittleEndian(at),
                "f32" => big ? BinaryPrimitives.ReadSingleBigEndian(at) : BinaryPrimitives.ReadSingleLittleEndian(at),
                "i32" => big ? BinaryPrimitives.ReadInt32BigEndian(at) : BinaryPrimitives.ReadInt32LittleEndian(at),
                "i16" => big ? BinaryPrimitives.ReadInt16BigEndian(at) : BinaryPrimitives.ReadInt16LittleEndian(at),
                _ => at[0],
            };
        }
        return Ret(ref c, a);
    }
}

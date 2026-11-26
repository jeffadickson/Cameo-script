using Cameo.Runtime;
using System.Globalization;
using System.Numerics;
using SharpSafeNumerics;
using Cameo.Runtime.Vm;
using Cameo.Runtime.Stdlib;

namespace Cameo.SafeNumerics;

/// <summary>
/// <c>safenum</c>: deterministic overflow-checked integer arithmetic, SIMD batch addition,
/// Q32.32 <see cref="Fixed64"/>, and exact IEEE 754 <see cref="Decimal64"/> / <see cref="Decimal128"/>
/// backed by SharpSafeNumerics.
/// Safe in default, Sandboxed, and Hermetic modes; all values are immutable and transferable across workers.
/// </summary>
public static class SafeNumericsLibrary
{
    private const string MetaFixed64 = "safenum.Fixed64";
    private const string MetaDec64 = "safenum.Decimal64";
    private const string MetaDec128 = "safenum.Decimal128";
    private const double FixedScale = 4294967296.0; // 2^32

    /// <summary>Enables <c>safenum</c> on <paramref name="engine"/> and all child workers.</summary>
    public static void Enable(ScriptEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        engine.State.RegisterExtension(Open);
    }

    /// <summary>Opens <c>safenum</c> on <paramref name="L"/>.</summary>
    public static void Open(LuaState L)
    {
        ArgumentNullException.ThrowIfNull(L);
        CopyMeta(BuildFixedMeta(L), L.MetaFor(MetaFixed64));
        CopyMeta(BuildDec64Meta(L), L.MetaFor(MetaDec64));
        CopyMeta(BuildDec128Meta(L), L.MetaFor(MetaDec128));

        var lib = new Table();

        // ---- SafeInteger (64-bit) ----
        L.Register(lib, "intAdd", static (ref CallContext c) =>
        {
            long a = c.CheckInt(0), b = c.CheckInt(1);
            ArithmeticPolicy pol = ParsePolicy(ref c, 2);
            return ReturnIntResult(ref c, RunChecked(() => SafeInteger.Add(a, b, pol)));
        });
        L.Register(lib, "intSub", static (ref CallContext c) =>
        {
            long a = c.CheckInt(0), b = c.CheckInt(1);
            ArithmeticPolicy pol = ParsePolicy(ref c, 2);
            return ReturnIntResult(ref c, RunChecked(() => SafeInteger.Subtract(a, b, pol)));
        });
        L.Register(lib, "intMul", static (ref CallContext c) =>
        {
            long a = c.CheckInt(0), b = c.CheckInt(1);
            ArithmeticPolicy pol = ParsePolicy(ref c, 2);
            return ReturnIntResult(ref c, RunChecked(() => SafeInteger.Multiply(a, b, pol)));
        });
        L.Register(lib, "intDiv", static (ref CallContext c) =>
        {
            long a = c.CheckInt(0), b = c.CheckInt(1);
            ArithmeticError err = SafeInteger.TryDivide(a, b, out long val);
            return err == ArithmeticError.None
                ? c.Return(Value.FromInt(val), Value.FromText("none"))
                : c.Return(Value.Nil, Value.FromText(ErrorName(err)));
        });
        L.Register(lib, "intNeg", static (ref CallContext c) =>
        {
            long a = c.CheckInt(0);
            ArithmeticError err = SafeInteger.TryNegate(a, out long val);
            return err == ArithmeticError.None
                ? c.Return(Value.FromInt(val), Value.FromText("none"))
                : c.Return(Value.Nil, Value.FromText(ErrorName(err)));
        });
        L.Register(lib, "intInRange", static (ref CallContext c) =>
        {
            long x = c.CheckInt(0), min = c.CheckInt(1), max = c.CheckInt(2);
            ArithmeticError err = SafeInteger.TryInRange(x, min, max, out long val);
            return err == ArithmeticError.None
                ? c.Return(Value.FromInt(val), Value.FromText("none"))
                : c.Return(Value.Nil, Value.FromText(ErrorName(err)));
        });
        L.Register(lib, "batchVectorWidth", static (ref CallContext c) =>
            c.Return(Value.FromInt(SafeInteger.BatchAddVectorWidth)));
        L.Register(lib, "intAddBatch", static (ref CallContext c) =>
        {
            int[] left = ExtractInt32s(ref c, 0);
            int[] right = ExtractInt32s(ref c, 1);
            if (left.Length != right.Length)
                throw new LuaException($"safenum.intAddBatch: length mismatch ({left.Length} vs {right.Length})");
            var values = new int[left.Length];
            var errors = new ArithmeticError[left.Length];
            BatchArithmeticResult br = SafeInteger.AddBatch(left, right, values, errors);
            if (br.Error != BatchArithmeticError.None)
                throw new LuaException($"safenum.intAddBatch: {br.Error}");
            var valList = new LuaList(c.L, values.Length);
            var errList = new LuaList(c.L, errors.Length);
            for (int i = 0; i < values.Length; i++)
            {
                valList.Items.Add(Value.FromInt(values[i]));
                errList.Items.Add(Value.FromText(ErrorName(errors[i])));
            }
            var res = new Table(0, 3);
            res.SetStr("values", Value.FromUserdata(valList));
            res.SetStr("errors", Value.FromUserdata(errList));
            res.SetStr("vectorWidth", Value.FromInt(br.VectorWidthUsed));
            return c.Return(Value.FromTable(res));
        });

        // ---- Fixed64 (Q32.32) ----
        L.Register(lib, "fixedFromInt", static (ref CallContext c) =>
            ReturnFixedResult(ref c, SafeFixed.FromInt64(c.CheckInt(0))));
        L.Register(lib, "fixedFromRatio", static (ref CallContext c) =>
        {
            long num = c.CheckInt(0), den = c.CheckInt(1);
            FixedRounding rnd = ParseFixedRounding(ref c, 2);
            return ReturnFixedResult(ref c, SafeFixed.FromRatio(num, den, rnd));
        });
        L.Register(lib, "fixedFromRaw", static (ref CallContext c) =>
            c.Return(Wrap(c.L, Fixed64.FromRaw(c.CheckInt(0)))));

        // ---- IEEE Decimal64 & Decimal128 ----
        L.Register(lib, "dec64", static (ref CallContext c) =>
            c.Return(Wrap(c.L, ParseDecimal64(ref c, 0))));
        L.Register(lib, "dec128", static (ref CallContext c) =>
            c.Return(Wrap(c.L, ParseDecimal128(ref c, 0))));

        L.Globals.SetStr("safenum", lib);
        L.LoadedModules.SetStr("safenum", lib);
    }

    // ------------------------------------------------------------------ userdata wrappers

    private sealed class LuaFixed64 : IUserdata, IFreezable, IValueTransferable
    {
        public readonly Fixed64 Value;
        public Table? Metatable { get; set; }
        public string TypeName => MetaFixed64;
        public LuaFixed64(Fixed64 v, Table mt) { Value = v; Metatable = mt; }
        public bool IsFrozen => true;
        public void Freeze(HashSet<object> visited) { }
        public Value TransferTo(LuaState target, Func<Value, Value> copyChild) => Wrap(target, Value);
        public override string ToString() => (Value.RawValue / FixedScale).ToString("G17", CultureInfo.InvariantCulture);
    }

    private sealed class LuaDec64 : IUserdata, IFreezable, IValueTransferable
    {
        public readonly Decimal64 Value;
        public Table? Metatable { get; set; }
        public string TypeName => MetaDec64;
        public LuaDec64(Decimal64 v, Table mt) { Value = v; Metatable = mt; }
        public bool IsFrozen => true;
        public void Freeze(HashSet<object> visited) { }
        public Value TransferTo(LuaState target, Func<Value, Value> copyChild) => Wrap(target, Value);
        public override string ToString() => Value.ToString("G", CultureInfo.InvariantCulture);
    }

    private sealed class LuaDec128 : IUserdata, IFreezable, IValueTransferable
    {
        public readonly Decimal128 Value;
        public Table? Metatable { get; set; }
        public string TypeName => MetaDec128;
        public LuaDec128(Decimal128 v, Table mt) { Value = v; Metatable = mt; }
        public bool IsFrozen => true;
        public void Freeze(HashSet<object> visited) { }
        public Value TransferTo(LuaState target, Func<Value, Value> copyChild) => Wrap(target, Value);
        public override string ToString() => Value.ToString("G", CultureInfo.InvariantCulture);
    }

    // ------------------------------------------------------------------ metatables

    private static Table BuildFixedMeta(LuaState L)
    {
        var methods = new Table();
        L.RegisterInstance<LuaFixed64>(methods, "raw", static (ref CallContext c) =>
            c.Return(Value.FromInt(CheckFixed(ref c, 0).Value.RawValue)));
        L.RegisterInstance<LuaFixed64>(methods, "toNumber", static (ref CallContext c) =>
            c.Return(Value.FromFloat(CheckFixed(ref c, 0).Value.RawValue / FixedScale)));
        L.RegisterInstance<LuaFixed64>(methods, "add", static (ref CallContext c) =>
            c.Return(Wrap(c.L, UnwrapFixed(SafeFixed.Add(CheckFixed(ref c, 0).Value, CheckFixed(ref c, 1).Value), "add"))));
        L.RegisterInstance<LuaFixed64>(methods, "sub", static (ref CallContext c) =>
            c.Return(Wrap(c.L, UnwrapFixed(SafeFixed.Subtract(CheckFixed(ref c, 0).Value, CheckFixed(ref c, 1).Value), "sub"))));
        L.RegisterInstance<LuaFixed64>(methods, "mul", static (ref CallContext c) =>
            c.Return(Wrap(c.L, UnwrapFixed(SafeFixed.Multiply(CheckFixed(ref c, 0).Value, CheckFixed(ref c, 1).Value, ParseFixedRounding(ref c, 2)), "mul"))));
        L.RegisterInstance<LuaFixed64>(methods, "div", static (ref CallContext c) =>
            c.Return(Wrap(c.L, UnwrapFixed(SafeFixed.Divide(CheckFixed(ref c, 0).Value, CheckFixed(ref c, 1).Value, ParseFixedRounding(ref c, 2)), "div"))));
        L.RegisterInstance<LuaFixed64>(methods, "neg", static (ref CallContext c) =>
            c.Return(Wrap(c.L, UnwrapFixed(SafeFixed.Negate(CheckFixed(ref c, 0).Value), "neg"))));
        L.RegisterInstance<LuaFixed64>(methods, "tryAdd", static (ref CallContext c) =>
            ReturnFixedResult(ref c, SafeFixed.Add(CheckFixed(ref c, 0).Value, CheckFixed(ref c, 1).Value)));
        L.RegisterInstance<LuaFixed64>(methods, "trySub", static (ref CallContext c) =>
            ReturnFixedResult(ref c, SafeFixed.Subtract(CheckFixed(ref c, 0).Value, CheckFixed(ref c, 1).Value)));
        L.RegisterInstance<LuaFixed64>(methods, "tryMul", static (ref CallContext c) =>
            ReturnFixedResult(ref c, SafeFixed.Multiply(CheckFixed(ref c, 0).Value, CheckFixed(ref c, 1).Value, ParseFixedRounding(ref c, 2))));
        L.RegisterInstance<LuaFixed64>(methods, "tryDiv", static (ref CallContext c) =>
            ReturnFixedResult(ref c, SafeFixed.Divide(CheckFixed(ref c, 0).Value, CheckFixed(ref c, 1).Value, ParseFixedRounding(ref c, 2))));
        L.RegisterInstance<LuaFixed64>(methods, "tryNeg", static (ref CallContext c) =>
            ReturnFixedResult(ref c, SafeFixed.Negate(CheckFixed(ref c, 0).Value)));

        var mt = new Table();
        mt.SetStr("__name", MetaFixed64);
        mt.SetStr("__index", methods);
        L.Register(mt, "__add", static (ref CallContext c) =>
            c.Return(Wrap(c.L, UnwrapFixed(SafeFixed.Add(CheckFixed(ref c, 0).Value, CheckFixed(ref c, 1).Value), "+"))));
        L.Register(mt, "__sub", static (ref CallContext c) =>
            c.Return(Wrap(c.L, UnwrapFixed(SafeFixed.Subtract(CheckFixed(ref c, 0).Value, CheckFixed(ref c, 1).Value), "-"))));
        L.Register(mt, "__mul", static (ref CallContext c) =>
            c.Return(Wrap(c.L, UnwrapFixed(SafeFixed.Multiply(CheckFixed(ref c, 0).Value, CheckFixed(ref c, 1).Value, FixedRounding.Exact), "*"))));
        L.Register(mt, "__div", static (ref CallContext c) =>
            c.Return(Wrap(c.L, UnwrapFixed(SafeFixed.Divide(CheckFixed(ref c, 0).Value, CheckFixed(ref c, 1).Value, FixedRounding.Exact), "/"))));
        L.Register(mt, "__unm", static (ref CallContext c) =>
            c.Return(Wrap(c.L, UnwrapFixed(SafeFixed.Negate(CheckFixed(ref c, 0).Value), "-"))));
        L.Register(mt, "__lt", static (ref CallContext c) =>
            c.Return(CheckFixed(ref c, 0).Value.RawValue < CheckFixed(ref c, 1).Value.RawValue));
        L.Register(mt, "__le", static (ref CallContext c) =>
            c.Return(CheckFixed(ref c, 0).Value.RawValue <= CheckFixed(ref c, 1).Value.RawValue));
        L.Register(mt, "__eq", static (ref CallContext c) =>
            c.Return(c[0].O is LuaFixed64 a && c[1].O is LuaFixed64 b && a.Value.Equals(b.Value)));
        L.Register(mt, "__tostring", static (ref CallContext c) =>
            c.Return(Value.FromText(CheckFixed(ref c, 0).ToString())));
        return mt;
    }

    private static Table BuildDec64Meta(LuaState L)
    {
        var methods = new Table();
        L.RegisterInstance<LuaDec64>(methods, "toNumber", static (ref CallContext c) =>
            c.Return(Value.FromFloat(double.Parse(CheckDec64(ref c, 0).Value.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture))));
        L.RegisterInstance<LuaDec64>(methods, "toDec128", static (ref CallContext c) =>
            c.Return(Wrap(c.L, UnwrapDec(SafeDecimal.TryConvert<Decimal64, Decimal128>(CheckDec64(ref c, 0).Value), "toDec128"))));
        L.RegisterInstance<LuaDec64>(methods, "add", static (ref CallContext c) =>
            c.Return(Wrap(c.L, UnwrapDec(SafeDecimal.TryAdd(CheckDec64(ref c, 0).Value, CheckDec64(ref c, 1).Value, ParseDecRounding(ref c, 2)), "add"))));
        L.RegisterInstance<LuaDec64>(methods, "sub", static (ref CallContext c) =>
            c.Return(Wrap(c.L, UnwrapDec(SafeDecimal.TrySubtract(CheckDec64(ref c, 0).Value, CheckDec64(ref c, 1).Value, ParseDecRounding(ref c, 2)), "sub"))));
        L.RegisterInstance<LuaDec64>(methods, "mul", static (ref CallContext c) =>
            c.Return(Wrap(c.L, UnwrapDec(SafeDecimal.TryMultiply(CheckDec64(ref c, 0).Value, CheckDec64(ref c, 1).Value, ParseDecRounding(ref c, 2)), "mul"))));
        L.RegisterInstance<LuaDec64>(methods, "div", static (ref CallContext c) =>
            c.Return(Wrap(c.L, UnwrapDec(SafeDecimal.TryDivide(CheckDec64(ref c, 0).Value, CheckDec64(ref c, 1).Value, ParseDecRounding(ref c, 2)), "div"))));
        L.RegisterInstance<LuaDec64>(methods, "neg", static (ref CallContext c) =>
            c.Return(Wrap(c.L, UnwrapDec(SafeDecimal.TryNegate(CheckDec64(ref c, 0).Value), "neg"))));
        L.RegisterInstance<LuaDec64>(methods, "tryAdd", static (ref CallContext c) =>
            ReturnDec64Result(ref c, SafeDecimal.TryAdd(CheckDec64(ref c, 0).Value, CheckDec64(ref c, 1).Value, ParseDecRounding(ref c, 2))));
        L.RegisterInstance<LuaDec64>(methods, "trySub", static (ref CallContext c) =>
            ReturnDec64Result(ref c, SafeDecimal.TrySubtract(CheckDec64(ref c, 0).Value, CheckDec64(ref c, 1).Value, ParseDecRounding(ref c, 2))));
        L.RegisterInstance<LuaDec64>(methods, "tryMul", static (ref CallContext c) =>
            ReturnDec64Result(ref c, SafeDecimal.TryMultiply(CheckDec64(ref c, 0).Value, CheckDec64(ref c, 1).Value, ParseDecRounding(ref c, 2))));
        L.RegisterInstance<LuaDec64>(methods, "tryDiv", static (ref CallContext c) =>
            ReturnDec64Result(ref c, SafeDecimal.TryDivide(CheckDec64(ref c, 0).Value, CheckDec64(ref c, 1).Value, ParseDecRounding(ref c, 2))));
        L.RegisterInstance<LuaDec64>(methods, "tryNeg", static (ref CallContext c) =>
            ReturnDec64Result(ref c, SafeDecimal.TryNegate(CheckDec64(ref c, 0).Value)));

        var mt = new Table();
        mt.SetStr("__name", MetaDec64);
        mt.SetStr("__index", methods);
        L.Register(mt, "__add", static (ref CallContext c) =>
            c.Return(Wrap(c.L, UnwrapDec(SafeDecimal.TryAdd(CheckDec64(ref c, 0).Value, CheckDec64(ref c, 1).Value, DecimalRounding.Exact), "+"))));
        L.Register(mt, "__sub", static (ref CallContext c) =>
            c.Return(Wrap(c.L, UnwrapDec(SafeDecimal.TrySubtract(CheckDec64(ref c, 0).Value, CheckDec64(ref c, 1).Value, DecimalRounding.Exact), "-"))));
        L.Register(mt, "__mul", static (ref CallContext c) =>
            c.Return(Wrap(c.L, UnwrapDec(SafeDecimal.TryMultiply(CheckDec64(ref c, 0).Value, CheckDec64(ref c, 1).Value, DecimalRounding.Exact), "*"))));
        L.Register(mt, "__div", static (ref CallContext c) =>
            c.Return(Wrap(c.L, UnwrapDec(SafeDecimal.TryDivide(CheckDec64(ref c, 0).Value, CheckDec64(ref c, 1).Value, DecimalRounding.Exact), "/"))));
        L.Register(mt, "__unm", static (ref CallContext c) =>
            c.Return(Wrap(c.L, UnwrapDec(SafeDecimal.TryNegate(CheckDec64(ref c, 0).Value), "-"))));
        L.Register(mt, "__lt", static (ref CallContext c) =>
            c.Return(CheckDec64(ref c, 0).Value < CheckDec64(ref c, 1).Value));
        L.Register(mt, "__le", static (ref CallContext c) =>
            c.Return(CheckDec64(ref c, 0).Value <= CheckDec64(ref c, 1).Value));
        L.Register(mt, "__eq", static (ref CallContext c) =>
            c.Return(c[0].O is LuaDec64 a && c[1].O is LuaDec64 b && a.Value == b.Value));
        L.Register(mt, "__tostring", static (ref CallContext c) =>
            c.Return(Value.FromText(CheckDec64(ref c, 0).ToString())));
        return mt;
    }

    private static Table BuildDec128Meta(LuaState L)
    {
        var methods = new Table();
        L.RegisterInstance<LuaDec128>(methods, "toNumber", static (ref CallContext c) =>
            c.Return(Value.FromFloat(double.Parse(CheckDec128(ref c, 0).Value.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture))));
        L.RegisterInstance<LuaDec128>(methods, "toDec64", static (ref CallContext c) =>
            c.Return(Wrap(c.L, UnwrapDec(SafeDecimal.TryConvert<Decimal128, Decimal64>(CheckDec128(ref c, 0).Value, ParseDecRounding(ref c, 1)), "toDec64"))));
        L.RegisterInstance<LuaDec128>(methods, "add", static (ref CallContext c) =>
            c.Return(Wrap(c.L, UnwrapDec(SafeDecimal.TryAdd(CheckDec128(ref c, 0).Value, CheckDec128(ref c, 1).Value, ParseDecRounding(ref c, 2)), "add"))));
        L.RegisterInstance<LuaDec128>(methods, "sub", static (ref CallContext c) =>
            c.Return(Wrap(c.L, UnwrapDec(SafeDecimal.TrySubtract(CheckDec128(ref c, 0).Value, CheckDec128(ref c, 1).Value, ParseDecRounding(ref c, 2)), "sub"))));
        L.RegisterInstance<LuaDec128>(methods, "mul", static (ref CallContext c) =>
            c.Return(Wrap(c.L, UnwrapDec(SafeDecimal.TryMultiply(CheckDec128(ref c, 0).Value, CheckDec128(ref c, 1).Value, ParseDecRounding(ref c, 2)), "mul"))));
        L.RegisterInstance<LuaDec128>(methods, "div", static (ref CallContext c) =>
            c.Return(Wrap(c.L, UnwrapDec(SafeDecimal.TryDivide(CheckDec128(ref c, 0).Value, CheckDec128(ref c, 1).Value, ParseDecRounding(ref c, 2)), "div"))));
        L.RegisterInstance<LuaDec128>(methods, "neg", static (ref CallContext c) =>
            c.Return(Wrap(c.L, UnwrapDec(SafeDecimal.TryNegate(CheckDec128(ref c, 0).Value), "neg"))));
        L.RegisterInstance<LuaDec128>(methods, "tryAdd", static (ref CallContext c) =>
            ReturnDec128Result(ref c, SafeDecimal.TryAdd(CheckDec128(ref c, 0).Value, CheckDec128(ref c, 1).Value, ParseDecRounding(ref c, 2))));
        L.RegisterInstance<LuaDec128>(methods, "trySub", static (ref CallContext c) =>
            ReturnDec128Result(ref c, SafeDecimal.TrySubtract(CheckDec128(ref c, 0).Value, CheckDec128(ref c, 1).Value, ParseDecRounding(ref c, 2))));
        L.RegisterInstance<LuaDec128>(methods, "tryMul", static (ref CallContext c) =>
            ReturnDec128Result(ref c, SafeDecimal.TryMultiply(CheckDec128(ref c, 0).Value, CheckDec128(ref c, 1).Value, ParseDecRounding(ref c, 2))));
        L.RegisterInstance<LuaDec128>(methods, "tryDiv", static (ref CallContext c) =>
            ReturnDec128Result(ref c, SafeDecimal.TryDivide(CheckDec128(ref c, 0).Value, CheckDec128(ref c, 1).Value, ParseDecRounding(ref c, 2))));
        L.RegisterInstance<LuaDec128>(methods, "tryNeg", static (ref CallContext c) =>
            ReturnDec128Result(ref c, SafeDecimal.TryNegate(CheckDec128(ref c, 0).Value)));

        var mt = new Table();
        mt.SetStr("__name", MetaDec128);
        mt.SetStr("__index", methods);
        L.Register(mt, "__add", static (ref CallContext c) =>
            c.Return(Wrap(c.L, UnwrapDec(SafeDecimal.TryAdd(CheckDec128(ref c, 0).Value, CheckDec128(ref c, 1).Value, DecimalRounding.Exact), "+"))));
        L.Register(mt, "__sub", static (ref CallContext c) =>
            c.Return(Wrap(c.L, UnwrapDec(SafeDecimal.TrySubtract(CheckDec128(ref c, 0).Value, CheckDec128(ref c, 1).Value, DecimalRounding.Exact), "-"))));
        L.Register(mt, "__mul", static (ref CallContext c) =>
            c.Return(Wrap(c.L, UnwrapDec(SafeDecimal.TryMultiply(CheckDec128(ref c, 0).Value, CheckDec128(ref c, 1).Value, DecimalRounding.Exact), "*"))));
        L.Register(mt, "__div", static (ref CallContext c) =>
            c.Return(Wrap(c.L, UnwrapDec(SafeDecimal.TryDivide(CheckDec128(ref c, 0).Value, CheckDec128(ref c, 1).Value, DecimalRounding.Exact), "/"))));
        L.Register(mt, "__unm", static (ref CallContext c) =>
            c.Return(Wrap(c.L, UnwrapDec(SafeDecimal.TryNegate(CheckDec128(ref c, 0).Value), "-"))));
        L.Register(mt, "__lt", static (ref CallContext c) =>
            c.Return(CheckDec128(ref c, 0).Value < CheckDec128(ref c, 1).Value));
        L.Register(mt, "__le", static (ref CallContext c) =>
            c.Return(CheckDec128(ref c, 0).Value <= CheckDec128(ref c, 1).Value));
        L.Register(mt, "__eq", static (ref CallContext c) =>
            c.Return(c[0].O is LuaDec128 a && c[1].O is LuaDec128 b && a.Value == b.Value));
        L.Register(mt, "__tostring", static (ref CallContext c) =>
            c.Return(Value.FromText(CheckDec128(ref c, 0).ToString())));
        return mt;
    }

    // ------------------------------------------------------------------ helpers

    private static void CopyMeta(Table src, Table dst)
    {
        foreach (KeyValuePair<Value, Value> kv in src.Pairs()) dst.Set(kv.Key, kv.Value);
    }

    private static Value Wrap(LuaState L, Fixed64 v) => Value.FromUserdata(new LuaFixed64(v, GetMeta(L, MetaFixed64)));
    private static Value Wrap(LuaState L, Decimal64 v) => Value.FromUserdata(new LuaDec64(v, GetMeta(L, MetaDec64)));
    private static Value Wrap(LuaState L, Decimal128 v) => Value.FromUserdata(new LuaDec128(v, GetMeta(L, MetaDec128)));

    private static Table GetMeta(LuaState L, string key)
    {
        Table mt = L.MetaFor(key);
        if (mt.GetStr("__index").IsNil) Open(L);
        return mt;
    }

    private static LuaFixed64 CheckFixed(ref CallContext c, int i) =>
        c[i].O as LuaFixed64 ?? throw c.ArgError(i, "expected Fixed64");
    private static LuaDec64 CheckDec64(ref CallContext c, int i) =>
        c[i].O as LuaDec64 ?? throw c.ArgError(i, "expected Decimal64");
    private static LuaDec128 CheckDec128(ref CallContext c, int i) =>
        c[i].O as LuaDec128 ?? throw c.ArgError(i, "expected Decimal128");

    private static Decimal64 ParseDecimal64(ref CallContext c, int i)
    {
        Value v = c[i];
        string text = v.Kind switch
        {
            ValueKind.String => c.CheckText(i),
            ValueKind.Integer => v.I.ToString(CultureInfo.InvariantCulture),
            ValueKind.Float => v.N.ToString("G17", CultureInfo.InvariantCulture),
            _ => throw c.ArgError(i, "expected string or number"),
        };
        return RunChecked(() =>
        {
            Decimal64 d = Decimal64.Parse(text, CultureInfo.InvariantCulture);
            if (!Decimal64.IsFinite(d)) throw new LuaException("safenum.dec64: value must be finite");
            return d;
        });
    }

    private static Decimal128 ParseDecimal128(ref CallContext c, int i)
    {
        Value v = c[i];
        string text = v.Kind switch
        {
            ValueKind.String => c.CheckText(i),
            ValueKind.Integer => v.I.ToString(CultureInfo.InvariantCulture),
            ValueKind.Float => v.N.ToString("G17", CultureInfo.InvariantCulture),
            _ => throw c.ArgError(i, "expected string or number"),
        };
        return RunChecked(() =>
        {
            Decimal128 d = Decimal128.Parse(text, CultureInfo.InvariantCulture);
            if (!Decimal128.IsFinite(d)) throw new LuaException("safenum.dec128: value must be finite");
            return d;
        });
    }

    private static ArithmeticPolicy ParsePolicy(ref CallContext c, int i)
    {
        if (c.IsNoneOrNil(i)) return ArithmeticPolicy.Reject;
        return c.CheckText(i) switch
        {
            "reject" => ArithmeticPolicy.Reject,
            "saturate" => ArithmeticPolicy.Saturate,
            "wrap" => ArithmeticPolicy.Wrap,
            "throw" => ArithmeticPolicy.Throw,
            string s => throw c.ArgError(i, $"unknown arithmetic policy '{s}'"),
        };
    }

    private static FixedRounding ParseFixedRounding(ref CallContext c, int i)
    {
        if (c.IsNoneOrNil(i)) return FixedRounding.Exact;
        return c.CheckText(i) switch
        {
            "exact" => FixedRounding.Exact,
            "to_even" => FixedRounding.ToEven,
            "to_zero" => FixedRounding.ToZero,
            "floor" => FixedRounding.Floor,
            "ceiling" => FixedRounding.Ceiling,
            "away_from_zero" => FixedRounding.AwayFromZero,
            string s => throw c.ArgError(i, $"unknown fixed rounding '{s}'"),
        };
    }

    private static DecimalRounding ParseDecRounding(ref CallContext c, int i)
    {
        if (c.IsNoneOrNil(i)) return DecimalRounding.Exact;
        return c.CheckText(i) switch
        {
            "exact" => DecimalRounding.Exact,
            "to_even" => DecimalRounding.ToEven,
            string s => throw c.ArgError(i, $"unknown decimal rounding '{s}'"),
        };
    }

    private static string ErrorName(ArithmeticError err) => err switch
    {
        ArithmeticError.None => "none",
        ArithmeticError.Overflow => "overflow",
        ArithmeticError.Underflow => "underflow",
        ArithmeticError.DivideByZero => "divide_by_zero",
        ArithmeticError.OutOfRange => "out_of_range",
        ArithmeticError.Inexact => "inexact",
        ArithmeticError.NonFinite => "non_finite",
        _ => err.ToString().ToLowerInvariant(),
    };

    private static int ReturnIntResult(ref CallContext c, NumericResult<long> r) =>
        r.IsSuccess
            ? c.Return(Value.FromInt(r.Value), Value.FromText("none"))
            : c.Return(Value.Nil, Value.FromText(ErrorName(r.Error)));

    private static int ReturnFixedResult(ref CallContext c, NumericResult<Fixed64> r) =>
        r.IsSuccess
            ? c.Return(Wrap(c.L, r.Value), Value.FromText("none"))
            : c.Return(Value.Nil, Value.FromText(ErrorName(r.Error)));

    private static int ReturnDec64Result(ref CallContext c, DecimalResult<Decimal64> r) =>
        r.IsSuccess
            ? c.Return(Wrap(c.L, r.Value), Value.FromText("none"))
            : c.Return(Value.Nil, Value.FromText(ErrorName(r.Error)));

    private static int ReturnDec128Result(ref CallContext c, DecimalResult<Decimal128> r) =>
        r.IsSuccess
            ? c.Return(Wrap(c.L, r.Value), Value.FromText("none"))
            : c.Return(Value.Nil, Value.FromText(ErrorName(r.Error)));

    private static Fixed64 UnwrapFixed(NumericResult<Fixed64> r, string op) =>
        r.IsSuccess ? r.Value : throw new LuaException($"safenum.Fixed64.{op}: {ErrorName(r.Error)}");

    private static T UnwrapDec<T>(DecimalResult<T> r, string op) where T : struct =>
        r.IsSuccess ? r.Value : throw new LuaException($"safenum.Decimal.{op}: {ErrorName(r.Error)}");

    private static int[] ExtractInt32s(ref CallContext c, int argIndex)
    {
        Value v = c[argIndex];
        if (v.O is LuaList list)
        {
            var arr = new int[list.Items.Count];
            for (int i = 0; i < arr.Length; i++)
            {
                if (!list.Items[i].TryToInteger(out long n) || n < int.MinValue || n > int.MaxValue)
                    throw c.ArgError(argIndex, $"element {i + 1} must be a 32-bit signed integer");
                arr[i] = (int)n;
            }
            return arr;
        }
        Table t = c.CheckTable(argIndex);
        int len = t.Length;
        var res = new int[len];
        for (int i = 0; i < len; i++)
        {
            if (!t.GetInt(i + 1).TryToInteger(out long n) || n < int.MinValue || n > int.MaxValue)
                throw c.ArgError(argIndex, $"element {i + 1} must be a 32-bit signed integer");
            res[i] = (int)n;
        }
        return res;
    }

    private static T RunChecked<T>(Func<T> fn)
    {
        try { return fn(); }
        catch (LuaException) { throw; }
        catch (Exception ex) { throw new LuaException($"safenum: {ex.Message}"); }
    }
}

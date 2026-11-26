using Cameo.Runtime;
using System.Text;
using Cameo.Runtime.Vm;
using Cameo.Runtime.Stdlib;

namespace Cameo.Fst;

/// <summary>A writer handed to a script by <c>fst.create</c>.</summary>
public sealed class LuaFstWriter(LuaState state, FstWriter writer, string path) : IUserdata
{
    public FstWriter Writer => writer;
    public string Path => path;
    public bool Closed;
    public string TypeName => "FstWriter";
    public Table? Metatable { get => state.MetaFor("FstWriter"); set { } }
}

/// <summary>
/// <c>fst</c>: reading and writing GTKWave's FST waveform files. An add-on like the SQLite
/// provider and HDF5; it has no dependencies.
///
/// Problems with a file (missing, unreadable, not FST, damaged) come back as nil and an
/// <c>IoError</c>. Mistakes in the calling program (an unknown handle, time going backwards,
/// a value of the wrong kind) are raised as errors.
/// </summary>
public static class FstLibrary
{
    /// <summary>Adds the <c>fst</c> library to an engine.</summary>
    public static void Enable(ScriptEngine engine)
    {
        LuaState L = engine.State;
        var lib = new Table();

        L.Register(lib, "info", static (ref CallContext c) =>
        {
            string path = c.CheckText(0);
            return Try(ref c, path, L =>
            {
                using FstReader r = FstReader.Open(path);
                return Info(r);
            });
        });
        L.Register(lib, "read", static (ref CallContext c) =>
        {
            string path = c.CheckText(0), name = c.CheckText(1);
            return Try(ref c, path, L =>
            {
                using FstReader r = FstReader.Open(path);
                FstSignal s = r.Find(name) ?? throw new FileNotFoundException($"no variable '{name}' in the file");
                return Trace(L, s, r.Read([s.Handle])[s.Handle]);
            });
        });
        L.Register(lib, "readAll", static (ref CallContext c) =>
        {
            string path = c.CheckText(0);
            List<string>? wanted = null;
            if (!c.IsNoneOrNil(1))
            {
                Table t = c.CheckTable(1);
                wanted = new List<string>();
                for (int i = 1; i <= t.Length; i++) wanted.Add(t.GetInt(i).ToText());
            }
            return Try(ref c, path, L =>
            {
                using FstReader r = FstReader.Open(path);
                var signals = new List<FstSignal>();
                if (wanted is null) signals.AddRange(r.Signals);
                else foreach (string name in wanted) signals.Add(r.Find(name) ?? throw new FileNotFoundException($"no variable '{name}' in the file"));
                Dictionary<int, FstTrace> traces = r.Read(signals.Select(s => s.Handle).Distinct());
                var result = new Table();
                for (int i = 0; i < signals.Count; i++)
                    result.Set(Value.FromText(wanted is null ? signals[i].Name : wanted[i]), Trace(L, signals[i], traces[signals[i].Handle]));
                return Value.FromTable(result);
            });
        });
        L.Register(lib, "write", static (ref CallContext c) =>
        {
            string path = c.CheckText(0);
            Table spec = c.CheckTable(1);
            Plan plan = ReadSpec(ref c, spec);              // mistakes in the description are raised here
            return Try(ref c, path, L =>
            {
                WriteAll(path, plan);
                return Value.True;
            });
        });
        L.Register(lib, "create", static (ref CallContext c) =>
        {
            string path = c.CheckText(0);
            Table? options = c.IsNoneOrNil(1) ? null : c.CheckTable(1);
            var (exponent, version, date) = Options(ref c, options);
            return Try(ref c, path, L => Value.FromUserdata(new LuaFstWriter(L, new FstWriter(path, exponent, version, date), path)));
        });

        var m = new Table();
        L.RegisterInstance<LuaFstWriter>(m, "scope", static (ref CallContext c) =>
        {
            LuaFstWriter w = Self(ref c);
            string name = c.CheckText(1);
            FstScopeType type = c.IsNoneOrNil(2) ? FstScopeType.Module : Lookup<FstScopeType>(ref c, 2, FstFormat.ScopeTypeNames, "scope type");
            Misuse(() => w.Writer.PushScope(name, type));
            return c.Return(c[0]);
        });
        L.RegisterInstance<LuaFstWriter>(m, "upscope", static (ref CallContext c) =>
        {
            LuaFstWriter w = Self(ref c);
            Misuse(() => w.Writer.PopScope());
            return c.Return(c[0]);
        });
        L.RegisterInstance<LuaFstWriter>(m, "var", static (ref CallContext c) =>
        {
            LuaFstWriter w = Self(ref c);
            string name = c.CheckText(1);
            int width = (int)Math.Clamp(c.OptInt(2, 1), int.MinValue, int.MaxValue);
            FstVarType type = c.IsNoneOrNil(3) ? FstVarType.Wire : Lookup<FstVarType>(ref c, 3, FstFormat.VarTypeNames, "variable type");
            return c.Return(Value.FromInt(Misuse(() => w.Writer.AddVariable(name, width, type))));
        });
        L.RegisterInstance<LuaFstWriter>(m, "real", static (ref CallContext c) =>
        {
            LuaFstWriter w = Self(ref c);
            string name = c.CheckText(1);
            return c.Return(Value.FromInt(Misuse(() => w.Writer.AddReal(name))));
        });
        L.RegisterInstance<LuaFstWriter>(m, "string", static (ref CallContext c) =>
        {
            LuaFstWriter w = Self(ref c);
            string name = c.CheckText(1);
            return c.Return(Value.FromInt(Misuse(() => w.Writer.AddString(name))));
        });
        L.RegisterInstance<LuaFstWriter>(m, "time", static (ref CallContext c) =>
        {
            LuaFstWriter w = Self(ref c);
            long t = c.CheckInt(1);
            if (t < 0) throw c.ArgError(1, "a time cannot be negative");
            Misuse(() => w.Writer.SetTime((ulong)t));
            return c.Return(c[0]);
        });
        L.RegisterInstance<LuaFstWriter>(m, "set", static (ref CallContext c) =>
        {
            LuaFstWriter w = Self(ref c);
            long handle = c.CheckInt(1);
            Value value = c[2];
            Misuse(() => Emit(w.Writer, (int)Math.Clamp(handle, int.MinValue, int.MaxValue), value));
            return c.Return(c[0]);
        });
        L.RegisterInstance<LuaFstWriter>(m, "close", static (ref CallContext c) =>
        {
            LuaFstWriter w = c[0].O as LuaFstWriter ?? throw c.TypeError(0, "FstWriter");
            if (w.Closed) return c.Return(Value.True, Value.Nil);          // closing twice is fine
            w.Closed = true;
            return Try(ref c, w.Path, _ => { w.Writer.Close(); return Value.True; });
        });
        Table mt = L.MetaFor("FstWriter");
        mt.SetStr("__index", m);
        mt.SetStr("__close", m.GetStr("close"));
        mt.SetStr("__gc", m.GetStr("close"));

        L.Globals.SetStr("fst", lib);
        L.LoadedModules.SetStr("fst", lib);
    }

    private static LuaFstWriter Self(ref CallContext c)
    {
        LuaFstWriter w = c[0].O as LuaFstWriter ?? throw c.TypeError(0, "FstWriter");
        if (w.Closed) throw new LuaException("the FST writer is closed");
        return w;
    }

    private static T Lookup<T>(ref CallContext c, int arg, string[] names, string what) where T : struct, Enum
    {
        string name = c.CheckText(arg);
        int i = Array.IndexOf(names, name);
        if (i < 0) throw c.ArgError(arg, $"unknown {what} '{name}' (one of: {string.Join(", ", names)})");
        return (T)Enum.ToObject(typeof(T), (byte)i);
    }

    // A mistake in the calling program: raised as an error, with the writer's message.
    private static void Misuse(Action action) => Misuse(() => { action(); return 0; });

    private static T Misuse<T>(Func<T> action)
    {
        try { return action(); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or OverflowException)
        {
            throw new LuaException(LuaText.Encode(e is ArgumentException { ParamName: not null } a ? a.Message.Split(" (Parameter")[0] : e.Message));
        }
    }

    private static void Emit(FstWriter w, int handle, in Value value)
    {
        if (value.IsInteger) w.EmitInteger(handle, value.I);
        else if (value.IsFloat)
        {
            value.TryToDouble(out double d);
            // a whole number given to a bit variable is that number
            if (w.IsReal(handle) || d != Math.Floor(d) || Math.Abs(d) >= 9.2e18) w.EmitReal(handle, d);
            else w.EmitInteger(handle, (long)d);
        }
        else if (value.IsBoolean) w.EmitInteger(handle, value.IsTruthy ? 1 : 0);
        else if (value.IsString)
        {
            byte[] bytes = Encoding.Latin1.GetBytes(value.AsString);
            if (w.IsString(handle)) w.EmitString(handle, bytes);
            else w.Emit(handle, bytes);
        }
        else throw new ArgumentException($"a value is a number, a string or a boolean, not a {LuaState.TypeNameOf(value)}");
    }

    private delegate Value Operation(LuaState L);

    private static int Try(ref CallContext c, string target, Operation op)
    {
        try
        {
            return c.Return(op(c.L), Value.Nil);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FstFormatException or NotSupportedException or System.Security.SecurityException)
        {
            IoErrorKind kind = e switch
            {
                FileNotFoundException or DirectoryNotFoundException => IoErrorKind.NotFound,
                UnauthorizedAccessException or System.Security.SecurityException => IoErrorKind.PermissionDenied,
                FstFormatException => IoErrorKind.Invalid,
                _ => IoErrorKind.Failed,
            };
            return c.Return(Value.Nil, new IoError(kind, e.Message, target).ToValue(c.L));
        }
    }

    // ------------------------------------------------------------------ reading

    private static Value Info(FstReader r)
    {
        var t = new Table();
        t.SetStr("timescale", Value.FromText(FstFormat.TimescaleText(r.TimescaleExponent)));
        t.SetStr("timescaleExponent", Value.FromInt(r.TimescaleExponent));
        t.SetStr("startTime", Value.FromInt((long)r.StartTime));
        t.SetStr("endTime", Value.FromInt((long)r.EndTime));
        t.SetStr("version", Value.FromText(r.Version));
        t.SetStr("date", Value.FromText(r.Date));
        var signals = new Table(r.Signals.Count, 0);
        foreach (FstSignal s in r.Signals)
        {
            var e = new Table();
            e.SetStr("name", Value.FromText(s.Name));
            e.SetStr("scope", Value.FromText(s.Scope));
            e.SetStr("handle", Value.FromInt(s.Handle));
            e.SetStr("width", Value.FromInt(s.Width));
            e.SetStr("type", TypeName(s));
            e.SetStr("direction", FstFormat.DirectionNames[Math.Min((int)s.Direction, FstFormat.DirectionNames.Length - 1)]);
            signals.Append(Value.FromTable(e));
        }
        t.SetStr("signals", Value.FromTable(signals));
        var scopes = new Table(r.Scopes.Count, 0);
        foreach (FstScope s in r.Scopes)
        {
            var e = new Table();
            e.SetStr("name", Value.FromText(s.Name));
            e.SetStr("type", (int)s.Type < FstFormat.ScopeTypeNames.Length ? FstFormat.ScopeTypeNames[(int)s.Type] : "other");
            scopes.Append(Value.FromTable(e));
        }
        t.SetStr("scopes", Value.FromTable(scopes));
        return Value.FromTable(t);
    }

    private static string TypeName(FstSignal s) => FstFormat.VarTypeNames[(int)s.Type];

    private static Value Trace(LuaState L, FstSignal s, FstTrace trace)
    {
        var t = new Table();
        t.SetStr("name", Value.FromText(s.Name));
        t.SetStr("type", TypeName(s));
        t.SetStr("width", Value.FromInt(s.Width));
        L.CheckArrayLength(trace.Times.Count);
        var times = new Table(trace.Times.Count, 0);
        foreach (ulong time in trace.Times) times.Append(Value.FromInt((long)time));
        t.SetStr("times", Value.FromTable(times));
        if (s.IsReal)
        {
            t.SetStr("reals", Value.FromUserdata(new FloatArray(L, trace.Reals.ToArray())));
        }
        else
        {
            var values = new Table(trace.Values.Count, 0);
            foreach (byte[] v in trace.Values) values.Append(Value.FromString(Encoding.Latin1.GetString(v)));
            t.SetStr("values", Value.FromTable(values));
        }
        return Value.FromTable(t);
    }

    // ------------------------------------------------------------------ writing a whole file

    private sealed class PlannedSignal
    {
        public required string[] Path;          // scopes, then the variable's name
        public FstVarType Type;
        public int Width;
        public long[]? Times;                   // or evenly spaced:
        public long Start, Step;
        public required Value[] Values;
        public double[]? Reals;
        public int Count => Reals?.Length ?? Values.Length;
        public long TimeAt(int i) => Times is null ? Start + Step * i : Times[i];
        public int Handle;
    }

    private sealed record Plan(int Exponent, string Version, string Date, List<PlannedSignal> Signals);

    private static (int Exponent, string Version, string Date) Options(ref CallContext c, Table? options)
    {
        int exponent = -9;
        string version = "Cameo.Runtime", date = DateTime.Now.ToString("ddd MMM d HH:mm:ss yyyy", System.Globalization.CultureInfo.InvariantCulture);
        if (options is null) return (exponent, version, date);
        Value ts = options.GetStr("timescale");
        if (ts.IsInteger)
        {
            if (ts.I is < -128 or > 127) throw new LuaException("timescale: the exponent must be between -128 and 127");
            exponent = (int)ts.I;
        }
        else if (ts.IsString)
        {
            if (!FstFormat.TryParseTimescale(ts.ToText(), out exponent))
                throw new LuaException(LuaText.Encode($"timescale: '{ts.ToText()}' is not 1, 10 or 100 of s, ms, us, ns, ps or fs"));
        }
        else if (!ts.IsNil) throw new LuaException("timescale: expected a string such as \"1ns\" or an exponent of ten");
        if (options.GetStr("version").IsString) version = options.GetStr("version").ToText();
        if (options.GetStr("date").IsString) date = options.GetStr("date").ToText();
        return (exponent, version, date);
    }

    private static Plan ReadSpec(ref CallContext c, Table spec)
    {
        var (exponent, version, date) = Options(ref c, spec);
        if (spec.GetStr("signals").O is not Table list) throw new LuaException("fst.write: the description needs a 'signals' list");
        var signals = new List<PlannedSignal>();
        for (int i = 1; i <= list.Length; i++)
        {
            if (list.GetInt(i).O is not Table s) throw Bad(i, "is not a table");
            if (!s.GetStr("name").IsString) throw Bad(i, "has no name");
            string name = s.GetStr("name").ToText();
            string[] path = name.Split('.');
            if (path.Any(p => p.Length == 0)) throw Bad(i, $"has an empty part in its name '{name}'");

            Value values = s.GetStr("values");
            double[]? reals = (values.O as FloatArray)?.Data;
            Value[] items = [];
            if (reals is null)
            {
                if (values.O is not Table vt) throw Bad(i, "needs 'values': a list or a FloatArray");
                items = new Value[vt.Length];
                for (int k = 0; k < items.Length; k++) items[k] = vt.GetInt(k + 1);
            }

            FstVarType type;
            Value typeName = s.GetStr("type");
            if (typeName.IsString)
            {
                int index = Array.IndexOf(FstFormat.VarTypeNames, typeName.ToText());
                if (index < 0) throw Bad(i, $"has an unknown type '{typeName.ToText()}'");
                type = (FstVarType)index;
            }
            else type = reals is not null ? FstVarType.Real : FstVarType.Wire;
            if (reals is not null && !FstFormat.IsReal(type)) throw Bad(i, "has a FloatArray of values, so its type must be \"real\"");
            if (FstFormat.IsReal(type) && reals is null)
            {
                reals = new double[items.Length];
                for (int k = 0; k < items.Length; k++)
                    if (!items[k].IsNumber || !items[k].TryToDouble(out reals[k])) throw Bad(i, $"value {k + 1} is not a number");
            }

            var planned = new PlannedSignal { Path = path, Type = type, Values = items, Reals = reals };
            Value width = s.GetStr("width");
            if (width.IsInteger && width.I is >= 1 and <= 1 << 20) planned.Width = (int)width.I;
            else if (width.IsNil) planned.Width = 1;
            else throw Bad(i, "has a width that is not a whole number from 1 to 1048576");

            Value times = s.GetStr("times");
            if (times.O is Table tt)
            {
                if (tt.Length != planned.Count) throw Bad(i, $"has {tt.Length} times for {planned.Count} values");
                planned.Times = new long[tt.Length];
                for (int k = 0; k < planned.Times.Length; k++)
                {
                    Value t = tt.GetInt(k + 1);
                    if (!t.TryToInteger(out planned.Times[k]) || t.IsString || planned.Times[k] < 0) throw Bad(i, $"time {k + 1} is not a whole number of ticks, zero or more");
                    if (k > 0 && planned.Times[k] < planned.Times[k - 1]) throw Bad(i, $"time {k + 1} is before time {k}");
                }
            }
            else if (times.IsNil)
            {
                planned.Start = Whole(s.GetStr("t0"), 0, i, "t0");
                planned.Step = Whole(s.GetStr("dt"), 1, i, "dt");
                if (planned.Step < 1) throw Bad(i, "has a dt that is not 1 or more");
                if (planned.Start < 0) throw Bad(i, "has a negative t0");
            }
            else throw Bad(i, "has 'times' that is not a list");
            signals.Add(planned);
        }
        return new Plan(exponent, version, date, signals);

        static long Whole(in Value v, long fallback, int i, string what)
        {
            if (v.IsNil) return fallback;
            if (v.IsString || !v.TryToInteger(out long n)) throw Bad(i, $"has a {what} that is not a whole number of ticks");
            return n;
        }
        static LuaException Bad(int i, string what) => new(LuaText.Encode($"fst.write: signal {i} {what}"));
    }

    private sealed class ScopeNode
    {
        public readonly List<(string Name, ScopeNode Node)> Children = new();
        public readonly List<PlannedSignal> Signals = new();
        public ScopeNode Child(string name)
        {
            foreach (var (n, node) in Children)
                if (n == name) return node;
            var made = new ScopeNode();
            Children.Add((name, made));
            return made;
        }
    }

    private static void WriteAll(string path, Plan plan)
    {
        using var w = new FstWriter(path, plan.Exponent, plan.Version, plan.Date);
        // "top.adc.ch1" declares scope top, scope adc inside it, and the variable ch1
        var root = new ScopeNode();
        foreach (PlannedSignal s in plan.Signals)
        {
            ScopeNode node = root;
            for (int i = 0; i < s.Path.Length - 1; i++) node = node.Child(s.Path[i]);
            node.Signals.Add(s);
        }
        Declare(w, root);

        // merge the signals by time
        var queue = new PriorityQueue<(PlannedSignal Signal, int Index), (long Time, int Order)>();
        for (int i = 0; i < plan.Signals.Count; i++)
            if (plan.Signals[i].Count > 0) queue.Enqueue((plan.Signals[i], 0), (plan.Signals[i].TimeAt(0), i));
        while (queue.TryDequeue(out var item, out var priority))
        {
            var (s, index) = item;
            w.SetTime((ulong)priority.Time);
            try
            {
                if (s.Reals is not null) w.EmitReal(s.Handle, s.Reals[index]);
                else Emit(w, s.Handle, s.Values[index]);
            }
            catch (ArgumentException e)
            {
                throw new LuaException(LuaText.Encode($"fst.write: {string.Join('.', s.Path)}, value {index + 1}: {e.Message.Split(" (Parameter")[0]}"));
            }
            if (index + 1 < s.Count) queue.Enqueue((s, index + 1), (s.TimeAt(index + 1), priority.Order));
        }
        w.Close();

        static void Declare(FstWriter w, ScopeNode node)
        {
            foreach (PlannedSignal s in node.Signals) s.Handle = w.AddVariable(s.Path[^1], s.Width, s.Type);
            foreach (var (name, child) in node.Children)
            {
                w.PushScope(name);
                Declare(w, child);
                w.PopScope();
            }
        }
    }
}

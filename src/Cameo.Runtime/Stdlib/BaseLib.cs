using Cameo.Runtime.Vm;

namespace Cameo.Runtime.Stdlib;

/// <summary>Basic functions: print, type, pairs, pcall, error, setmetatable, require, ...</summary>
public static class BaseLib
{
    public static void Open(LuaState L, bool sandboxed = false)
    {
        Table g = L.Globals;
        g.SetStr("_G", g);
        g.SetStr("_VERSION", "Lua 5.4");
        L.Register(g, "print", Print);
        L.Register(g, "type", Type);
        L.Register(g, "tostring", ToString);
        L.Register(g, "tonumber", ToNumber);
        L.Register(g, "ipairs", IPairs);
        L.Register(g, "pairs", Pairs);
        L.Register(g, "next", Next);
        L.Register(g, "select", Select);
        L.Register(g, "rawget", RawGet);
        L.Register(g, "rawset", RawSet);
        // __class(class, instanceMetatable [, parent [, mixins [, getters [, setters]]]]): what a
        // `class` declaration calls once, after its functions are defined.
        //  - the class gets every member of its parent and of its mixins that it does not define
        //    itself, copied in, so that a method is found in one lookup however deep the hierarchy;
        //    the parent stays as the fallback for members added to it later
        //  - the instances' metatable takes over the parent's metamethods (Lua does not look
        //    metamethods up through __index) and holds the property getters and setters
        L.Register(g, "__class", static (ref CallContext c) =>
        {
            Table cls = c.CheckTable(0), instanceMeta = c.CheckTable(1);
            var classMeta = new Table(0, 2);
            classMeta.SetStr("__instance", instanceMeta);
            Table? getters = c[4].O as Table, setters = c[5].O as Table;
            string className = c[6].IsString ? c[6].AsString : "?";
            Table? about = c[7].O as Table;
            // the class's identity is carried by the functions written inside its declaration
            ClassTag? tag = (cls.GetStr("new").O as Closure)?.Proto.ClassTag;
            var data = new ClassData { Name = className, Id = about?.GetStr("id") is { IsString: true } id ? id.AsString : className, OwnerTag = tag };
            data.Ids.Add(data.Id);
            Table? hiddenMembers = null;
            void InheritClass(Table from)
            {
                if (from.Metatable?.Class is not { } parentData) return;
                data.Ids.UnionWith(parentData.Ids);
                if (parentData.Hidden == null) return;
                foreach (KeyValuePair<Value, Value> kv in parentData.Hidden.Pairs())
                {
                    var member = (HiddenMember)kv.Value.O!;
                    // a protected member is also for the code of this class
                    if (member.Protected && tag != null) member.Allowed.Add(tag);
                    hiddenMembers ??= new Table();
                    if (hiddenMembers.Get(kv.Key).IsNil) hiddenMembers.Set(kv.Key, kv.Value);
                }
            }
            IEnumerable<string> NamesOf(string field)
            {
                if (about?.GetStr(field).O is not Table list) yield break;
                for (int n = 1; n <= list.Length; n++) if (list.GetInt(n).IsString) yield return list.GetInt(n).AsString;
            }
            // an abstract function that nothing defines raises when called
            foreach (string name in NamesOf("abstract"))
            {
                string abstractName = name;
                if (cls.GetStr(name).IsNil)
                    cls.SetStr(name, Value.FromFunction((ref CallContext _) =>
                        throw new LuaException($"abstract function '{abstractName}' of class '{className}' is not defined"), name));
            }

            void Inherit(Table from)
            {
                foreach (KeyValuePair<Value, Value> kv in from.Pairs())
                    if (cls.Get(kv.Key).IsNil) cls.Set(kv.Key, kv.Value);
                if (from.Metatable?.GetStr("__instance").O is Table meta)
                {
                    foreach (KeyValuePair<Value, Value> kv in meta.Pairs())
                        if (kv.Key.IsString && kv.Key.AsString != "__index" && instanceMeta.Get(kv.Key).IsNil) instanceMeta.Set(kv.Key, kv.Value);
                    Merge(ref getters, meta.Getters);
                    Merge(ref setters, meta.Setters);
                }
            }
            static void Merge(ref Table? into, Table? from)
            {
                if (from == null) return;
                into ??= new Table();
                foreach (KeyValuePair<Value, Value> kv in from.Pairs())
                    if (into.Get(kv.Key).IsNil) into.Set(kv.Key, kv.Value);
            }

            // mixins first: two of them offering the same member is a conflict the class must settle
            if (c[3].O is Table mixins)
            {
                var seen = new Dictionary<string, int>();
                for (int m = 1; m <= mixins.Length; m++)
                {
                    if (mixins.GetInt(m).O is not Table mixin) throw c.ArgError(3, $"mixin {m} of class '{LuaText.Decode(className)}' is not a class or record");
                    foreach (KeyValuePair<Value, Value> kv in mixin.Pairs())
                    {
                        if (!kv.Key.IsString || kv.Key.AsString is "new" or "__init" || !cls.Get(kv.Key).IsNil && !seen.ContainsKey(kv.Key.AsString)) continue;
                        if (seen.TryGetValue(kv.Key.AsString, out int first) && first != m && !cls.Get(kv.Key).RawEquals(kv.Value))
                            throw new LuaException($"class '{className}': mixins {first} and {m} both define '{kv.Key.AsString}'; define it in the class to choose");
                        seen[kv.Key.AsString] = m;
                        cls.Set(kv.Key, kv.Value);
                    }
                    if (mixin.Metatable?.GetStr("__instance").O is Table meta)
                    {
                        Merge(ref getters, meta.Getters);
                        Merge(ref setters, meta.Setters);
                    }
                    InheritClass(mixin);
                }
            }
            if (c[2].O is Table parent)
            {
                classMeta.SetStr("__index", parent);
                Inherit(parent);
                InheritClass(parent);
            }
            // a property with a getter and no setter cannot be assigned
            if (getters != null)
            {
                foreach (KeyValuePair<Value, Value> kv in getters.Pairs())
                {
                    if (setters != null && !setters.Get(kv.Key).IsNil) continue;
                    string property = kv.Key.IsString ? kv.Key.AsString : "?";
                    (setters ??= new Table()).Set(kv.Key, Value.FromFunction(
                        (ref CallContext _) => throw new LuaException($"property '{property}' of class '{className}' is read-only"), "readonly"));
                }
            }
            // null when the class has no properties, so that its field lookups keep the short path
            instanceMeta.Getters = getters != null && getters.Pairs().Any() ? getters : null;
            instanceMeta.Setters = setters != null && setters.Pairs().Any() ? setters : null;
            // private and protected members of this class (they replace inherited ones of the same name)
            if (tag != null)
            {
                foreach (bool isProtected in new[] { false, true })
                    foreach (string name in NamesOf(isProtected ? "protected" : "private"))
                    {
                        var member = new HiddenMember(className, isProtected);
                        member.Allowed.Add(tag);
                        (hiddenMembers ??= new Table()).SetStr(name, Value.FromUserdata(member));
                    }
                data.Hidden = hiddenMembers;
                data.FastTag = tag;
                if (hiddenMembers != null)
                    foreach (KeyValuePair<Value, Value> kv in hiddenMembers.Pairs())
                        if (!((HiddenMember)kv.Value.O!).Allowed.Contains(tag)) { data.FastTag = null; break; }
            }
            instanceMeta.Class = data;
            classMeta.Class = data;
            cls.Metatable = classMeta;
            if (data.Hidden != null)
            {
                // from here on, the class table's hidden members are checked too
                cls.Guarded = true;
                cls.GuardTag = data.FastTag;
            }
            return c.Return();
        });
        // __isa(value, id): what `value is Class` compiles to
        L.Register(g, "__isa", static (ref CallContext c) =>
            c.Return(c[0].O is Table { Metatable.Class: { } data } && c[1].IsString && data.Ids.Contains(c[1].AsString)));
        // instanceof(value, Class): the object's class is Class, derives from it, or uses it as a mixin
        L.Register(g, "instanceof", static (ref CallContext c) =>
        {
            if (c[1].O is not Table { Metatable.Class: { } wanted }) throw c.ArgError(1, "a class expected");
            return c.Return(c[0].O is Table { Metatable.Class: { } data } && data.Ids.Contains(wanted.Id));
        });
        // __cameo_record(recTable, instanceMeta, id, name): wires a Cameo record's method table,
        // instance metatable, runtime identity (`is`/`instanceof`), and `Rec { ... }` constructor.
        L.Register(g, "__cameo_record", static (ref CallContext c) =>
        {
            Table rec = c.CheckTable(0), instanceMeta = c.CheckTable(1);
            string id = c.CheckString(2);
            string name = c[3].IsString ? c[3].AsString : id;
            instanceMeta.SetStr("__index", Value.FromTable(rec));
            instanceMeta.IsCameoInstanceMeta = true;
            var data = new ClassData { Name = name, Id = id };
            data.Ids.Add(id);
            instanceMeta.Class = data;
            var recMeta = new Table(0, 2);
            recMeta.SetStr("__instance", Value.FromTable(instanceMeta));
            recMeta.Class = data;
            recMeta.SetStr("__call", Value.FromFunction((ref CallContext cc) =>
            {
                Table tbl = cc.Count >= 2 && !cc[1].IsNil ? cc.CheckTable(1) : new Table();
                tbl.Metatable = instanceMeta;
                return cc.Return(Value.FromTable(tbl));
            }, name));
            rec.Metatable = recMeta;
            (c.L.CameoRecordMetas ??= new())[id] = instanceMeta;
            return c.Return();
        });
        // __cameo_bless(tbl, mtOrId): attaches a Cameo record's instance metatable to a table literal.
        L.Register(g, "__cameo_bless", static (ref CallContext c) =>
        {
            Table tbl = c.CheckTable(0);
            if (c[1].O is Table mt) tbl.Metatable = mt;
            else if (c[1].IsString && c.L.CameoRecordMetas != null && c.L.CameoRecordMetas.TryGetValue(c[1].AsString, out Table? regMt))
                tbl.Metatable = regMt;
            return c.Return(c[0]);
        });
        L.Register(g, "rawequal", RawEqual);
        L.Register(g, "rawlen", RawLen);
        L.Register(g, "setmetatable", SetMetatable);
        L.Register(g, "getmetatable", GetMetatable);
        L.Register(g, "assert", Assert);
        L.Register(g, "unwrap", Unwrap);
        L.Register(g, "error", Error);
        g.SetStr("xpcall", Value.FromFunction(new NativeFunction("xpcall", XPCall) { Special = LuaState.SpecialXpcall }));
        L.Register(g, "collectgarbage", CollectGarbage);
        L.Register(g, "require", Require);
        L.Register(g, "load", Load);
        L.Register(g, "loadstring", Load);
        if (!sandboxed) L.Register(g, "dofile", DoFile);
        L.Register(g, "warn", Warn);

        var pcall = new NativeFunction("pcall", PCall) { Special = LuaState.SpecialPcall };
        g.SetStr("pcall", Value.FromFunction(pcall));

        var package = new Table();
        package.SetStr("loaded", L.LoadedModules);
        package.SetStr("path", "./?.tl;./?/init.tl");
        package.SetStr("cpath", "");
        package.SetStr("config", "/\n;\n?\n!\n-\n");
        var preload = new Table();
        package.SetStr("preload", preload);
        L.Register(package, "searchpath", SearchPath);
        // native libraries cannot be loaded: the three results Lua gives for a failure
        L.Register(package, "loadlib", static (ref CallContext c) =>
            c.Return(Value.Nil, Value.FromString("dynamic libraries are not supported"), Value.FromString("absent")));
        // the two ways require finds a module, in order: package.preload, then the host's loader
        var searchers = new Table();
        searchers.Append(Value.FromFunction(static (ref CallContext c) =>
        {
            Value loader = ((Table)((Table)c.L.Globals.GetStr("package").O!).GetStr("preload").O!).Get(c[0]);
            return loader.IsNil ? c.Return(Value.FromString("no field package.preload['" + c.CheckString(0) + "']")) : c.Return(loader, Value.FromString(":preload:"));
        }, "preload searcher"));
        searchers.Append(Value.FromFunction(static (ref CallContext c) =>
        {
            string name = c.CheckString(0);
            LuaState state = c.L;
            if (state.ModuleLoader == null) return c.Return(Value.FromString("no module loader configured"));
            return c.Return(Value.FromFunction((ref CallContext inner) => inner.Return(state.ModuleLoader(state, name)), "module loader"), Value.FromString(name));
        }, "module searcher"));
        package.SetStr("searchers", searchers);
        package.SetStr("loaders", searchers);
        g.SetStr("package", package);
        L.LoadedModules.SetStr("_G", g);
        L.LoadedModules.SetStr("package", package);

        var argt = new Table();
        for (int i = 0; i < L.ScriptArgs.Length; i++) argt.SetInt(i, Value.FromText(L.ScriptArgs[i]));
        g.SetStr("arg", argt);
    }

    private static int Print(ref CallContext c)
    {
        if (c.Count == 1)
        {
            c.L.WriteOut(c.L.ToStringMeta(c[0]) + "\n");
            return 0;
        }
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < c.Count; i++)
        {
            if (i > 0) sb.Append('\t');
            sb.Append(c.L.ToStringMeta(c[i]));
        }
        sb.Append('\n');
        c.L.WriteOut(sb.ToString());
        return 0;
    }

    private static int Type(ref CallContext c)
    {
        c.CheckAny(0);
        return c.Return(c[0].TypeName);
    }

    private static int ToString(ref CallContext c)
    {
        c.CheckAny(0);
        return c.Return(c.L.ToStringMeta(c[0]));
    }

    private static int ToNumber(ref CallContext c)
    {
        if (c.IsNoneOrNil(1))
        {
            Value v = c[0];
            if (v.IsNumber) return c.Return(v);
            if (v.IsString && LuaNumber.TryParse(v.AsString, out Value n)) return c.Return(n);
            c.CheckAny(0);
            return c.Return(Value.Nil);
        }
        long b = c.CheckInt(1);
        if (!c[0].IsString) throw c.TypeError(0, "string");
        if (b < 2 || b > 36) throw c.ArgError(1, "base out of range");
        string s = c[0].AsString.Trim(' ', '\t', '\n', '\r', '\f', '\v');
        bool neg = false;
        int p = 0;
        if (s.Length > 0 && (s[0] == '-' || s[0] == '+')) { neg = s[0] == '-'; p = 1; }
        if (p >= s.Length) return c.Return(Value.Nil);
        long acc = 0;
        for (; p < s.Length; p++)
        {
            char ch = s[p];
            int d = char.IsAsciiDigit(ch) ? ch - '0' : char.IsAsciiLetter(ch) ? char.ToLowerInvariant(ch) - 'a' + 10 : 99;
            if (d >= b) return c.Return(Value.Nil);
            acc = unchecked(acc * b + d);
        }
        return c.Return(Value.FromInt(neg ? unchecked(-acc) : acc));
    }

    private static readonly Value IPairsIter = Value.FromFunction(new NativeFunction("ipairs_iter", static (ref CallContext c) =>
    {
        long i = c[1].I + 1;
        Value t = c[0];
        Value v = t.Kind == ValueKind.Table && t.AsTable.Metatable == null ? t.AsTable.GetInt(i) : c.L.Index(t, Value.FromInt(i));
        return v.IsNil ? c.Return(Value.Nil) : c.Return(Value.FromInt(i), v);
    }));

    private static int IPairs(ref CallContext c)
    {
        c.CheckAny(0);
        return c.Return(IPairsIter, c[0], Value.FromInt(0));
    }

    private static readonly Value NextFn = Value.FromFunction(new NativeFunction("next", Next));

    private static int Next(ref CallContext c)
    {
        Table t = c.CheckTable(0);
        Value key = c[1];
        while (t.Next(key, out Value k, out Value v))
        {
            // the hidden members of an object are not shown to code that may not use them
            if (t.Guarded && LuaState.IsHiddenFrom(t, k, LuaState.RunningClass(c.Thread), out _)) { key = k; continue; }
            return c.Return(k, v);
        }
        return c.Return(Value.Nil);
    }

    private static int Pairs(ref CallContext c)
    {
        c.CheckAny(0);
        Value h = c.L.GetMetamethod(c[0], Meta.Pairs);
        if (!h.IsNil)
        {
            Value[] r = c.L.Call(h, c[0]);
            return c.Return(r.Length > 0 ? r[0] : Value.Nil, r.Length > 1 ? r[1] : Value.Nil, r.Length > 2 ? r[2] : Value.Nil);
        }
        c.CheckTable(0);
        return c.Return(NextFn, c[0], Value.Nil);
    }

    private static int Select(ref CallContext c)
    {
        Value n = c[0];
        if (n.IsString && n.AsString == "#") return c.Return(Value.FromInt(c.Count - 1));
        long i = c.CheckInt(0);
        if (i < 0) i = c.Count + i;
        else if (i > c.Count) i = c.Count;
        if (i < 1) throw c.ArgError(0, "index out of range");
        return c.ReturnArgsFrom((int)i);
    }

    private static int RawGet(ref CallContext c)
    {
        Table t = c.CheckTable(0);
        if (t.Guarded) LuaState.GuardCheck(t, c[1], LuaState.RunningClass(c.Thread));
        return c.Return(t.Get(c[1]));
    }

    private static int RawSet(ref CallContext c)
    {
        Table target = c.CheckTable(0);
        if (target.Guarded) LuaState.GuardCheck(target, c[1], LuaState.RunningClass(c.Thread));
        target.Set(c[1], c[2]);
        return c.Return(c[0]);
    }

    private static int RawEqual(ref CallContext c) => c.Return(c[0].RawEquals(c[1]));

    private static int RawLen(ref CallContext c)
    {
        Value v = c[0];
        if (v.IsTable) return c.Return(Value.FromInt(v.AsTable.Length));
        if (v.IsString) return c.Return(Value.FromInt(v.AsString.Length));
        throw c.ArgError(0, "table or string expected");
    }

    private static int SetMetatable(ref CallContext c)
    {
        Table t = c.CheckTable(0);
        t.CheckMutable();
        Value mt = c[1];
        if (!mt.IsNil && !mt.IsTable) throw c.TypeError(1, "nil or table");
        if (t.Metatable != null && !t.Metatable.GetStr(Meta.Metatable).IsNil)
            throw new LuaException("cannot change a protected metatable");
        if (t.Guarded) throw new LuaException("cannot change the metatable of an object whose class has private or protected members");
        t.Metatable = mt.IsNil ? null : mt.AsTable;
        if (t.Metatable?.Class is { Hidden: not null } guard) { t.Guarded = true; t.GuardTag = guard.FastTag; }
        if (t.Metatable != null) c.L.Gc.OnSetMetatable(t, t.Metatable); // weak tables, __gc
        c.L.GcSafePoint();
        return c.Return(c[0]);
    }

    private static int GetMetatable(ref CallContext c)
    {
        Table? mt = c.L.GetMetatable(c[0]);
        if (mt == null) return c.Return(Value.Nil);
        Value prot = mt.GetStr(Meta.Metatable);
        return c.Return(prot.IsNil ? Value.FromTable(mt) : prot);
    }

    private static int Assert(ref CallContext c)
    {
        if (c[0].IsTruthy) return c.ReturnArgsFrom(0);
        c.CheckAny(0);
        Value msg = c.Count > 1 ? c[1] : Value.FromString("assertion failed!");
        if (msg.IsString) msg = Value.FromString(c.L.Where(1) + msg.AsString);
        var aex = new LuaException(msg, true);
        aex.CaptureTraceback(c.Thread);
        throw aex;
    }

    // The companion issue follows a nil-only protocol, independent of Lua truthiness.
    // Keep its original value (including bytes/object identity) and capture the throw site.
    private static int Unwrap(ref CallContext c)
    {
        c.CheckAny(0);
        if (c[1].IsNil) return c.Return(c[0]);
        var ex = new LuaException(c[1], true);
        ex.CaptureTraceback(c.Thread);
        throw ex;
    }

    private static int Error(ref CallContext c)
    {
        Value v = c[0];
        long level = c.OptInt(1, 1);
        if (v.IsString && level > 0)
            v = Value.FromString(c.L.Where((int)level) + v.AsString);
        var ex = new LuaException(v, true);
        ex.CaptureTraceback(c.Thread);
        throw ex;
    }

    // Fallback path for pcall (the interpreter handles pcall of Lua functions inline,
    // which also lets coroutines yield across it).
    private static int PCall(ref CallContext c)
    {
        c.CheckAny(0);
        var th = c.Thread;
        int frames = th.FrameCount;
        try
        {
            Value[] r = c.L.Call(c[0], c.ArgsFrom(1));
            var res = new Value[r.Length + 1];
            res[0] = Value.True;
            r.CopyTo(res, 1);
            return c.Return(res);
        }
        catch (LuaException e)
        {
            th.FrameCount = frames;
            return c.Return(Value.False, e.Value);
        }
    }

    private static int XPCall(ref CallContext c)
    {
        c.CheckFunction(1);
        var th = c.Thread;
        int frames = th.FrameCount;
        Value handler = c[1];
        try
        {
            Value[] r = c.L.Call(c[0], c.ArgsFrom(2));
            var res = new Value[r.Length + 1];
            res[0] = Value.True;
            r.CopyTo(res, 1);
            return c.Return(res);
        }
        catch (LuaException e)
        {
            th.FrameCount = frames;
            c.L.PendingTraceback = e.Traceback;
            try
            {
                Value hv;
                try { hv = c.L.Call1(handler, e.Value); }
                catch (LuaException he) { hv = he.Value; }
                return c.Return(Value.False, hv);
            }
            finally { c.L.PendingTraceback = null; }
        }
    }

    private static int CollectGarbage(ref CallContext c)
    {
        string opt = c.OptString(0, "collect");
        LuaState L = c.L;
        switch (opt)
        {
            case "collect":
                L.CollectGarbage();
                return c.Return(Value.FromInt(0));
            case "step":
                // There is no incremental collector to advance: a step is a complete cycle.
                L.CollectGarbage();
                return c.Return(Value.True);
            case "count":
                L.GcSafePoint();
                return c.Return(Value.FromFloat(GC.GetTotalMemory(false) / 1024.0));
            case "stop":
                L.Gc.Running = false;
                return c.Return(Value.FromInt(0));
            case "restart":
                L.Gc.Running = true;
                L.GcSafePoint();
                return c.Return(Value.FromInt(0));
            case "isrunning":
                return c.Return(L.Gc.Running);
            case "incremental":
            case "generational":
                {
                    string prev = L.Gc.Mode;
                    L.Gc.Mode = opt;
                    return c.Return(prev);
                }
            case "setpause":
                return c.Return(Value.FromInt(200));
            case "setstepmul":
                return c.Return(Value.FromInt(100));
            default:
                throw c.ArgError(0, $"invalid option '{opt}'");
        }
    }

    private static int Require(ref CallContext c)
    {
        string name = c.CheckString(0);
        Value loaded = c.L.LoadedModules.GetStr(name);
        if (!loaded.IsNil) return c.Return(loaded);
        // package.preload[name], if a program put a loader there, comes first
        if (c.L.Globals.GetStr("package").O is Table pkg && pkg.GetStr("preload").O is Table preloadTable
            && preloadTable.GetStr(name) is { IsNil: false } preloader)
        {
            Value made = c.L.Call1(preloader, c[0], Value.FromString(":preload:"));
            Value registered = c.L.LoadedModules.GetStr(name);
            if (!registered.IsNil) return c.Return(registered);
            if (made.IsNil) made = Value.True;
            c.L.LoadedModules.SetStr(name, made);
            return c.Return(made);
        }
        if (c.L.ModuleLoader == null) throw new LuaException($"module '{name}' not found: no module loader configured");
        Value v = c.L.ModuleLoader(c.L, name);
        // A module may register itself in package.loaded; otherwise store what it returned.
        Value again = c.L.LoadedModules.GetStr(name);
        if (!again.IsNil)
        {
            if (c.L.IsHermetic) CollectionsLib.FreezeValue(again, new HashSet<object>(ReferenceEqualityComparer.Instance));
            return c.Return(again);
        }
        if (v.IsNil) v = Value.True;
        if (c.L.IsHermetic) CollectionsLib.FreezeValue(v, new HashSet<object>(ReferenceEqualityComparer.Instance));
        c.L.LoadedModules.SetStr(name, v);
        return c.Return(v);
    }

    private static int Load(ref CallContext c)
    {
        Value chunk = c[0];
        string src;
        if (chunk.IsString) src = chunk.AsString;
        else if (chunk.IsFunction)
        {
            var sb = new System.Text.StringBuilder();
            while (true)
            {
                Value piece = c.L.Call1(chunk);
                if (piece.IsNil || (piece.IsString && piece.AsString.Length == 0)) break;
                if (!piece.IsString) throw new LuaException("reader function must return a string");
                sb.Append(piece.AsString);
            }
            src = sb.ToString();
        }
        else throw c.TypeError(0, "string");
        string chunkName = c.OptString(1, src);
        if (c.L.ChunkLoader == null) return c.Return(Value.Nil, "load is not available: no chunk loader configured");
        try
        {
            return c.Return(c.L.ChunkLoader(c.L, src, chunkName));
        }
        catch (LuaException e)
        {
            return c.Return(Value.Nil, e.Value);
        }
    }

    private static int DoFile(ref CallContext c)
    {
        string path = c.CheckString(0);
        if (c.L.ChunkLoader == null) throw new LuaException("dofile is not available: no chunk loader configured");
        string decoded = LuaText.Decode(path);
        if (!c.L.AuthorizeFile(decoded, write: false, out string canonical))
            throw new LuaException("cannot open " + path + ": permission denied by host file policy");
        string src;
        try { src = LuaText.ReadFile(canonical); }
        catch (IOException e) { throw new LuaException("cannot open " + path + ": " + e.Message); }
        Value fn = c.L.ChunkLoader(c.L, src, "@" + path);
        return c.Return(c.L.Call(fn));
    }

    // warn(msg, ...): the pieces are written as one line to standard error. A single piece that
    // starts with '@' is a control message: "@off" silences warnings, "@on" turns them on again;
    // others are ignored (as in Lua 5.4, where warnings start off; here they start on).
    private static int Warn(ref CallContext c)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < c.Count; i++) sb.Append(c.CheckString(i));
        if (c.Count == 1 && sb.Length > 0 && sb[0] == '@')
        {
            string control = sb.ToString();
            if (control == "@off") c.L.WarningsOff = true;
            else if (control == "@on") c.L.WarningsOff = false;
            return 0;
        }
        if (c.L.WarningsOff) return 0;
        c.L.WriteErr("Lua warning: " + sb + "\n");
        return 0;
    }

    // package.searchpath(name, path [, sep [, rep]]): the first file that exists among the
    // templates of path with '?' replaced by name (its sep replaced by rep), or nil and a
    // message listing what was tried.
    private static int SearchPath(ref CallContext c)
    {
        string name = c.CheckText(0), path = c.CheckText(1);
        string sep = c.IsNoneOrNil(2) ? "." : c.CheckText(2), rep = c.IsNoneOrNil(3) ? "/" : c.CheckText(3);
        if (sep.Length > 0) name = name.Replace(sep, rep);
        var tried = new System.Text.StringBuilder();
        foreach (string template in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string file = template.Replace("?", name);
            if (c.L.AuthorizeFile(file, write: false, out string canonical) && File.Exists(canonical))
                return c.Return(Value.FromText(file));
            tried.Append(tried.Length == 0 ? "no file '" : "\n\tno file '").Append(file).Append('\'');
        }
        return c.Return(Value.Nil, Value.FromText(tried.ToString()));
    }
}

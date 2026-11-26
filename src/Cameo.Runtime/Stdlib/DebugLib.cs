using Cameo.Runtime.Vm;

namespace Cameo.Runtime.Stdlib;

/// <summary>A minimal debug library: traceback, getinfo, (get|set)metatable and inert stubs.</summary>
public static class DebugLib
{
    public static void Open(LuaState L)
    {
        var d = new Table();
        L.Register(d, "traceback", Traceback);
        L.Register(d, "getinfo", GetInfo);
        L.Register(d, "getmetatable", static (ref CallContext c) =>
        {
            Table? mt = c.L.GetMetatable(c[0]);
            return c.Return(mt == null ? Value.Nil : Value.FromTable(mt));
        });
        L.Register(d, "setmetatable", static (ref CallContext c) =>
        {
            Value v = c[0], mt = c[1];
            if (!mt.IsNil && !mt.IsTable) throw c.TypeError(1, "nil or table");
            Table? m = mt.IsNil ? null : mt.AsTable;
            if (v.IsTable)
            {
                if (v.AsTable.Guarded) throw new LuaException("cannot change the metatable of an object whose class has private or protected members");
                v.AsTable.Metatable = m; c.L.Gc.OnSetMetatable(v.AsTable, m);
                if (m?.Class is { Hidden: not null } guard) { v.AsTable.Guarded = true; v.AsTable.GuardTag = guard.FastTag; }
            }
            else if (v.Kind == ValueKind.Userdata && v.O is IUserdata u) { u.Metatable = m; c.L.Gc.OnSetMetatable(u, m); }
            else if (!v.IsString) throw new LuaException("cannot set a metatable for a " + v.TypeName + " value");
            return c.Return(v);
        });
        NativeFn nilStub = static (ref CallContext c) => c.Return(Value.Nil);
        L.Register(d, "sethook", static (ref CallContext c) => 0);
        L.Register(d, "gethook", nilStub);
        L.Register(d, "getlocal", nilStub);
        // upvalues of a Teal function: name and value by position
        L.Register(d, "getupvalue", static (ref CallContext c) =>
        {
            c.CheckFunction(0);
            long n = c.CheckInt(1);
            if (c[0].O is not Closure cl || n < 1 || n > cl.Upvals.Length) return c.Return();
            return c.Return(Value.FromString(cl.Proto.Upvals[n - 1].Name), cl.Upvals[n - 1].Value);
        });
        L.Register(d, "upvalueid", static (ref CallContext c) =>
        {
            c.CheckFunction(0);
            long n = c.CheckInt(1);
            if (c[0].O is not Closure cl || n < 1 || n > cl.Upvals.Length) return c.Return(Value.Nil);
            return c.Return(Value.FromUserdata(cl.Upvals[n - 1]));       // the shared cell itself: equal ids mean a shared upvalue
        });
        L.Register(d, "upvaluejoin", static (ref CallContext c) =>
        {
            c.CheckFunction(0); c.CheckFunction(2);
            long n1 = c.CheckInt(1), n2 = c.CheckInt(3);
            if (c[0].O is not Closure f1 || n1 < 1 || n1 > f1.Upvals.Length) throw c.ArgError(1, "invalid upvalue index");
            if (c[2].O is not Closure f2 || n2 < 1 || n2 > f2.Upvals.Length) throw c.ArgError(3, "invalid upvalue index");
            f1.Upvals[n1 - 1] = f2.Upvals[n2 - 1];
            return c.Return();
        });
        // userdata carries no user values here
        L.Register(d, "getuservalue", static (ref CallContext c) => c.Return(Value.Nil, Value.False));
        L.Register(d, "setuservalue", nilStub);
        L.Register(d, "setcstacklimit", static (ref CallContext c) => c.Return(Value.FromInt(0)));
        // debug.debug(): runs lines from standard input until a line "cont" (or end of input)
        L.Register(d, "debug", static (ref CallContext c) =>
        {
            while (true)
            {
                c.L.WriteErr("lua_debug> ");
                var line = new System.Text.StringBuilder();
                int ch;
                while ((ch = c.L.ReadIn()) >= 0 && ch != '\n') line.Append((char)ch);
                string text = line.ToString().TrimEnd('\r');
                if ((ch < 0 && text.Length == 0) || text == "cont") return c.Return();
                try
                {
                    Value loader = c.L.Globals.GetStr("load");
                    Value[] made = c.L.Call(loader, Value.FromString(text), Value.FromString("=(debug command)"));
                    if (made.Length > 0 && made[0].IsFunction) c.L.Call(made[0]);
                    else c.L.WriteErr((made.Length > 1 ? made[1].ToString() : "syntax error") + "\n");
                }
                catch (LuaException e) { c.L.WriteErr(e.Message + "\n"); }
            }
        });
        L.Register(d, "setlocal", nilStub);
        L.Register(d, "setupvalue", static (ref CallContext c) =>
        {
            c.CheckFunction(0);
            long n = c.CheckInt(1);
            if (c[0].O is not Closure cl || n < 1 || n > cl.Upvals.Length) return c.Return();
            cl.Upvals[n - 1].Value = c[2];
            return c.Return(Value.FromString(cl.Proto.Upvals[n - 1].Name));
        });
        L.Register(d, "getregistry", static (ref CallContext c) => c.Return(Value.FromTable(new Table())));
        L.Globals.SetStr("debug", d);
        L.LoadedModules.SetStr("debug", d);
    }

    private static int Traceback(ref CallContext c)
    {
        int a = c[0].Kind == ValueKind.Thread ? 1 : 0;
        Value msg = c[a];
        if (!msg.IsNil && !msg.IsString && !msg.IsNumber) return c.Return(msg);
        string tb = c.L.PendingTraceback ?? c.L.Traceback();
        if (msg.IsNil) return c.Return(tb);
        return c.Return((msg.IsString ? msg.AsString : msg.ToString()) + "\n" + tb);
    }

    private static int GetInfo(ref CallContext c)
    {
        int a = c[0].Kind == ValueKind.Thread ? 1 : 0;
        Value what = c[a];
        var t = new Table(0, 8);
        if (what.IsFunction)
        {
            bool native = what.O is NativeFunction;
            t.SetStr("func", what);
            t.SetStr("what", native ? "C" : "Lua");
            t.SetStr("source", native ? "=[C]" : "=?");
            t.SetStr("short_src", native ? "[C]" : "?");
            t.SetStr("currentline", Value.FromInt(-1));
            t.SetStr("name", what.AsFunction.Name);
            return c.Return(Value.FromTable(t));
        }
        long level = c.CheckInt(a);
        if (level < 0) throw c.ArgError(a, "level out of range");
        if (level == 0)
        {
            t.SetStr("what", "C");
            t.SetStr("source", "=[C]");
            t.SetStr("short_src", "[C]");
            t.SetStr("currentline", Value.FromInt(-1));
            return c.Return(Value.FromTable(t));
        }
        // "chunk:line: " of the Lua function at this level, or "" when there is none
        string where = level > int.MaxValue ? "" : c.L.Where((int)level);
        if (where.Length == 0) return c.Return(Value.Nil);
        string body = where.Substring(0, where.Length - 2);
        int colon = body.LastIndexOf(':');
        string src = colon >= 0 ? body.Substring(0, colon) : body;
        long line = colon >= 0 && long.TryParse(body.AsSpan(colon + 1), out long ln) ? ln : -1;
        t.SetStr("what", "Lua");
        t.SetStr("source", "@" + src);
        t.SetStr("short_src", src);
        t.SetStr("currentline", Value.FromInt(line));
        return c.Return(Value.FromTable(t));
    }
}

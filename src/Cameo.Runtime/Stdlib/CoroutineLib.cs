using Cameo.Runtime.Vm;

namespace Cameo.Runtime.Stdlib;

public static class CoroutineLib
{
    public static void Open(LuaState L)
    {
        var co = new Table();
        L.Register(co, "create", Create);
        L.Register(co, "resume", Resume);
        L.Register(co, "yield", Yield);
        L.Register(co, "status", Status);
        L.Register(co, "wrap", Wrap);
        L.Register(co, "running", Running);
        L.Register(co, "isyieldable", IsYieldable);
        L.Register(co, "close", Close);
        L.Globals.SetStr("coroutine", co);
        L.LoadedModules.SetStr("coroutine", co);
    }

    private static LuaThread CheckThread(ref CallContext c, int i)
    {
        if (c[i].Kind == ValueKind.Thread) return c[i].AsThread;
        throw c.TypeError(i, "coroutine");
    }

    private static int Create(ref CallContext c)
    {
        Value f = c.CheckFunction(0);
        return c.Return(Value.FromThread(c.L.NewThread(f)));
    }

    private static int Resume(ref CallContext c)
    {
        LuaThread co = CheckThread(ref c, 0);
        bool ok = c.L.Resume(co, c.ArgsFrom(1), out Value[] results);
        var r = new Value[results.Length + 1];
        r[0] = ok ? Value.True : Value.False;
        results.CopyTo(r, 1);
        return c.Return(r);
    }

    private static int Yield(ref CallContext c) => c.YieldAll();

    private static int Status(ref CallContext c)
    {
        LuaThread co = CheckThread(ref c, 0);
        return c.Return(co.Status switch
        {
            CoStatus.Suspended => "suspended",
            CoStatus.Running => "running",
            CoStatus.Normal => "normal",
            _ => "dead",
        });
    }

    private static int Wrap(ref CallContext c)
    {
        Value f = c.CheckFunction(0);
        LuaThread co = c.L.NewThread(f);
        return c.Return(Value.FromFunction(new NativeFunction("wrap", (ref CallContext cc) =>
        {
            if (cc.L.Resume(co, cc.ArgsFrom(0), out Value[] results)) return cc.Return(results);
            Value err = results[0];
            throw new LuaException(err, true);
        })));
    }

    private static int Running(ref CallContext c) =>
        c.Return(Value.FromThread(c.L.CurrentThread), c.L.CurrentThread.IsMain);

    private static int IsYieldable(ref CallContext c) =>
        c.Return(!c.L.CurrentThread.IsMain && c.L.CurrentThread.ExecDepth == 1);

    private static int Close(ref CallContext c)
    {
        LuaThread co = CheckThread(ref c, 0);
        if (co.Status == CoStatus.Suspended || co.Status == CoStatus.Dead)
        {
            co.Status = CoStatus.Dead;
            co.FrameCount = 0;
            // run pending __close handlers of the abandoned coroutine
            while (co.TbcCount > 0)
            {
                Value v = co.Stack[co.Tbc[--co.TbcCount]];
                if (v.Kind == ValueKind.Box) v = ((Box)v.O!).Value;
                Value h = c.L.GetMetamethod(v, "__close");
                if (!h.IsNil) c.L.Call1(h, v, Value.Nil);
            }
            // a coroutine that ended in an error: false and that error, once (as Lua 5.4 does)
            if (co.DiedWithError)
            {
                Value error = co.DeathError;
                co.DiedWithError = false;
                co.DeathError = default;
                return c.Return(Value.False, error);
            }
            return c.Return(Value.True);
        }
        throw new LuaException("cannot close a " + (co.Status == CoStatus.Running ? "running" : "normal") + " coroutine");
    }
}

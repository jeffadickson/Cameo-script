using Cameo.Runtime;
using Cameo.Runtime.Vm;

namespace Cameo.Jit;

/// <summary>
/// <c>jit</c>: the IL target, on request. <c>jit.compile(f)</c> returns a function that does
/// what <c>f</c> does, compiled to .NET IL — or nil and a <c>JitError</c> when <c>f</c> uses
/// something the compiler does not translate. Nothing is compiled unless a program asks.
/// </summary>
public static class JitLibrary
{
    /// <summary>Adds the <c>jit</c> library to an engine.</summary>
    public static void Enable(ScriptEngine engine)
    {
        LuaState L = engine.State;
        var lib = new Table();
        L.Register(lib, "compile", static (ref CallContext c) =>
        {
            if (TryCompile(c[0], out Value compiled, out JitError? error)) return c.Return(compiled, Value.Nil);
            return c.Return(Value.Nil, ErrorValue(c.L, error!));
        });
        L.Register(lib, "supports", static (ref CallContext c) =>
        {
            if (c[0].O is not Closure cl) return c.Return(Value.False, Value.FromText("not a Teal function"));
            string? why = IlCompiler.Unsupported(cl.Proto);
            return why is null ? c.Return(Value.True, Value.Nil) : c.Return(Value.False, Value.FromText(why));
        });
        L.Register(lib, "auto", static (ref CallContext c) =>
        {
            if (c.IsNoneOrNil(0)) return c.Return(Value.FromInt(c.L.JitTierThreshold));
            long threshold = c.CheckInt(0);
            if (threshold < 0 || threshold > int.MaxValue) throw c.ArgError(0, "threshold must be a non-negative 32-bit integer");
            c.L.JitTierThreshold = (int)threshold;
            return c.Return(Value.FromInt(c.L.JitTierThreshold));
        });
        L.AutoTierCompiler = static closure =>
        {
            Proto p = closure.Proto;
            CompiledBody? body = p.TierBody as CompiledBody;
            if (body == null)
            {
                if (p.TierAttempted) return null;
                p.TierAttempted = true;
                if (!IlCompiler.TryCompile(p, out body, out _) || body == null) return null;
                p.TierBody = body;
            }
            var binding = new CompiledFunction(body, closure);
            return new NativeFunction(p.Name, binding.Invoke);
        };
        lib.SetStr("available", Value.FromBool(System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported));
        L.Globals.SetStr("jit", lib);
        L.LoadedModules.SetStr("jit", lib);
    }

    /// <summary>Compiles a Teal function value; the result is a function that can be called in its place.</summary>
    public static bool TryCompile(in Value function, out Value compiled, out JitError? error)
    {
        compiled = default;
        if (function.O is not Closure cl)
        {
            error = new JitError(JitErrorKind.NotAFunction,
                function.IsFunction ? "only functions written in Teal can be compiled, not built-in ones" : $"a function was expected, not a {LuaState.TypeNameOf(function)}");
            return false;
        }
        if (!IlCompiler.TryCompile(cl.Proto, out CompiledBody? body, out error)) return false;
        var binding = new CompiledFunction(body!, cl);
        compiled = Value.FromFunction(binding.Invoke, cl.Proto.Name);
        return true;
    }

    private static Value ErrorValue(LuaState L, JitError e)
    {
        var t = new Table(0, 2);
        t.SetStr("kind", e.Kind switch
        {
            JitErrorKind.Unsupported => "unsupported",
            JitErrorKind.Unavailable => "unavailable",
            _ => "not_a_function",
        });
        t.SetStr("message", LuaText.Encode(e.Message));
        t.Metatable = L.MetaFor("JitError");
        return Value.FromTable(t);
    }
}

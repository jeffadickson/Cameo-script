using Cameo.Runtime.Vm;

namespace Cameo.Jit;

/// <summary>A generated body runs until it returns or requests a call. Negative results
/// are internal suspension codes, never VM native-function return codes.</summary>
internal delegate int CompiledStep(LuaState state, LuaThread thread, int @base, int count,
    Closure closure, Value[] constants, CompiledFrame frame);

/// <summary>Continuation owned by one dispatcher activation; never shared between threads.</summary>
internal sealed class CompiledFrame(CompiledProgram program, Closure closure, int @base, int count)
{
    internal readonly CompiledProgram Program = program;
    internal readonly Closure Closure = closure;
    internal readonly int Base = @base, Count = count;
    // Public fields are accessed by generated IL. The containing type remains internal.
    public int Resume;
    public object?[] Saved = [];
    public Value Function;
    public Value[] Arguments = [], Results = [];
    public Value[] Varargs = [];
    public List<(int Reg, Value Val)>? Tbc;
    public int Line = 0; // assigned by generated IL before each suspended call
    internal bool Tail;
    internal CompiledStep? ActiveStep;

    internal int Slots => Program.Slots;

    public static void InitVarargs(LuaThread thread, int @base, int count, int numParams, CompiledFrame frame)
    {
        int extra = count - numParams;
        if (extra <= 0) { frame.Varargs = []; return; }
        var va = new Value[extra];
        Array.Copy(thread.Stack, @base + numParams, va, 0, extra);
        frame.Varargs = va;
    }

    public static Value[] TakeVarargs(CompiledFrame frame, int want)
    {
        Value[] va = frame.Varargs;
        if (want < 0) return va;
        var outArr = new Value[want];
        int copy = Math.Min(want, va.Length);
        if (copy > 0) Array.Copy(va, 0, outArr, 0, copy);
        return outArr;
    }

    public static void Request(Value function, Value[] arguments, CompiledFrame frame, int resume, bool tail)
    {
        frame.Function = function;
        frame.Arguments = arguments;
        frame.Resume = resume;
        frame.Tail = tail;
    }

    public static object?[] SaveArea(CompiledFrame frame, int count)
    {
        if (frame.Saved.Length == 0) frame.Saved = new object?[count];
        return frame.Saved;
    }

    public static Value[] TakeResults(CompiledFrame frame, int want)
    {
        Value[] results = frame.Results;
        frame.Results = [];
        if (want >= 0 && results.Length < want) Array.Resize(ref results, want);
        return results;
    }
}

/// <summary>Binding of a compiled program to the original closure, preserving upvalues.
/// Delegate target identity lets the dispatcher recognize only actual compiled values.</summary>
internal sealed class CompiledFunction(CompiledBody body, Closure closure)
{
    internal readonly CompiledBody Body = body;
    internal readonly Closure Closure = closure;
    public int Invoke(ref CallContext context) => Body(context.L, context.Thread, context.Base,
        context.Count, Closure, Closure.Proto.K);
}

/// <summary>Runs resumable IL using heap frames. Calls to ordinary native/interpreted
/// functions still use LuaState.Call, so callbacks and yield boundaries retain VM semantics.</summary>
internal sealed class CompiledProgram(CompiledStep step, CompiledStep? generic, ValueKind?[] guards, int slots)
{
    internal int Run(LuaState state, LuaThread thread, int @base, int count, Closure closure, Value[] constants)
    {
        int savedTop = thread.CallTop;
        var frames = new List<CompiledFrame>();
        var current = new CompiledFrame(this, closure, @base, count);
        int bottom = Math.Max(savedTop, @base + count);
        int used = 0;
        try
        {
            while (true)
            {
                JitOps.Poll(state);
                // Budget counts interpreter storage, arguments and compiled register/snapshot
                // slots. Check before publishing a frame; arithmetic is widened before adding.
                if ((long)bottom + used + current.Slots > state.Limits.MaxStackSlots)
                    throw new LuaException("stack overflow (compiled frame budget)");
                thread.CallTop = checked(bottom + used + current.Slots);
                current.ActiveStep ??= current.Program.Select(thread, current.Base, current.Count);
                int result = current.ActiveStep(state, thread, current.Base, current.Count,
                    current.Closure, current.Closure.Proto.K, current);
                if (result < 0)
                {
                    Value function = current.Function;
                    Value[] arguments = current.Arguments;
                    current.Function = default;
                    current.Arguments = [];
                    if (function.O is NativeFunction { Fn.Target: CompiledFunction target }
                        && target.Body.Target is CompiledProgram next)
                    {
                        var child = new CompiledFrame(next, target.Closure,
                            current.Tail ? current.Base : thread.CallTop, arguments.Length);
                        if (!current.Tail)
                        {
                            frames.Add(current);
                            used = checked(used + current.Slots);
                        }
                        // Tail replacement neither grows the continuation stack nor keeps
                        // the old closure/snapshots alive. Arguments were gathered first.
                        current = child;
                        long needed = (long)bottom + used + current.Slots + arguments.Length;
                        if (needed > state.Limits.MaxStackSlots)
                            throw new LuaException("stack overflow (compiled frame budget)");
                        thread.EnsureStack(checked(current.Base + arguments.Length));
                        arguments.CopyTo(thread.Stack, current.Base);
                        continue;
                    }
                    try { current.Results = state.Call(function, arguments); }
                    catch (Exception error) { throw JitOps.Locate(error, current.Closure.Proto.ChunkName, current.Line); }
                    if (!current.Tail) continue;
                    result = JitOps.RetN(current.Results, thread, current.Base);
                }
                if (frames.Count == 0)
                {
                    if (current.Base != @base) Array.Copy(thread.Stack, current.Base, thread.Stack, @base, result);
                    return result;
                }
                var values = new Value[result];
                Array.Copy(thread.Stack, current.Base, values, 0, result);
                current = frames[^1];
                frames.RemoveAt(frames.Count - 1);
                used -= current.Slots;
                current.Results = values;
            }
        }
        catch (Exception err)
        {
            Value errVal = err is LuaException le ? le.Value : Value.FromText(err.Message);
            Exception active = err;
            try { JitOps.CloseFrom(state, current, 0, errVal); }
            catch (Exception closeErr) { active = closeErr; errVal = closeErr is LuaException cle ? cle.Value : Value.FromText(closeErr.Message); }
            for (int fi = frames.Count - 1; fi >= 0; fi--)
            {
                try { JitOps.CloseFrom(state, frames[fi], 0, errVal); }
                catch (Exception closeErr) { active = closeErr; errVal = closeErr is LuaException cle ? cle.Value : Value.FromText(closeErr.Message); }
            }
            if (!ReferenceEquals(active, err)) throw active;
            throw;
        }
        finally { thread.CallTop = savedTop; }
    }

    internal readonly CompiledStep Step = step;
    internal readonly int Slots = slots;

    private CompiledStep Select(LuaThread thread, int @base, int count)
    {
        if (generic is null) return Step;
        for (int i = 0; i < guards.Length; i++)
        {
            ValueKind? hint = guards[i];
            if (hint is not (ValueKind.Integer or ValueKind.Float)) continue;
            ValueKind kind = hint.Value;
            if (i >= count || thread.Stack[@base + i].Kind != kind) return generic;
        }
        return Step;
    }
}

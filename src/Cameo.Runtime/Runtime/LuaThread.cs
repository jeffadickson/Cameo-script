namespace Cameo.Runtime.Vm;

public enum CoStatus { Suspended, Running, Normal, Dead }

internal struct Frame
{
    public Closure Cl;
    public int Base;        // absolute stack index of register 0
    public int PC;          // saved program counter
    public int RetDest;     // absolute stack index where results are delivered
    public int NResults;    // number of results wanted by the caller, -1 = all
    public Value[] Varargs; // extra arguments of a vararg function
    public bool Protected;  // frame was entered through pcall
public bool HasHandler; // protected by xpcall: the message handler is at stack[RetDest + 1]
    public byte Finish;     // 1: result of a comparison metamethod; the caller's instruction is completed on return
    public int TbcBase;     // number of pending to-be-closed variables when the frame was entered
}

/// <summary>A thread of execution (the main thread or a coroutine) with its own value stack.</summary>
public sealed class LuaThread
{
    internal VmLimits Limits = new();
    /// <summary>The error a coroutine ended with, until coroutine.close has reported it.</summary>
    internal Value DeathError;
    internal bool DiedWithError;

    internal Value[] Stack;
    internal Frame[] Frames;
    internal int FrameCount;
    /// <summary>End of an open (multi-value) result/argument list.</summary>
    internal int Top;
    /// <summary>First stack slot that native-initiated calls may use.</summary>
    internal int CallTop;
    /// <summary>Number of interpreter activations currently running on this thread.</summary>
    internal int ExecDepth;
    internal int ResultBase, ResultCount;
    /// <summary>pc (already advanced) of the instruction being executed when an error may be raised.</summary>
    internal int SavedPC;
    /// <summary>Extra (vararg) arguments held by live frames; they count against the stack limit.</summary>
    internal int VarargSlots;
    /// <summary>Stack indices of pending to-be-closed variables, innermost last.</summary>
    internal int[] Tbc = new int[4];
    internal int TbcCount;

    /// <summary>Run by the scheduler as an asynchronous task (it may suspend on a future).</summary>
    internal bool IsTask;
    /// <summary>The future this task is suspended on, set just before it yields.</summary>
    internal LuaFuture? Awaiting;
    /// <summary>Whether the pending wait re-raises a failure (get) or returns it (await).</summary>
    internal bool AwaitThrows;
    /// <summary>An error to raise inside the coroutine as soon as it is resumed.</summary>
    internal bool HasPendingThrow;
    internal Value PendingThrow;

    internal readonly bool IsMain;
    internal Value StartFunction;
    internal bool Started;
    public CoStatus Status { get; internal set; }

    internal LuaThread(bool isMain)
    {
        IsMain = isMain;
        Status = isMain ? CoStatus.Running : CoStatus.Suspended;
        // Coroutines start small (programs may create very many) and grow on demand.
        Stack = new Value[isMain ? 256 : 32];
        Frames = new Frame[isMain ? 16 : 4];
    }

    internal void EnsureStack(int n)
    {
        if (n <= Stack.Length) return;
        if (n > Limits.MaxStackSlots + 4096) throw new LuaException("stack overflow");
        int size = Math.Max(n + 64, Stack.Length * 2);
        Array.Resize(ref Stack, size);
    }
}

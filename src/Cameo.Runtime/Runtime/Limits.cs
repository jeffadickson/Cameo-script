namespace Cameo.Runtime.Vm;

/// <summary>Why a running program was stopped from outside. A closed set.</summary>
public enum AbortReason
{
    /// <summary>The watchdog's time limit passed.</summary>
    Timeout,
    /// <summary>The host asked for cancellation.</summary>
    Cancelled,
    /// <summary>The managed heap grew past the configured limit.</summary>
    MemoryLimit,
    /// <summary>The deterministic bytecode step budget was exhausted.</summary>
    StepLimit,
}

/// <summary>
/// Raised inside the VM when the host aborts a run (<see cref="LuaState.RequestAbort"/>).
/// Deliberately not a <see cref="LuaException"/>: a program cannot catch it with pcall.
/// </summary>
public sealed class LuaAbortException : Exception
{
    public AbortReason Reason { get; }
    public LuaAbortException(AbortReason reason) : base("execution aborted: " + reason) { Reason = reason; }
}

/// <summary>Resource limits of one VM. Exceeding one raises an ordinary Lua error
/// (catchable with pcall), never a crash of the host process.</summary>
public sealed class VmLimits
{
    /// <summary>Value-stack slots per thread/coroutine; bounds recursion of Teal functions
    /// ("stack overflow"). A call uses roughly as many slots as the function has locals and
    /// temporaries, so the default allows on the order of 100,000 nested calls.</summary>
    public int MaxStackSlots = 1_000_000;

    /// <summary>Nested native↔Teal transitions (a native function calling back into Teal,
    /// metamethods written as native code, coroutines resuming coroutines). Each level uses
    /// native stack, so this is kept small, as in PUC Lua (LUAI_MAXCCALLS).</summary>
    public int MaxNativeDepth = 200;

    /// <summary>Longest string, in bytes, that concatenation, string.rep and table.concat may build.</summary>
    public int MaxStringLength = 0x3FFFFFFF;

    /// <summary>Most elements a numeric array (<c>FloatArray</c>) may have: 2²⁷, one gigabyte.</summary>
    public int MaxArrayLength = 1 << 27;

    /// <summary>Parallel workers (<c>Future.parallel</c>) of one VM that run at the same time;
    /// further ones wait for a free slot. Defaults to the number of processors.</summary>
    public int MaxParallelWorkers = Environment.ProcessorCount;
}

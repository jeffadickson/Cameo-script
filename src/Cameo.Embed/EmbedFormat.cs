namespace Cameo.Embed;

/// <summary>
/// Flat 8-byte instruction opcodes executed by the static C++20 <c>Cameo.Embed</c> VM.
/// Values must remain bit-for-bit identical to <c>cameo::embed::Op</c> in
/// <c>embed/cameo_embed/include/cameo_embed_vm.hpp</c>.
/// </summary>
public enum EmbedOpCode : byte
{
    /// <summary>Register copy: R[A] = R[B].</summary>
    Move = 0,
    /// <summary>Load constant: R[A] = K[B].</summary>
    LoadK = 1,
    /// <summary>Clear register range R[A..A+B] to nil.</summary>
    LoadNil = 2,
    /// <summary>Load boolean B!=0 into R[A], skipping next instruction when C!=0.</summary>
    LoadBool = 3,
    /// <summary>Load signed 16-bit integer immediate B into R[A].</summary>
    LoadInt = 4,
    /// <summary>Read captured upvalue box Up[B] into R[A].</summary>
    GetUpval = 5,
    /// <summary>Write RK[A] into captured upvalue box Up[B].</summary>
    SetUpval = 6,
    /// <summary>Allocate a static box cell holding R[A] and store handle in R[A].</summary>
    NewBox = 7,
    /// <summary>Read value from box handle R[B] into R[A].</summary>
    GetBox = 8,
    /// <summary>Write RK[B] into box handle R[A].</summary>
    SetBox = 9,
    /// <summary>Read static global slot B into R[A].</summary>
    GetGlobalSlot = 10,
    /// <summary>Write RK[A] into static global slot B.</summary>
    SetGlobalSlot = 11,
    /// <summary>Load host syscall handle B into R[A].</summary>
    LoadHostFn = 12,
    /// <summary>Load built-in ROM namespace B (math/rtos/axi/dbg/Ptr) into R[A].</summary>
    LoadNamespace = 13,
    /// <summary>Index fixed array/record/namespace R[B] by key RK[C] into R[A].</summary>
    GetTable = 14,
    /// <summary>Index fixed record/namespace/Ptr R[B] by string constant K[C] into R[A].</summary>
    GetField = 15,
    /// <summary>Store RK[C] at integer index RK[B] in fixed array R[A].</summary>
    SetTable = 16,
    /// <summary>Store RK[C] at string field K[B] in fixed record R[A].</summary>
    SetField = 17,
    /// <summary>Allocate a new fixed record/array slot from the static pool into R[A].</summary>
    NewTable = 18,
    /// <summary>Method lookup: R[A+1] = R[B]; R[A] = R[B][K[C]] (supports records, namespaces, and Ptr).</summary>
    Self = 19,
    /// <summary>Addition (integer, optional float, or Ptr + offset): R[A] = RK[B] + RK[C].</summary>
    Add = 20,
    /// <summary>Subtraction (integer, optional float, Ptr - offset, or Ptr - Ptr): R[A] = RK[B] - RK[C].</summary>
    Sub = 21,
    /// <summary>Multiplication: R[A] = RK[B] * RK[C].</summary>
    Mul = 22,
    /// <summary>Floating-point division: R[A] = RK[B] / RK[C].</summary>
    Div = 23,
    /// <summary>Lua floor-modulo: R[A] = RK[B] % RK[C].</summary>
    Mod = 24,
    /// <summary>Exponentiation: R[A] = RK[B] ^ RK[C].</summary>
    Pow = 25,
    /// <summary>Floor division: R[A] = RK[B] // RK[C].</summary>
    IDiv = 26,
    /// <summary>Bitwise AND: R[A] = RK[B] &amp; RK[C].</summary>
    BAnd = 27,
    /// <summary>Bitwise OR: R[A] = RK[B] | RK[C].</summary>
    BOr = 28,
    /// <summary>Bitwise XOR: R[A] = RK[B] ~ RK[C].</summary>
    BXor = 29,
    /// <summary>Bitwise left shift: R[A] = RK[B] &lt;&lt; RK[C].</summary>
    Shl = 30,
    /// <summary>Bitwise right shift: R[A] = RK[B] &gt;&gt; RK[C].</summary>
    Shr = 31,
    /// <summary>Unary negation: R[A] = -R[B].</summary>
    Unm = 32,
    /// <summary>Logical negation: R[A] = not R[B].</summary>
    Not = 33,
    /// <summary>Length operator: R[A] = #R[B].</summary>
    Len = 34,
    /// <summary>Bitwise NOT: R[A] = ~R[B].</summary>
    BNot = 35,
    /// <summary>Unconditional relative jump: pc += B.</summary>
    Jmp = 36,
    /// <summary>Conditional relative jump if R[A] is truthy.</summary>
    JmpIf = 37,
    /// <summary>Conditional relative jump if R[A] is falsy.</summary>
    JmpIfNot = 38,
    /// <summary>Equality test (including Ptr == Ptr); skips next Jmp when result differs from A!=0.</summary>
    Eq = 39,
    /// <summary>Less-than test (including unsigned Ptr &lt; Ptr); skips next Jmp when result differs from A!=0.</summary>
    Lt = 40,
    /// <summary>Less-or-equal test (including unsigned Ptr &lt;= Ptr); skips next Jmp when result differs from A!=0.</summary>
    Le = 41,
    /// <summary>Function call: R[A..A+C-2] = R[A](R[A+1..A+B-1]).</summary>
    Call = 42,
    /// <summary>Proper tail call: return R[A](R[A+1..A+B-1]).</summary>
    TailCall = 43,
    /// <summary>Return R[A..A+B-2] to caller.</summary>
    Return = 44,
    /// <summary>Numeric for-loop preparation; skips loop by B if trip count is zero.</summary>
    ForPrep = 45,
    /// <summary>Numeric for-loop step and back-branch by B.</summary>
    ForLoop = 46,
    /// <summary>Populate consecutive array elements in fixed array R[A] starting at offset C.</summary>
    SetList = 47,
    /// <summary>Instantiate a static closure for global prototype ID B into R[A].</summary>
    Closure = 48,
    /// <summary>Runtime type test (Cameo/Teal <c>is</c>).</summary>
    IsKind = 49,
    /// <summary>Unary math intrinsic C applied to R[B] into R[A].</summary>
    MathFn = 50,
    /// <summary>Load built-in VM function ID B (e.g. global <c>ptr(addr)</c>) into R[A].</summary>
    LoadBuiltin = 51,
}

/// <summary>Constant pool entry kind in a CEMB image.</summary>
public enum EmbedConstKind : byte
{
    /// <summary>Nil constant.</summary>
    Nil = 0,
    /// <summary>Boolean constant (0 or 1).</summary>
    Bool = 1,
    /// <summary>Signed 64-bit integer constant.</summary>
    Int = 2,
    /// <summary>IEEE-754 binary64 floating-point constant.</summary>
    Float = 3,
    /// <summary>ROM string pool index.</summary>
    String = 4,
}

/// <summary>Built-in ROM namespaces resolved without heap or pool allocation.</summary>
public enum EmbedNamespaceId : ushort
{
    /// <summary>Pure <c>math</c> namespace (integer ops always enabled; float ops opt-in).</summary>
    Math = 0,
    /// <summary>NexRT <c>rtos</c> kernel and task/stack/queue inspection namespace.</summary>
    Rtos = 1,
    /// <summary>Explicit <c>axi</c> MMIO / AXI4-Lite &amp; AXI4 burst namespace.</summary>
    Axi = 2,
    /// <summary>Hardware debugging &amp; bitfield <c>dbg</c> namespace.</summary>
    Dbg = 3,
    /// <summary>First-class <c>Ptr</c> static constructor namespace (<c>Ptr.of</c>, <c>Ptr.null</c>).</summary>
    Ptr = 4,
}

/// <summary>Well-known VM builtin IDs loadable directly via <see cref="EmbedOpCode.LoadBuiltin"/>.</summary>
public enum EmbedBuiltinId : ushort
{
    /// <summary>Global constructor <c>ptr(addr) -&gt; Ptr</c>.</summary>
    PtrOf = 200,
}

/// <summary>Capability flags recorded in the CEMB image header.</summary>
[Flags]
public enum EmbedImageFlags : ushort
{
    /// <summary>No optional capabilities required (pure integer).</summary>
    None = 0,
    /// <summary>Program uses floating-point constants or operations.</summary>
    HasFloat = 1 << 0,
    /// <summary>Program allocates or mutates fixed static-pool records or arrays.</summary>
    HasRecordsOrArrays = 1 << 1,
    /// <summary>Program uses <c>math</c> intrinsics or power operations.</summary>
    HasMath = 1 << 2,
    /// <summary>Program uses static-pool closures or boxed captures.</summary>
    HasClosures = 1 << 3,
    /// <summary>Program invokes standard <c>rtos.*</c> kernel/inspection syscalls.</summary>
    HasRtosSyscalls = 1 << 4,
    /// <summary>Program constructs or dereferences first-class <c>Ptr</c> values.</summary>
    HasPointers = 1 << 5,
    /// <summary>Program accesses the <c>axi.*</c> MMIO/bus API.</summary>
    HasAxi = 1 << 6,
    /// <summary>Program performs <c>axi</c> or <c>Ptr</c> memory/MMIO writes.</summary>
    HasMemWrite = 1 << 7,
    /// <summary>Program uses <c>dbg.*</c> debugging helpers.</summary>
    HasDbg = 1 << 8,
}

/// <summary>
/// Well-known host syscall IDs bound to NexRT (<c>jeffadickson/rtos</c>) primitives and debug hooks.
/// Custom host functions registered by the embedder start at <see cref="CustomBase"/>.
/// </summary>
public enum EmbedStandardHostId : ushort
{
    /// <summary><c>rtos.ticks()</c> — current kernel tick count.</summary>
    Ticks = 0,
    /// <summary><c>rtos.sleep(ticks)</c> — sleep for relative ticks.</summary>
    Sleep = 1,
    /// <summary><c>rtos.yield()</c> — yield to scheduler.</summary>
    Yield = 2,
    /// <summary><c>rtos.log(tag, value)</c> — enqueue structured decimal integer log.</summary>
    Log = 3,
    /// <summary><c>rtos.log_str(msg)</c> — enqueue string log.</summary>
    LogStr = 4,
    /// <summary><c>rtos.watchdog_checkin(task_id)</c> — report liveness to watchdog supervisor.</summary>
    WatchdogCheckin = 5,
    /// <summary><c>rtos.queue_send(channel, value, timeout?)</c> — send integer to bound queue.</summary>
    QueueSend = 6,
    /// <summary><c>rtos.queue_recv(channel, timeout?)</c> — receive integer from bound queue.</summary>
    QueueRecv = 7,
    /// <summary><c>rtos.notify(target, value)</c> — direct-to-task notification.</summary>
    Notify = 8,
    /// <summary><c>rtos.gpio_read(pin)</c> — read board GPIO pin.</summary>
    GpioRead = 9,
    /// <summary><c>rtos.gpio_write(pin, value)</c> — write board GPIO pin.</summary>
    GpioWrite = 10,
    /// <summary><c>rtos.task_count()</c> — registered thread count from kernel snapshot.</summary>
    TaskCount = 11,
    /// <summary><c>rtos.task_state(index)</c> — thread state enum integer.</summary>
    TaskState = 12,
    /// <summary><c>rtos.task_priority(index)</c> — thread effective priority.</summary>
    TaskPriority = 13,
    /// <summary><c>rtos.task_stack_bytes(index)</c> — thread stack size in bytes.</summary>
    TaskStackBytes = 14,
    /// <summary><c>rtos.task_guard_ok(index)</c> — thread stack-guard sentinel health.</summary>
    TaskGuardOk = 15,
    /// <summary><c>rtos.task_cycles(index)</c> — thread accumulated runtime cycles.</summary>
    TaskCycles = 16,
    /// <summary><c>rtos.queue_depth(channel)</c> — current depth of bound queue channel.</summary>
    QueueDepth = 17,
    /// <summary><c>dbg.log_hex(tag, value)</c> — emit hex-formatted diagnostic line.</summary>
    DbgLogHex = 18,
    /// <summary><c>dbg.hexdump(addr_or_ptr, bytes)</c> — emit policy-checked hex dump lines.</summary>
    DbgHexdump = 19,
    /// <summary><c>dbg.trace(event_id, arg)</c> — record event into NexRT TraceRing.</summary>
    DbgTrace = 20,
    /// <summary><c>dbg.breakpoint(id)</c> — invoke optional host debug breakpoint callback.</summary>
    DbgBreakpoint = 21,
    /// <summary><c>rtos.dma_state(channel)</c> — NexRT DMA channel state enum integer (-1 if unbound).</summary>
    DmaState = 22,
    /// <summary><c>rtos.dma_transferred(channel)</c> — NexRT DMA bytes transferred in current/last ticket.</summary>
    DmaTransferred = 23,
    /// <summary><c>rtos.dma_capacity(channel)</c> — NexRT DMA static staging buffer capacity in bytes.</summary>
    DmaCapacity = 24,
    /// <summary><c>rtos.dma_generation(channel)</c> — NexRT DMA monotonic ticket generation.</summary>
    DmaGeneration = 25,
    /// <summary><c>rtos.hwsched_status()</c> — raw NexRT-HWSched status register word.</summary>
    HwSchedStatus = 26,
    /// <summary><c>rtos.hwsched_ready_mask()</c> — NexRT-HWSched ready thread bitmask.</summary>
    HwSchedReadyMask = 27,
    /// <summary><c>rtos.hwsched_fault_mask()</c> — NexRT-HWSched budget/SEU fault bitmask.</summary>
    HwSchedFaultMask = 28,
    /// <summary><c>rtos.hwsched_seu_count()</c> — NexRT-HWSched TMR voter SEU correction count.</summary>
    HwSchedSeuCount = 29,
    /// <summary><c>rtos.hwsched_dispatches(tid)</c> — NexRT-HWSched per-thread hardware dispatch count.</summary>
    HwSchedDispatches = 30,
    /// <summary>First ID reserved for application-specific host syscalls.</summary>
    CustomBase = 32,
}

/// <summary>Binary layout constants for the CEMB v1 ROM/hot-loadable bytecode package.</summary>
public static class EmbedBinaryFormat
{
    /// <summary>ASCII magic bytes "CEMB" as little-endian uint32.</summary>
    public const uint Magic = 0x424D4543u; // 'C','E','M','B'
    /// <summary>Current binary image format version.</summary>
    public const ushort Version = 1;
    /// <summary>Fixed header size in bytes (8-byte aligned).</summary>
    public const int HeaderSize = 80;
    /// <summary>Fixed instruction size in bytes.</summary>
    public const int InstructionSize = 8;
    /// <summary>Fixed prototype descriptor size in bytes.</summary>
    public const int ProtoDescSize = 24;
    /// <summary>Fixed constant descriptor size in bytes.</summary>
    public const int ConstDescSize = 16;
    /// <summary>Fixed upvalue descriptor size in bytes.</summary>
    public const int UpvalDescSize = 4;
    /// <summary>Fixed global descriptor size in bytes.</summary>
    public const int GlobalDescSize = 4;
    /// <summary>Fixed host import descriptor size in bytes.</summary>
    public const int HostImportDescSize = 8;
    /// <summary>Fixed string descriptor size in bytes.</summary>
    public const int StringDescSize = 12;

    /// <summary>
    /// Castagnoli CRC-32C (polynomial <c>0x82F63B78</c>), matching
    /// <c>rtos::watchdog::crc32c</c> in <c>jeffadickson/rtos</c>.
    /// </summary>
    public static uint Crc32C(ReadOnlySpan<byte> data, uint seed = 0xFFFFFFFFu)
    {
        uint crc = seed;
        for (int i = 0; i < data.Length; i++)
        {
            crc ^= data[i];
            for (int bit = 0; bit < 8; bit++)
                crc = (crc & 1u) != 0 ? (crc >> 1) ^ 0x82F63B78u : crc >> 1;
        }
        return ~crc;
    }

    /// <summary>32-bit FNV-1a hash used for ROM string matching in the static C++20 VM.</summary>
    public static uint Fnv1a32(ReadOnlySpan<byte> data)
    {
        uint h = 2166136261u;
        for (int i = 0; i < data.Length; i++)
        {
            h ^= data[i];
            h *= 16777619u;
        }
        return h;
    }
}

/// <summary>A host function declared to the Cameo.Embed compiler and mapped to a fixed numeric ID.</summary>
public sealed record EmbedHostFunctionSpec(
    string Name,
    string? Signature = null,
    ushort? ExplicitId = null,
    byte Arity = 0xFF);

/// <summary>
/// Compilation and profile constraints for targeting the static C++20 <c>Cameo.Embed</c> VM.
/// Defaults to integer-only mode (<see cref="AllowFloats"/> = <see langword="false"/>) for zero
/// soft-float/libm overhead on RV32IM and ARM Cortex-M3 targets.
/// </summary>
public sealed class EmbedCompileOptions
{
    /// <summary>Run the static Cameo type checker before lowering (recommended).</summary>
    public bool TypeCheck { get; init; } = true;
    /// <summary>Inject the built-in <c>rtos</c> API type declarations for NexRT syscalls and inspection.</summary>
    public bool IncludeRtosPrelude { get; init; } = true;
    /// <summary>Inject the built-in <c>axi</c> MMIO/bus API type declarations.</summary>
    public bool IncludeAxiPrelude { get; init; } = true;
    /// <summary>Inject the built-in <c>dbg</c> hardware debugging and bitfield API declarations.</summary>
    public bool IncludeDbgPrelude { get; init; } = true;
    /// <summary>Inject the first-class <c>Ptr</c> / <c>ptr(addr)</c> pointer type declarations.</summary>
    public bool IncludePtrPrelude { get; init; } = true;
    /// <summary>
    /// Allow IEEE-754 floating-point constants and operations.
    /// Defaults to <see langword="false"/> for zero soft-float/libm footprint on RV32IM and Cortex-M3.
    /// </summary>
    public bool AllowFloats { get; init; } = false;
    /// <summary>Allow fixed static-pool records and integer-indexed fixed arrays (dynamic dictionaries are always rejected).</summary>
    public bool AllowRecordsAndArrays { get; init; } = true;
    /// <summary>Allow <c>math</c> namespace (integer helpers always available; transcendental float ops require <see cref="AllowFloats"/>).</summary>
    public bool AllowMath { get; init; } = true;
    /// <summary>Allow static-pool closures and boxed captures (needed for local <c>fn</c> helpers and module <c>var</c> state).</summary>
    public bool AllowClosures { get; init; } = true;
    /// <summary>Disallow allocating new closures or boxes inside non-chunk functions (enforces boot-only capture allocation).</summary>
    public bool BootOnlyClosures { get; init; } = false;
    /// <summary>Allow AXI and pointer write operations; set <see langword="false"/> for read-only diagnostic probes.</summary>
    public bool AllowMemWrites { get; init; } = true;
    /// <summary>Additional application-specific host syscalls callable by name from Cameo scripts.</summary>
    public IReadOnlyList<EmbedHostFunctionSpec> HostFunctions { get; init; } = [];
}

/// <summary>Structured compile-time diagnostic produced by the Cameo.Embed pipeline.</summary>
public sealed record EmbedDiagnostic(
    string Code,
    string Message,
    string? File = null,
    int? Line = null,
    int? Column = null);

/// <summary>Imported host function recorded in a compiled CEMB image.</summary>
public sealed record EmbedHostImport(ushort HostId, string Name, byte Arity);

/// <summary>Static resource footprint summary computed during Cameo.Embed lowering.</summary>
public sealed record EmbedProgramReport(
    int ImageBytes,
    uint Crc32C,
    EmbedImageFlags Flags,
    int ProtoCount,
    int InstructionCount,
    int ConstantCount,
    int StringCount,
    int GlobalCount,
    int RequiredStackSlots,
    int RequiredFrames,
    int RequiredBoxes,
    int RequiredClosures,
    int RequiredRecords,
    int RequiredRecordSlots,
    IReadOnlyList<string> Globals,
    IReadOnlyList<EmbedHostImport> HostImports);

/// <summary>Per-prototype PC-to-source-line map emitted into the host-side debug map sidecar (zero target Flash bytes).</summary>
public sealed record EmbedProtoDebugMap(
    int ProtoId,
    string Name,
    int[] PcToLine);

/// <summary>
/// Host-side source-line debug map (<c>.cemb.map.json</c>) enabling source-level breakpoints and
/// single-stepping over UART/TCP without adding debug metadata to target Flash.
/// </summary>
public sealed record EmbedDebugMap(
    string SourceName,
    uint Crc32C,
    IReadOnlyList<EmbedProtoDebugMap> Protos)
{
    /// <summary>Finds the first <c>(protoId, pc)</c> matching a 1-based Cameo source line, if any.</summary>
    public bool TryFindLocationForLine(int line, out int protoId, out int pc)
    {
        // Prefer non-chunk functions first, then fall back to chunk proto 0.
        for (int pass = 0; pass < 2; pass++)
        {
            foreach (var p in Protos)
            {
                if (pass == 0 && p.ProtoId == 0) continue;
                if (pass == 1 && p.ProtoId != 0) continue;
                for (int i = 0; i < p.PcToLine.Length; i++)
                {
                    if (p.PcToLine[i] == line)
                    {
                        protoId = p.ProtoId;
                        pc = i;
                        return true;
                    }
                }
            }
        }
        protoId = 0;
        pc = 0;
        return false;
    }

    /// <summary>Resolves a target <c>(protoId, pc)</c> back to its 1-based Cameo source line (0 if unknown).</summary>
    public int ResolveLine(int protoId, int pc)
    {
        foreach (var p in Protos)
        {
            if (p.ProtoId == protoId && pc >= 0 && pc < p.PcToLine.Length)
                return p.PcToLine[pc];
        }
        return 0;
    }

    /// <summary>Serializes this debug map to deterministic JSON.</summary>
    public string ToJson() => System.Text.Json.JsonSerializer.Serialize(
        this,
        new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

    /// <summary>Deserializes a debug map from JSON.</summary>
    public static EmbedDebugMap FromJson(string json) =>
        System.Text.Json.JsonSerializer.Deserialize<EmbedDebugMap>(json)
        ?? throw new InvalidOperationException("Invalid Cameo.Embed debug map JSON.");
}

/// <summary>Compiled static ROM / hot-loadable CEMB image plus metadata, footprint report, and host debug map.</summary>
public sealed class EmbedCompiledImage
{
    /// <summary>Flat little-endian CEMB v1 binary bytes suitable for Flash <c>.rodata</c> or UART hot-loading.</summary>
    public byte[] Bytes { get; }
    /// <summary>Static resource requirements and capability inventory.</summary>
    public EmbedProgramReport Report { get; }
    /// <summary>Logical source file name.</summary>
    public string SourceName { get; }
    /// <summary>Host-side source-line debug map (kept outside target Flash).</summary>
    public EmbedDebugMap DebugMap { get; }

    internal EmbedCompiledImage(byte[] bytes, EmbedProgramReport report, string sourceName, EmbedDebugMap? debugMap = null)
    {
        Bytes = bytes;
        Report = report;
        SourceName = sourceName;
        DebugMap = debugMap ?? new EmbedDebugMap(sourceName, report.Crc32C, []);
    }

    /// <summary>Generates a zero-constructor, freestanding C++20 header embedding this image in <c>.rodata</c>.</summary>
    public string EmitCppHeader(string symbolPrefix = "kCameoProgram", string cppNamespace = "cameo::programs") =>
        EmbedCppEmitter.EmitHeader(this, symbolPrefix, cppNamespace);

    /// <summary>Encodes the binary image as uppercase ASCII hex suitable for UART monitor hot-loading.</summary>
    public string ToHexString() => Convert.ToHexString(Bytes);
}

/// <summary>Railway-oriented compile outcome for <see cref="EmbedCompiler"/>.</summary>
public sealed record EmbedCompileResult(
    EmbedCompiledImage? Image,
    IReadOnlyList<EmbedDiagnostic> Diagnostics)
{
    /// <summary>True when compilation succeeded with a valid CEMB binary image.</summary>
    public bool Success => Image is not null && Diagnostics.Count == 0;
}

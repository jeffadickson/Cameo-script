using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using Cameo.Runtime;
using Cameo.Runtime.Vm;

namespace Cameo.Embed;

/// <summary>Memory/MMIO access permission flags mirroring <c>cameo::embed::MemoryWindow</c>.</summary>
[Flags]
public enum EmbedMemoryPerms : byte
{
    /// <summary>No access permitted.</summary>
    None = 0,
    /// <summary>Read access permitted.</summary>
    Read = 1 << 0,
    /// <summary>Write access permitted.</summary>
    Write = 1 << 1,
    /// <summary>AXI peripheral window (accessible via <c>axi.*</c> and <c>Ptr</c>).</summary>
    Axi = 1 << 2,
    /// <summary>RAM/SRAM data window (accessible via <c>Ptr</c> and <c>axi.*</c>).</summary>
    Ram = 1 << 3,
}

/// <summary>Simulated board MMIO/RAM window for desktop differential testing.</summary>
public sealed class SimulatedMemoryWindow
{
    /// <summary>Base physical/bus address.</summary>
    public ulong Base { get; }
    /// <summary>Window size in bytes.</summary>
    public ulong Size => (ulong)Data.Length;
    /// <summary>Allowed permissions.</summary>
    public EmbedMemoryPerms Perms { get; }
    /// <summary>Minimum allowed transaction width in bytes (1, 2, 4, or 8).</summary>
    public byte MinWidth { get; }
    /// <summary>Maximum allowed transaction width in bytes (1, 2, 4, or 8).</summary>
    public byte MaxWidth { get; }
    /// <summary>Enforce natural alignment (<c>addr % width == 0</c>).</summary>
    public bool StrictAlign { get; }
    /// <summary>Backing little-endian byte storage for simulation.</summary>
    public byte[] Data { get; }

    /// <summary>Creates a zero-initialized simulated MMIO/RAM window.</summary>
    public SimulatedMemoryWindow(
        ulong baseAddr, int sizeBytes, EmbedMemoryPerms perms,
        byte minWidth = 1, byte maxWidth = 8, bool strictAlign = true)
    {
        Base = baseAddr;
        Perms = perms;
        MinWidth = minWidth;
        MaxWidth = maxWidth;
        StrictAlign = strictAlign;
        Data = new byte[sizeBytes];
    }

    /// <summary>Writes a 32-bit little-endian word directly into backing storage (bypassing script policy for test setup).</summary>
    public void SeedU32(ulong offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(Data.AsSpan((int)offset, 4), value);

    /// <summary>Reads a 32-bit little-endian word directly from backing storage.</summary>
    public uint PeekU32(ulong offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan((int)offset, 4));
}

/// <summary>Simulated NexRT thread snapshot row for testing <c>rtos.task_*</c> inspection calls.</summary>
public sealed record SimulatedTaskInfo(
    int State,
    int Priority,
    int StackBytes,
    bool GuardOk,
    long Cycles);

/// <summary>First-class <c>Ptr</c> userdata value inside desktop <see cref="ScriptEngine"/> simulations.</summary>
public sealed class SimulatedPtr : IUserdata
{
    /// <summary>Unsigned 64-bit address carried by this pointer.</summary>
    public ulong Address { get; }
    /// <inheritdoc />
    public string TypeName => "Ptr";
    /// <inheritdoc />
    public Table? Metatable { get; set; }

    /// <summary>Constructs a simulated pointer for <paramref name="address"/>.</summary>
    public SimulatedPtr(ulong address, Table? metatable = null)
    {
        Address = address;
        Metatable = metatable;
    }
}

/// <summary>
/// Deterministic desktop simulator of the complete <c>Cameo.Embed</c> surface
/// (<c>Ptr</c>, <c>axi.*</c>, <c>dbg.*</c>, and <c>rtos.*</c>) for running and
/// differential-testing scripts in <see cref="ScriptEngine"/> against the C++20 <c>StaticVm</c>.
/// </summary>
public sealed class SimulatedEmbedHost
{
    private readonly Dictionary<long, Queue<long>> _queues = new();
    private readonly Dictionary<long, long> _gpio = new();
    private readonly Dictionary<long, long> _notifications = new();
    private readonly List<(string Tag, long Value)> _logs = new();
    private readonly List<(string Tag, ulong Value)> _hexLogs = new();
    private readonly List<string> _strLogs = new();
    private readonly List<string> _hexdumpLines = new();
    private readonly List<long> _watchdogCheckins = new();
    private readonly List<(long EventId, long Arg)> _traceEvents = new();
    private readonly List<long> _breakpoints = new();
    private readonly List<SimulatedMemoryWindow> _windows = new();
    private readonly List<SimulatedTaskInfo> _tasks = new();
    private readonly uint[] _scratch;

    /// <summary>Creates a simulator with a fixed static scratchpad capacity in 32-bit words.</summary>
    public SimulatedEmbedHost(int scratchWords = 64)
    {
        _scratch = new uint[scratchWords];
    }

    /// <summary>Current simulated kernel tick counter.</summary>
    public long Ticks { get; set; }
    /// <summary>Number of <c>rtos.yield()</c> calls observed.</summary>
    public long Yields { get; private set; }
    /// <summary>Number of <c>axi.fence()</c> memory barriers observed.</summary>
    public long Fences { get; private set; }
    /// <summary>When true, all <c>axi.*</c> and <c>Ptr</c> writes are denied regardless of window flags.</summary>
    public bool AxiReadOnly { get; set; }
    /// <summary>Optional hook invoked on every <c>axi.poll32</c> iteration (e.g. to flip hardware status bits).</summary>
    public Action<int>? OnPollStep { get; set; }

    /// <summary>Structured <c>rtos.log(tag, value)</c> entries recorded.</summary>
    public IReadOnlyList<(string Tag, long Value)> Logs => _logs;
    /// <summary>Hex <c>dbg.log_hex(tag, value)</c> entries recorded.</summary>
    public IReadOnlyList<(string Tag, ulong Value)> HexLogs => _hexLogs;
    /// <summary>Plain string <c>rtos.log_str(msg)</c> entries recorded.</summary>
    public IReadOnlyList<string> StringLogs => _strLogs;
    /// <summary>Formatted hex dump lines emitted by <c>dbg.hexdump()</c>.</summary>
    public IReadOnlyList<string> HexdumpLines => _hexdumpLines;
    /// <summary>Task IDs passed to <c>rtos.watchdog_checkin(task_id)</c>.</summary>
    public IReadOnlyList<long> WatchdogCheckins => _watchdogCheckins;
    /// <summary>Binary trace events recorded by <c>dbg.trace(id, arg)</c>.</summary>
    public IReadOnlyList<(long EventId, long Arg)> TraceEvents => _traceEvents;
    /// <summary>Breakpoint IDs hit by <c>dbg.breakpoint(id)</c>.</summary>
    public IReadOnlyList<long> Breakpoints => _breakpoints;
    /// <summary>Current GPIO pin values indexed by pin number.</summary>
    public IReadOnlyDictionary<long, long> Gpio => _gpio;
    /// <summary>Last notification value sent to each target task ID.</summary>
    public IReadOnlyDictionary<long, long> Notifications => _notifications;
    /// <summary>Simulated AXI/RAM windows.</summary>
    public IReadOnlyList<SimulatedMemoryWindow> Windows => _windows;
    /// <summary>Static 32-bit scratchpad words.</summary>
    public uint[] Scratch => _scratch;
    /// <summary>Captured AXI/Ptr bus trace events when tracing or watchpoints are enabled.</summary>
    public IReadOnlyList<BusTraceRecord> BusTrace => _busTrace;
    /// <summary>Total hardware watchpoint hits observed.</summary>
    public int WatchHits => _watchHits;
    /// <summary>Whether unconditional AXI bus tracing is currently enabled.</summary>
    public bool BusTraceEnabled { get; set; }
    /// <summary>Simulated NexRT-HWSched status register word.</summary>
    public uint HwSchedStatus { get; set; }
    /// <summary>Simulated NexRT-HWSched ready thread bitmask.</summary>
    public uint HwSchedReadyMask { get; set; }
    /// <summary>Simulated NexRT-HWSched fault bitmask.</summary>
    public uint HwSchedFaultMask { get; set; }
    /// <summary>Simulated NexRT-HWSched SEU count.</summary>
    public uint HwSchedSeuCount { get; set; }
    /// <summary>Simulated NexRT-HWSched per-thread dispatch counts.</summary>
    public Dictionary<int, uint> HwSchedDispatches { get; } = new();
    /// <summary>Simulated DMA channel snapshots keyed by channel index.</summary>
    public Dictionary<int, (int State, long Transferred, long Capacity, long Generation)> DmaChannels { get; } = new();

    private readonly List<(ulong Base, ulong Size, byte Flags, bool Active)> _watchpoints = new();
    private readonly List<BusTraceRecord> _busTrace = new();
    private int _watchHits;

    /// <summary>Registers a simulated MMIO or RAM address window.</summary>
    public SimulatedMemoryWindow AddWindow(
        ulong baseAddr, int sizeBytes, EmbedMemoryPerms perms,
        byte minWidth = 1, byte maxWidth = 8, bool strictAlign = true)
    {
        var w = new SimulatedMemoryWindow(baseAddr, sizeBytes, perms, minWidth, maxWidth, strictAlign);
        _windows.Add(w);
        return w;
    }

    /// <summary>Adds a simulated NexRT thread row for <c>rtos.task_*</c> inspection.</summary>
    public void AddTask(SimulatedTaskInfo task) => _tasks.Add(task);

    /// <summary>Enqueues an integer message into a simulated RTOS channel queue.</summary>
    public void Enqueue(long channel, long value)
    {
        if (!_queues.TryGetValue(channel, out var q))
            _queues[channel] = q = new Queue<long>();
        q.Enqueue(value);
    }

    /// <summary>Dequeues an integer message from a simulated RTOS channel queue.</summary>
    public bool TryDequeue(long channel, out long value)
    {
        if (_queues.TryGetValue(channel, out var q) && q.Count > 0)
        {
            value = q.Dequeue();
            return true;
        }
        value = 0;
        return false;
    }

    /// <summary>Sets a simulated GPIO input pin value.</summary>
    public void SetGpio(long pin, long value) => _gpio[pin] = value;

    private void RecordBusEvent(ulong addr, byte width, ulong val, bool isWrite, bool isFault)
    {
        bool wpHit = false;
        byte rwBit = isWrite ? (byte)2 : (byte)1;
        if (!isFault)
        {
            for (int i = 0; i < _watchpoints.Count; i++)
            {
                var wp = _watchpoints[i];
                if (!wp.Active || (wp.Flags & rwBit) == 0) continue;
                ulong aEnd = addr <= ulong.MaxValue - width ? addr + width : ulong.MaxValue;
                ulong wEnd = wp.Base <= ulong.MaxValue - wp.Size ? wp.Base + wp.Size : ulong.MaxValue;
                if (addr < wEnd && aEnd > wp.Base)
                {
                    wpHit = true;
                    _watchHits++;
                    break;
                }
            }
        }
        if (BusTraceEnabled || wpHit || isFault)
            _busTrace.Add(new BusTraceRecord((uint)_busTrace.Count, isWrite, width, addr, val, wpHit));
    }

    /// <summary>Checks whether an access of <paramref name="width"/> bytes at <paramref name="addr"/> is permitted.</summary>
    public bool TryFindWindow(ulong addr, uint width, bool isWrite, out SimulatedMemoryWindow? win)
    {
        win = null;
        if (width == 0 || (isWrite && AxiReadOnly)) return false;
        foreach (SimulatedMemoryWindow w in _windows)
        {
            if (addr < w.Base) continue;
            ulong off = addr - w.Base;
            if (width > w.Size || off > w.Size - width) continue;
            if (isWrite && !w.Perms.HasFlag(EmbedMemoryPerms.Write)) return false;
            if (!isWrite && !w.Perms.HasFlag(EmbedMemoryPerms.Read)) return false;
            if (width < w.MinWidth || width > w.MaxWidth) return false;
            if (w.StrictAlign && (addr & (width - 1UL)) != 0) return false;
            win = w;
            return true;
        }
        return false;
    }

    /// <summary>Performs a policy-checked little-endian read of 1, 2, 4, or 8 bytes.</summary>
    public bool TryRead(ulong addr, byte width, out ulong value)
    {
        value = 0;
        if (!TryFindWindow(addr, width, isWrite: false, out SimulatedMemoryWindow? win) || win is null)
        {
            RecordBusEvent(addr, width, 0, isWrite: false, isFault: true);
            return false;
        }
        int off = (int)(addr - win.Base);
        value = width switch
        {
            1 => win.Data[off],
            2 => BinaryPrimitives.ReadUInt16LittleEndian(win.Data.AsSpan(off, 2)),
            4 => BinaryPrimitives.ReadUInt32LittleEndian(win.Data.AsSpan(off, 4)),
            8 => BinaryPrimitives.ReadUInt64LittleEndian(win.Data.AsSpan(off, 8)),
            _ => 0UL,
        };
        RecordBusEvent(addr, width, value, isWrite: false, isFault: false);
        return true;
    }

    /// <summary>Performs a policy-checked little-endian write of 1, 2, 4, or 8 bytes.</summary>
    public bool TryWrite(ulong addr, byte width, ulong value)
    {
        if (!TryFindWindow(addr, width, isWrite: true, out SimulatedMemoryWindow? win) || win is null)
        {
            RecordBusEvent(addr, width, value, isWrite: true, isFault: true);
            return false;
        }
        int off = (int)(addr - win.Base);
        switch (width)
        {
            case 1: win.Data[off] = (byte)value; break;
            case 2: BinaryPrimitives.WriteUInt16LittleEndian(win.Data.AsSpan(off, 2), (ushort)value); break;
            case 4: BinaryPrimitives.WriteUInt32LittleEndian(win.Data.AsSpan(off, 4), (uint)value); break;
            case 8: BinaryPrimitives.WriteUInt64LittleEndian(win.Data.AsSpan(off, 8), value); break;
            default: RecordBusEvent(addr, width, value, isWrite: true, isFault: true); return false;
        }
        RecordBusEvent(addr, width, value, isWrite: true, isFault: false);
        return true;
    }

    private static ulong CheckAddrArg(ref CallContext c, int index)
    {
        Value v = c[index];
        if (v.Kind == ValueKind.Userdata && v.O is SimulatedPtr p)
            return p.Address;
        return unchecked((ulong)c.CheckInt(index));
    }

    private static SimulatedPtr CheckPtr(ref CallContext c, int index)
    {
        Value v = c[index];
        if (v.Kind == ValueKind.Userdata && v.O is SimulatedPtr p)
            return p;
        throw new LuaException(Value.FromText($"expected Ptr at argument #{index + 1}"));
    }

    /// <summary>Installs <c>Ptr</c>, <c>ptr()</c>, <c>axi</c>, <c>dbg</c>, and <c>rtos</c> into <paramref name="engine"/>.</summary>
    public void Install(ScriptEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        engine.DeclareGlobal(EmbedPrelude.PtrDeclarationSource, "<embed-ptr-prelude>");
        engine.DeclareGlobal(EmbedPrelude.AxiDeclarationSource, "<embed-axi-prelude>");
        engine.DeclareGlobal(EmbedPrelude.DbgDeclarationSource, "<embed-dbg-prelude>");
        engine.DeclareGlobal(EmbedPrelude.RtosDeclarationSource, "<embed-rtos-prelude>");

        Table ptrMethods = new Table();
        Table ptrMt = new Table();

        Value MakePtr(ulong addr) => Value.FromUserdata(new SimulatedPtr(addr, ptrMt));

        ptrMethods.SetStr("addr", Value.FromFunction((ref CallContext c) =>
            c.Return(Value.FromInt(unchecked((long)CheckPtr(ref c, 0).Address))), "Ptr.addr"));
        ptrMethods.SetStr("is_null", Value.FromFunction((ref CallContext c) =>
            c.Return(Value.FromBool(CheckPtr(ref c, 0).Address == 0UL)), "Ptr.is_null"));
        ptrMethods.SetStr("is_aligned", Value.FromFunction((ref CallContext c) =>
        {
            ulong a = CheckPtr(ref c, 0).Address;
            long align = c.CheckInt(1);
            bool ok = align > 0 && ((ulong)align & ((ulong)align - 1UL)) == 0 && (a & ((ulong)align - 1UL)) == 0;
            return c.Return(Value.FromBool(ok));
        }, "Ptr.is_aligned"));
        ptrMethods.SetStr("align_up", Value.FromFunction((ref CallContext c) =>
        {
            ulong a = CheckPtr(ref c, 0).Address;
            long align = c.CheckInt(1);
            if (align <= 0 || ((ulong)align & ((ulong)align - 1UL)) != 0)
                throw new LuaException(Value.FromText("Ptr.align_up requires a positive power-of-two alignment"));
            ulong mask = (ulong)align - 1UL;
            return c.Return(MakePtr((a + mask) & ~mask));
        }, "Ptr.align_up"));
        ptrMethods.SetStr("add", Value.FromFunction((ref CallContext c) =>
            c.Return(MakePtr(unchecked(CheckPtr(ref c, 0).Address + (ulong)c.CheckInt(1)))), "Ptr.add"));
        ptrMethods.SetStr("sub", Value.FromFunction((ref CallContext c) =>
            c.Return(MakePtr(unchecked(CheckPtr(ref c, 0).Address - (ulong)c.CheckInt(1)))), "Ptr.sub"));
        ptrMethods.SetStr("diff", Value.FromFunction((ref CallContext c) =>
            c.Return(Value.FromInt(unchecked((long)(CheckPtr(ref c, 0).Address - CheckPtr(ref c, 1).Address)))), "Ptr.diff"));

        Value MakeReadMethod(byte width, string name) => Value.FromFunction((ref CallContext c) =>
        {
            ulong addr = CheckPtr(ref c, 0).Address;
            if (!TryRead(addr, width, out ulong val))
                throw new LuaException(Value.FromText($"Memory fault in {name} at 0x{addr:X}"));
            return c.Return(Value.FromInt(unchecked((long)val)));
        }, name);

        Value MakeWriteMethod(byte width, string name) => Value.FromFunction((ref CallContext c) =>
        {
            ulong addr = CheckPtr(ref c, 0).Address;
            ulong val = unchecked((ulong)c.CheckInt(1));
            return c.Return(Value.FromBool(TryWrite(addr, width, val)));
        }, name);

        ptrMethods.SetStr("read8", MakeReadMethod(1, "Ptr.read8"));
        ptrMethods.SetStr("read16", MakeReadMethod(2, "Ptr.read16"));
        ptrMethods.SetStr("read32", MakeReadMethod(4, "Ptr.read32"));
        ptrMethods.SetStr("read64", MakeReadMethod(8, "Ptr.read64"));
        ptrMethods.SetStr("write8", MakeWriteMethod(1, "Ptr.write8"));
        ptrMethods.SetStr("write16", MakeWriteMethod(2, "Ptr.write16"));
        ptrMethods.SetStr("write32", MakeWriteMethod(4, "Ptr.write32"));
        ptrMethods.SetStr("write64", MakeWriteMethod(8, "Ptr.write64"));

        Value MakeReadAtMethod(byte width, string name) => Value.FromFunction((ref CallContext c) =>
        {
            ulong addr = unchecked(CheckPtr(ref c, 0).Address + (ulong)c.CheckInt(1));
            if (!TryRead(addr, width, out ulong val))
                throw new LuaException(Value.FromText($"Memory fault in {name} at 0x{addr:X}"));
            return c.Return(Value.FromInt(unchecked((long)val)));
        }, name);

        Value MakeWriteAtMethod(byte width, string name) => Value.FromFunction((ref CallContext c) =>
        {
            ulong addr = unchecked(CheckPtr(ref c, 0).Address + (ulong)c.CheckInt(1));
            ulong val = unchecked((ulong)c.CheckInt(2));
            return c.Return(Value.FromBool(TryWrite(addr, width, val)));
        }, name);

        ptrMethods.SetStr("read8_at", MakeReadAtMethod(1, "Ptr.read8_at"));
        ptrMethods.SetStr("read16_at", MakeReadAtMethod(2, "Ptr.read16_at"));
        ptrMethods.SetStr("read32_at", MakeReadAtMethod(4, "Ptr.read32_at"));
        ptrMethods.SetStr("read64_at", MakeReadAtMethod(8, "Ptr.read64_at"));
        ptrMethods.SetStr("write8_at", MakeWriteAtMethod(1, "Ptr.write8_at"));
        ptrMethods.SetStr("write16_at", MakeWriteAtMethod(2, "Ptr.write16_at"));
        ptrMethods.SetStr("write32_at", MakeWriteAtMethod(4, "Ptr.write32_at"));
        ptrMethods.SetStr("write64_at", MakeWriteAtMethod(8, "Ptr.write64_at"));
        ptrMethods.SetStr("bits32_at", Value.FromFunction((ref CallContext c) =>
        {
            ulong addr = unchecked(CheckPtr(ref c, 0).Address + (ulong)c.CheckInt(1));
            long lsb = c.CheckInt(2);
            long width = c.CheckInt(3);
            if (!TryRead(addr, 4, out ulong raw))
                throw new LuaException(Value.FromText($"Memory fault in Ptr.bits32_at at 0x{addr:X}"));
            if (lsb < 0 || lsb >= 32 || width <= 0) return c.Return(Value.FromInt(0));
            uint shifted = unchecked((uint)raw) >> (int)lsb;
            uint mask = width >= 32 ? uint.MaxValue : ((1u << (int)width) - 1u);
            return c.Return(Value.FromInt(shifted & mask));
        }, "Ptr.bits32_at"));
        ptrMethods.SetStr("rmw_bits32_at", Value.FromFunction((ref CallContext c) =>
        {
            ulong addr = unchecked(CheckPtr(ref c, 0).Address + (ulong)c.CheckInt(1));
            long lsb = c.CheckInt(2);
            long width = c.CheckInt(3);
            uint fv = unchecked((uint)c.CheckInt(4));
            if (!TryRead(addr, 4, out ulong raw))
                return c.Return(Value.FromInt(0), Value.False);
            if (lsb < 0 || lsb >= 32 || width <= 0)
                return c.Return(Value.FromInt(unchecked((long)raw)), Value.False);
            uint mask = width >= 32 ? uint.MaxValue : ((1u << (int)width) - 1u);
            uint smask = mask << (int)lsb;
            uint next = (unchecked((uint)raw) & ~smask) | ((fv & mask) << (int)lsb);
            if (!TryWrite(addr, 4, next))
                return c.Return(Value.FromInt(0), Value.False);
            return c.Return(Value.FromInt(next), Value.True);
        }, "Ptr.rmw_bits32_at"));

        ptrMt.SetStr("__index", Value.FromTable(ptrMethods));
        ptrMt.SetStr("__add", Value.FromFunction((ref CallContext c) =>
        {
            Value a = c[0], b = c[1];
            if (a.Kind == ValueKind.Userdata && a.O is SimulatedPtr pa)
                return c.Return(MakePtr(unchecked(pa.Address + (ulong)c.CheckInt(1))));
            if (b.Kind == ValueKind.Userdata && b.O is SimulatedPtr pb)
                return c.Return(MakePtr(unchecked(pb.Address + (ulong)c.CheckInt(0))));
            throw new LuaException(Value.FromText("invalid Ptr addition"));
        }, "Ptr.__add"));
        ptrMt.SetStr("__sub", Value.FromFunction((ref CallContext c) =>
        {
            SimulatedPtr pa = CheckPtr(ref c, 0);
            Value b = c[1];
            if (b.Kind == ValueKind.Userdata && b.O is SimulatedPtr pb)
                return c.Return(Value.FromInt(unchecked((long)(pa.Address - pb.Address))));
            return c.Return(MakePtr(unchecked(pa.Address - (ulong)c.CheckInt(1))));
        }, "Ptr.__sub"));
        ptrMt.SetStr("__eq", Value.FromFunction((ref CallContext c) =>
            c.Return(Value.FromBool(CheckPtr(ref c, 0).Address == CheckPtr(ref c, 1).Address)), "Ptr.__eq"));
        ptrMt.SetStr("__lt", Value.FromFunction((ref CallContext c) =>
            c.Return(Value.FromBool(CheckPtr(ref c, 0).Address < CheckPtr(ref c, 1).Address)), "Ptr.__lt"));
        ptrMt.SetStr("__le", Value.FromFunction((ref CallContext c) =>
            c.Return(Value.FromBool(CheckPtr(ref c, 0).Address <= CheckPtr(ref c, 1).Address)), "Ptr.__le"));

        Table ptrNs = new Table();
        ptrNs.SetStr("of", Value.FromFunction((ref CallContext c) =>
            c.Return(MakePtr(unchecked((ulong)c.CheckInt(0)))), "Ptr.of"));
        ptrNs.SetStr("null", Value.FromFunction((ref CallContext c) =>
            c.Return(MakePtr(0UL)), "Ptr.null"));

        engine.State.Globals.SetStr("Ptr", Value.FromTable(ptrNs));
        engine.State.Globals.SetStr("ptr", Value.FromFunction((ref CallContext c) =>
            c.Return(MakePtr(unchecked((ulong)c.CheckInt(0)))), "ptr"));

        // axi.* namespace
        Table axi = new Table();
        Value MakeAxiRead(byte width, string name) => Value.FromFunction((ref CallContext c) =>
        {
            ulong addr = CheckAddrArg(ref c, 0);
            if (TryRead(addr, width, out ulong val))
                return c.Return(Value.FromInt(unchecked((long)val)), Value.True);
            return c.Return(Value.FromInt(0), Value.False);
        }, name);
        Value MakeAxiWrite(byte width, string name) => Value.FromFunction((ref CallContext c) =>
        {
            ulong addr = CheckAddrArg(ref c, 0);
            ulong val = unchecked((ulong)c.CheckInt(1));
            return c.Return(Value.FromBool(TryWrite(addr, width, val)));
        }, name);

        axi.SetStr("read8", MakeAxiRead(1, "axi.read8"));
        axi.SetStr("read16", MakeAxiRead(2, "axi.read16"));
        axi.SetStr("read32", MakeAxiRead(4, "axi.read32"));
        axi.SetStr("read64", MakeAxiRead(8, "axi.read64"));
        axi.SetStr("write8", MakeAxiWrite(1, "axi.write8"));
        axi.SetStr("write16", MakeAxiWrite(2, "axi.write16"));
        axi.SetStr("write32", MakeAxiWrite(4, "axi.write32"));
        axi.SetStr("write64", MakeAxiWrite(8, "axi.write64"));
        axi.SetStr("rmw32", Value.FromFunction((ref CallContext c) =>
        {
            ulong addr = CheckAddrArg(ref c, 0);
            uint mask = unchecked((uint)c.CheckInt(1));
            uint val = unchecked((uint)c.CheckInt(2));
            if (!TryRead(addr, 4, out ulong cur))
                return c.Return(Value.FromInt(0), Value.False);
            uint next = (((uint)cur) & ~mask) | (val & mask);
            if (!TryWrite(addr, 4, next))
                return c.Return(Value.FromInt(0), Value.False);
            return c.Return(Value.FromInt(next), Value.True);
        }, "axi.rmw32"));
        axi.SetStr("poll32", Value.FromFunction((ref CallContext c) =>
        {
            ulong addr = CheckAddrArg(ref c, 0);
            uint mask = unchecked((uint)c.CheckInt(1));
            uint expect = unchecked((uint)c.CheckInt(2));
            long maxPolls = c.CheckInt(3);
            if (maxPolls < 1) maxPolls = 1;
            uint last = 0;
            for (int iter = 0; iter < maxPolls; iter++)
            {
                OnPollStep?.Invoke(iter);
                if (!TryRead(addr, 4, out ulong raw))
                    return c.Return(Value.FromInt(0), Value.False);
                last = (uint)raw;
                if ((last & mask) == (expect & mask))
                    return c.Return(Value.FromInt(last), Value.True);
            }
            return c.Return(Value.FromInt(last), Value.False);
        }, "axi.poll32"));
        axi.SetStr("burst_read32", Value.FromFunction((ref CallContext c) =>
        {
            ulong addr = CheckAddrArg(ref c, 0);
            long count = c.CheckInt(1);
            long off = !c.IsNoneOrNil(2) ? c.CheckInt(2) : 0;
            if (count < 0 || off < 0 || off + count > _scratch.Length)
                return c.Return(Value.FromInt(0), Value.False);
            for (long i = 0; i < count; i++)
            {
                if (!TryRead(addr + (ulong)(i * 4), 4, out ulong raw))
                    return c.Return(Value.FromInt(i), Value.False);
                _scratch[off + i] = (uint)raw;
            }
            return c.Return(Value.FromInt(count), Value.True);
        }, "axi.burst_read32"));
        axi.SetStr("burst_write32", Value.FromFunction((ref CallContext c) =>
        {
            ulong addr = CheckAddrArg(ref c, 0);
            long count = c.CheckInt(1);
            long off = !c.IsNoneOrNil(2) ? c.CheckInt(2) : 0;
            if (count < 0 || off < 0 || off + count > _scratch.Length)
                return c.Return(Value.FromInt(0), Value.False);
            for (long i = 0; i < count; i++)
            {
                if (!TryWrite(addr + (ulong)(i * 4), 4, _scratch[off + i]))
                    return c.Return(Value.FromInt(i), Value.False);
            }
            return c.Return(Value.FromInt(count), Value.True);
        }, "axi.burst_write32"));
        axi.SetStr("scratch_get32", Value.FromFunction((ref CallContext c) =>
        {
            long idx = c.CheckInt(0);
            if (idx < 0 || idx >= _scratch.Length) return c.Return(Value.FromInt(0));
            return c.Return(Value.FromInt(_scratch[idx]));
        }, "axi.scratch_get32"));
        axi.SetStr("scratch_set32", Value.FromFunction((ref CallContext c) =>
        {
            long idx = c.CheckInt(0);
            if (idx < 0 || idx >= _scratch.Length) return c.Return(Value.False);
            _scratch[idx] = unchecked((uint)c.CheckInt(1));
            return c.Return(Value.True);
        }, "axi.scratch_set32"));
        axi.SetStr("scratch_len", Value.FromFunction((ref CallContext c) =>
            c.Return(Value.FromInt(_scratch.Length)), "axi.scratch_len"));
        axi.SetStr("fence", Value.FromFunction((ref CallContext c) => { Fences++; return 0; }, "axi.fence"));
        axi.SetStr("window_check", Value.FromFunction((ref CallContext c) =>
        {
            ulong addr = CheckAddrArg(ref c, 0);
            long sz = c.CheckInt(1);
            bool wr = c[2].IsTruthy;
            if (sz <= 0 || sz > 8) return c.Return(Value.False);
            return c.Return(Value.FromBool(TryFindWindow(addr, (uint)sz, wr, out _)));
        }, "axi.window_check"));
        axi.SetStr("watch", Value.FromFunction((ref CallContext c) =>
        {
            ulong addr = CheckAddrArg(ref c, 0);
            long sz = c.CheckInt(1);
            byte fl = unchecked((byte)(c.CheckInt(2) & 0x7));
            if (sz <= 0 || (fl & 0x3) == 0) return c.Return(Value.FromInt(-1));
            for (int i = 0; i < _watchpoints.Count; i++)
            {
                if (!_watchpoints[i].Active)
                {
                    _watchpoints[i] = (addr, (ulong)sz, fl, true);
                    return c.Return(Value.FromInt(i));
                }
            }
            if (_watchpoints.Count >= 8) return c.Return(Value.FromInt(-1));
            _watchpoints.Add((addr, (ulong)sz, fl, true));
            return c.Return(Value.FromInt(_watchpoints.Count - 1));
        }, "axi.watch"));
        axi.SetStr("unwatch", Value.FromFunction((ref CallContext c) =>
        {
            int id = (int)c.CheckInt(0);
            if (id < 0 || id >= _watchpoints.Count || !_watchpoints[id].Active)
                return c.Return(Value.False);
            _watchpoints[id] = (0, 0, 0, false);
            return c.Return(Value.True);
        }, "axi.unwatch"));
        axi.SetStr("watch_hits", Value.FromFunction((ref CallContext c) =>
            c.Return(Value.FromInt(_watchHits)), "axi.watch_hits"));
        axi.SetStr("trace_enable", Value.FromFunction((ref CallContext c) =>
        {
            BusTraceEnabled = c[0].IsTruthy;
            return c.Return(Value.FromBool(BusTraceEnabled));
        }, "axi.trace_enable"));
        axi.SetStr("trace_count", Value.FromFunction((ref CallContext c) =>
            c.Return(Value.FromInt(_busTrace.Count)), "axi.trace_count"));
        engine.State.Globals.SetStr("axi", Value.FromTable(axi));

        // dbg.* namespace
        Table dbg = new Table();
        dbg.SetStr("bits", Value.FromFunction((ref CallContext c) =>
        {
            ulong val = unchecked((ulong)c.CheckInt(0));
            long lsb = c.CheckInt(1);
            long width = c.CheckInt(2);
            if (lsb < 0 || lsb >= 64 || width <= 0) return c.Return(Value.FromInt(0));
            ulong shifted = val >> (int)lsb;
            ulong mask = width >= 64 ? ulong.MaxValue : ((1UL << (int)width) - 1UL);
            return c.Return(Value.FromInt(unchecked((long)(shifted & mask))));
        }, "dbg.bits"));
        dbg.SetStr("set_bits", Value.FromFunction((ref CallContext c) =>
        {
            ulong val = unchecked((ulong)c.CheckInt(0));
            long lsb = c.CheckInt(1);
            long width = c.CheckInt(2);
            ulong fv = unchecked((ulong)c.CheckInt(3));
            if (lsb < 0 || lsb >= 64 || width <= 0) return c.Return(Value.FromInt(unchecked((long)val)));
            ulong mask = width >= 64 ? ulong.MaxValue : ((1UL << (int)width) - 1UL);
            ulong shiftedMask = mask << (int)lsb;
            ulong res = (val & ~shiftedMask) | ((fv & mask) << (int)lsb);
            return c.Return(Value.FromInt(unchecked((long)res)));
        }, "dbg.set_bits"));
        dbg.SetStr("bit", Value.FromFunction((ref CallContext c) =>
        {
            ulong val = unchecked((ulong)c.CheckInt(0));
            long idx = c.CheckInt(1);
            bool set = idx >= 0 && idx < 64 && ((val >> (int)idx) & 1UL) != 0;
            return c.Return(Value.FromBool(set));
        }, "dbg.bit"));
        dbg.SetStr("popcount", Value.FromFunction((ref CallContext c) =>
            c.Return(Value.FromInt(BitOperations.PopCount(unchecked((ulong)c.CheckInt(0))))), "dbg.popcount"));
        dbg.SetStr("clz32", Value.FromFunction((ref CallContext c) =>
            c.Return(Value.FromInt(BitOperations.LeadingZeroCount(unchecked((uint)c.CheckInt(0))))), "dbg.clz32"));
        dbg.SetStr("ctz32", Value.FromFunction((ref CallContext c) =>
            c.Return(Value.FromInt(BitOperations.TrailingZeroCount(unchecked((uint)c.CheckInt(0))))), "dbg.ctz32"));
        dbg.SetStr("bswap32", Value.FromFunction((ref CallContext c) =>
            c.Return(Value.FromInt(BinaryPrimitives.ReverseEndianness(unchecked((uint)c.CheckInt(0))))), "dbg.bswap32"));
        dbg.SetStr("log_hex", Value.FromFunction((ref CallContext c) =>
        {
            string tag = c.CheckText(0);
            ulong val = unchecked((ulong)c.CheckInt(1));
            _hexLogs.Add((tag, val));
            return c.Return(Value.True);
        }, "dbg.log_hex"));
        dbg.SetStr("hexdump", Value.FromFunction((ref CallContext c) =>
        {
            ulong addr = CheckAddrArg(ref c, 0);
            long len = c.CheckInt(1);
            if (len <= 0) return c.Return(Value.FromInt(0));
            if (len > 256) len = 256;
            long dumped = 0;
            var sb = new StringBuilder(96);
            for (long row = 0; row < len; row += 16)
            {
                sb.Clear();
                sb.Append("0x").Append((addr + (ulong)row).ToString("X8")).Append(':');
                long rowEnd = Math.Min(len, row + 16);
                bool any = false;
                for (long i = row; i < rowEnd; i++)
                {
                    if (!TryRead(addr + (ulong)i, 1, out ulong b))
                        break;
                    sb.Append(' ').Append(((byte)b).ToString("X2"));
                    dumped++;
                    any = true;
                }
                if (any) _hexdumpLines.Add(sb.ToString());
                else break;
            }
            return c.Return(Value.FromInt(dumped));
        }, "dbg.hexdump"));
        dbg.SetStr("assert", Value.FromFunction((ref CallContext c) =>
        {
            bool cond = c[0].IsTruthy;
            long code = !c.IsNoneOrNil(1) ? c.CheckInt(1) : 0;
            if (!cond) throw new LuaException(Value.FromText($"dbg.assert failed (code={code})"));
            return c.Return(Value.True);
        }, "dbg.assert"));
        dbg.SetStr("expect_eq", Value.FromFunction((ref CallContext c) =>
        {
            long actual = c.CheckInt(0);
            long expected = c.CheckInt(1);
            long code = !c.IsNoneOrNil(2) ? c.CheckInt(2) : 0;
            if (actual != expected)
                throw new LuaException(Value.FromText($"dbg.expect_eq failed (code={code}): actual=0x{actual:X}, expected=0x{expected:X}"));
            return c.Return(Value.True);
        }, "dbg.expect_eq"));
        dbg.SetStr("trace", Value.FromFunction((ref CallContext c) =>
        {
            _traceEvents.Add((c.CheckInt(0), c.CheckInt(1)));
            return c.Return(Value.True);
        }, "dbg.trace"));
        dbg.SetStr("breakpoint", Value.FromFunction((ref CallContext c) =>
        {
            _breakpoints.Add(c.CheckInt(0));
            return 0;
        }, "dbg.breakpoint"));
        engine.State.Globals.SetStr("dbg", Value.FromTable(dbg));

        // rtos.* namespace
        Table api = new Table();
        api.SetStr("ticks", Value.FromFunction((ref CallContext c) => c.Return(Value.FromInt(Ticks)), "rtos.ticks"));
        api.SetStr("sleep", Value.FromFunction((ref CallContext c) =>
        {
            long dt = c.CheckInt(0);
            if (dt < 0) return c.Return(Value.False);
            Ticks += dt;
            return c.Return(Value.True);
        }, "rtos.sleep"));
        api.SetStr("yield", Value.FromFunction((ref CallContext c) => { Yields++; return 0; }, "rtos.yield"));
        api.SetStr("log", Value.FromFunction((ref CallContext c) =>
        {
            _logs.Add((c.CheckText(0), c.CheckInt(1)));
            return c.Return(Value.True);
        }, "rtos.log"));
        api.SetStr("log_str", Value.FromFunction((ref CallContext c) =>
        {
            _strLogs.Add(c.CheckText(0));
            return c.Return(Value.True);
        }, "rtos.log_str"));
        api.SetStr("watchdog_checkin", Value.FromFunction((ref CallContext c) =>
        {
            _watchdogCheckins.Add(c.CheckInt(0));
            return c.Return(Value.True);
        }, "rtos.watchdog_checkin"));
        api.SetStr("queue_send", Value.FromFunction((ref CallContext c) =>
        {
            Enqueue(c.CheckInt(0), c.CheckInt(1));
            return c.Return(Value.FromInt(0));
        }, "rtos.queue_send"));
        api.SetStr("queue_recv", Value.FromFunction((ref CallContext c) =>
        {
            if (TryDequeue(c.CheckInt(0), out long val))
                return c.Return(Value.FromInt(val), Value.True);
            return c.Return(Value.FromInt(0), Value.False);
        }, "rtos.queue_recv"));
        api.SetStr("notify", Value.FromFunction((ref CallContext c) =>
        {
            _notifications[c.CheckInt(0)] = c.CheckInt(1);
            return c.Return(Value.FromInt(0));
        }, "rtos.notify"));
        api.SetStr("gpio_read", Value.FromFunction((ref CallContext c) =>
            c.Return(Value.FromInt(_gpio.GetValueOrDefault(c.CheckInt(0)))), "rtos.gpio_read"));
        api.SetStr("gpio_write", Value.FromFunction((ref CallContext c) =>
        {
            _gpio[c.CheckInt(0)] = c.CheckInt(1);
            return 0;
        }, "rtos.gpio_write"));
        api.SetStr("task_count", Value.FromFunction((ref CallContext c) =>
            c.Return(Value.FromInt(_tasks.Count)), "rtos.task_count"));
        api.SetStr("task_state", Value.FromFunction((ref CallContext c) =>
        {
            int idx = (int)c.CheckInt(0);
            return c.Return(Value.FromInt(idx >= 0 && idx < _tasks.Count ? _tasks[idx].State : -1));
        }, "rtos.task_state"));
        api.SetStr("task_priority", Value.FromFunction((ref CallContext c) =>
        {
            int idx = (int)c.CheckInt(0);
            return c.Return(Value.FromInt(idx >= 0 && idx < _tasks.Count ? _tasks[idx].Priority : -1));
        }, "rtos.task_priority"));
        api.SetStr("task_stack_bytes", Value.FromFunction((ref CallContext c) =>
        {
            int idx = (int)c.CheckInt(0);
            return c.Return(Value.FromInt(idx >= 0 && idx < _tasks.Count ? _tasks[idx].StackBytes : 0));
        }, "rtos.task_stack_bytes"));
        api.SetStr("task_guard_ok", Value.FromFunction((ref CallContext c) =>
        {
            int idx = (int)c.CheckInt(0);
            return c.Return(Value.FromBool(idx >= 0 && idx < _tasks.Count && _tasks[idx].GuardOk));
        }, "rtos.task_guard_ok"));
        api.SetStr("task_cycles", Value.FromFunction((ref CallContext c) =>
        {
            int idx = (int)c.CheckInt(0);
            return c.Return(Value.FromInt(idx >= 0 && idx < _tasks.Count ? _tasks[idx].Cycles : 0));
        }, "rtos.task_cycles"));
        api.SetStr("queue_depth", Value.FromFunction((ref CallContext c) =>
        {
            long ch = c.CheckInt(0);
            return c.Return(Value.FromInt(_queues.TryGetValue(ch, out var q) ? q.Count : 0));
        }, "rtos.queue_depth"));
        api.SetStr("dma_state", Value.FromFunction((ref CallContext c) =>
        {
            int ch = (int)c.CheckInt(0);
            return c.Return(Value.FromInt(DmaChannels.TryGetValue(ch, out var d) ? d.State : -1));
        }, "rtos.dma_state"));
        api.SetStr("dma_transferred", Value.FromFunction((ref CallContext c) =>
        {
            int ch = (int)c.CheckInt(0);
            return c.Return(Value.FromInt(DmaChannels.TryGetValue(ch, out var d) ? d.Transferred : 0));
        }, "rtos.dma_transferred"));
        api.SetStr("dma_capacity", Value.FromFunction((ref CallContext c) =>
        {
            int ch = (int)c.CheckInt(0);
            return c.Return(Value.FromInt(DmaChannels.TryGetValue(ch, out var d) ? d.Capacity : 0));
        }, "rtos.dma_capacity"));
        api.SetStr("dma_generation", Value.FromFunction((ref CallContext c) =>
        {
            int ch = (int)c.CheckInt(0);
            return c.Return(Value.FromInt(DmaChannels.TryGetValue(ch, out var d) ? d.Generation : 0));
        }, "rtos.dma_generation"));
        api.SetStr("hwsched_status", Value.FromFunction((ref CallContext c) =>
            c.Return(Value.FromInt(HwSchedStatus)), "rtos.hwsched_status"));
        api.SetStr("hwsched_ready_mask", Value.FromFunction((ref CallContext c) =>
            c.Return(Value.FromInt(HwSchedReadyMask)), "rtos.hwsched_ready_mask"));
        api.SetStr("hwsched_fault_mask", Value.FromFunction((ref CallContext c) =>
            c.Return(Value.FromInt(HwSchedFaultMask)), "rtos.hwsched_fault_mask"));
        api.SetStr("hwsched_seu_count", Value.FromFunction((ref CallContext c) =>
            c.Return(Value.FromInt(HwSchedSeuCount)), "rtos.hwsched_seu_count"));
        api.SetStr("hwsched_dispatches", Value.FromFunction((ref CallContext c) =>
        {
            int tid = (int)c.CheckInt(0);
            return c.Return(Value.FromInt(HwSchedDispatches.GetValueOrDefault(tid)));
        }, "rtos.hwsched_dispatches"));
        engine.State.Globals.SetStr("rtos", Value.FromTable(api));
    }
}

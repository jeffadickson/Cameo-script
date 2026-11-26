namespace Cameo.Embed;

/// <summary>
/// Built-in static type declarations and syscall catalog for <c>Cameo.Embed</c>:
/// first-class <c>Ptr</c>, explicit <c>axi.*</c> MMIO/bus access, <c>dbg.*</c> hardware
/// debugging helpers, and <c>rtos.*</c> NexRT kernel/inspection syscalls.
/// </summary>
public static class EmbedPrelude
{
    /// <summary>
    /// Standard <c>rtos.*</c> and host-backed <c>dbg.*</c> syscalls bound in NexRT (<c>jeffadickson/rtos</c>).
    /// </summary>
    public static IReadOnlyList<EmbedHostImport> StandardSyscalls { get; } =
    [
        new((ushort)EmbedStandardHostId.Ticks, "rtos.ticks", 0),
        new((ushort)EmbedStandardHostId.Sleep, "rtos.sleep", 1),
        new((ushort)EmbedStandardHostId.Yield, "rtos.yield", 0),
        new((ushort)EmbedStandardHostId.Log, "rtos.log", 2),
        new((ushort)EmbedStandardHostId.LogStr, "rtos.log_str", 1),
        new((ushort)EmbedStandardHostId.WatchdogCheckin, "rtos.watchdog_checkin", 1),
        new((ushort)EmbedStandardHostId.QueueSend, "rtos.queue_send", 0xFF),
        new((ushort)EmbedStandardHostId.QueueRecv, "rtos.queue_recv", 0xFF),
        new((ushort)EmbedStandardHostId.Notify, "rtos.notify", 2),
        new((ushort)EmbedStandardHostId.GpioRead, "rtos.gpio_read", 1),
        new((ushort)EmbedStandardHostId.GpioWrite, "rtos.gpio_write", 2),
        new((ushort)EmbedStandardHostId.TaskCount, "rtos.task_count", 0),
        new((ushort)EmbedStandardHostId.TaskState, "rtos.task_state", 1),
        new((ushort)EmbedStandardHostId.TaskPriority, "rtos.task_priority", 1),
        new((ushort)EmbedStandardHostId.TaskStackBytes, "rtos.task_stack_bytes", 1),
        new((ushort)EmbedStandardHostId.TaskGuardOk, "rtos.task_guard_ok", 1),
        new((ushort)EmbedStandardHostId.TaskCycles, "rtos.task_cycles", 1),
        new((ushort)EmbedStandardHostId.QueueDepth, "rtos.queue_depth", 1),
        new((ushort)EmbedStandardHostId.DbgLogHex, "dbg.log_hex", 2),
        new((ushort)EmbedStandardHostId.DbgHexdump, "dbg.hexdump", 2),
        new((ushort)EmbedStandardHostId.DbgTrace, "dbg.trace", 2),
        new((ushort)EmbedStandardHostId.DbgBreakpoint, "dbg.breakpoint", 1),
        new((ushort)EmbedStandardHostId.DmaState, "rtos.dma_state", 1),
        new((ushort)EmbedStandardHostId.DmaTransferred, "rtos.dma_transferred", 1),
        new((ushort)EmbedStandardHostId.DmaCapacity, "rtos.dma_capacity", 1),
        new((ushort)EmbedStandardHostId.DmaGeneration, "rtos.dma_generation", 1),
        new((ushort)EmbedStandardHostId.HwSchedStatus, "rtos.hwsched_status", 0),
        new((ushort)EmbedStandardHostId.HwSchedReadyMask, "rtos.hwsched_ready_mask", 0),
        new((ushort)EmbedStandardHostId.HwSchedFaultMask, "rtos.hwsched_fault_mask", 0),
        new((ushort)EmbedStandardHostId.HwSchedSeuCount, "rtos.hwsched_seu_count", 0),
        new((ushort)EmbedStandardHostId.HwSchedDispatches, "rtos.hwsched_dispatches", 1),
    ];

    /// <summary>First-class <c>Ptr</c> userdata record and <c>ptr(addr)</c> constructor declarations.</summary>
    public const string PtrDeclarationSource = """
        global record Ptr
           userdata
           addr: function(Ptr): integer
           is_null: function(Ptr): boolean
           is_aligned: function(Ptr, integer): boolean
           align_up: function(Ptr, integer): Ptr
           add: function(Ptr, integer): Ptr
           sub: function(Ptr, integer): Ptr
           diff: function(Ptr, Ptr): integer
           read8: function(Ptr): integer
           read16: function(Ptr): integer
           read32: function(Ptr): integer
           read64: function(Ptr): integer
           write8: function(Ptr, integer): boolean
           write16: function(Ptr, integer): boolean
           write32: function(Ptr, integer): boolean
           write64: function(Ptr, integer): boolean
           read8_at: function(Ptr, integer): integer
           read16_at: function(Ptr, integer): integer
           read32_at: function(Ptr, integer): integer
           read64_at: function(Ptr, integer): integer
           write8_at: function(Ptr, integer, integer): boolean
           write16_at: function(Ptr, integer, integer): boolean
           write32_at: function(Ptr, integer, integer): boolean
           write64_at: function(Ptr, integer, integer): boolean
           bits32_at: function(Ptr, integer, integer, integer): integer
           rmw_bits32_at: function(Ptr, integer, integer, integer, integer): integer, boolean

           of: function(integer): Ptr
           null: function(): Ptr

           metamethod __add: function(Ptr, integer): Ptr
           metamethod __sub: function(Ptr, integer): Ptr
           metamethod __eq: function(Ptr, Ptr): boolean
           metamethod __lt: function(Ptr, Ptr): boolean
           metamethod __le: function(Ptr, Ptr): boolean
        end
        global ptr: function(integer): Ptr
        """;

    /// <summary>Explicit <c>axi.*</c> AXI4-Lite / AXI4 MMIO, watchpoint, bus-trace, and static scratchpad declarations.</summary>
    public const string AxiDeclarationSource = """
        global record AxiBusApi
           read8: function(addr: integer | Ptr): integer, boolean
           read16: function(addr: integer | Ptr): integer, boolean
           read32: function(addr: integer | Ptr): integer, boolean
           read64: function(addr: integer | Ptr): integer, boolean
           write8: function(addr: integer | Ptr, val: integer): boolean
           write16: function(addr: integer | Ptr, val: integer): boolean
           write32: function(addr: integer | Ptr, val: integer): boolean
           write64: function(addr: integer | Ptr, val: integer): boolean
           rmw32: function(addr: integer | Ptr, mask: integer, val: integer): integer, boolean
           poll32: function(addr: integer | Ptr, mask: integer, expect: integer, max_polls: integer): integer, boolean
           burst_read32: function(addr: integer | Ptr, count: integer, scratch_offset?: integer): integer, boolean
           burst_write32: function(addr: integer | Ptr, count: integer, scratch_offset?: integer): integer, boolean
           scratch_get32: function(index: integer): integer
           scratch_set32: function(index: integer, val: integer): boolean
           scratch_len: function(): integer
           fence: function()
           window_check: function(addr: integer | Ptr, size: integer, write: boolean): boolean
           watch: function(addr: integer | Ptr, size: integer, flags: integer): integer
           unwatch: function(id: integer): boolean
           watch_hits: function(): integer
           trace_enable: function(enabled: boolean): boolean
           trace_count: function(): integer
        end
        global axi: AxiBusApi
        """;

    /// <summary>Hardware debugging and bitfield <c>dbg.*</c> declarations.</summary>
    public const string DbgDeclarationSource = """
        global record DbgApi
           bits: function(val: integer, lsb: integer, width: integer): integer
           set_bits: function(val: integer, lsb: integer, width: integer, field_val: integer): integer
           bit: function(val: integer, bit_idx: integer): boolean
           popcount: function(val: integer): integer
           clz32: function(val: integer): integer
           ctz32: function(val: integer): integer
           bswap32: function(val: integer): integer
           hexdump: function(addr: integer | Ptr, byte_len: integer): integer
           log_hex: function(tag: string, val: integer): boolean
           assert: function(cond: boolean, code?: integer): boolean
           expect_eq: function(actual: integer, expected: integer, code?: integer): boolean
           trace: function(event_id: integer, arg: integer): boolean
           breakpoint: function(id: integer)
        end
        global dbg: DbgApi
        """;

    /// <summary>NexRT kernel, thread/stack/queue, DMA, and HWSched inspection <c>rtos.*</c> declarations.</summary>
    public const string RtosDeclarationSource = """
        global record RtosKernelApi
           ticks: function(): integer
           sleep: function(ticks: integer): boolean
           yield: function()
           log: function(tag: string, value: integer): boolean
           log_str: function(msg: string): boolean
           watchdog_checkin: function(task_id: integer): boolean
           queue_send: function(channel: integer, value: integer, timeout?: integer): integer
           queue_recv: function(channel: integer, timeout?: integer): integer, boolean
           notify: function(target: integer, value: integer): integer
           gpio_read: function(pin: integer): integer
           gpio_write: function(pin: integer, value: integer)
           task_count: function(): integer
           task_state: function(index: integer): integer
           task_priority: function(index: integer): integer
           task_stack_bytes: function(index: integer): integer
           task_guard_ok: function(index: integer): boolean
           task_cycles: function(index: integer): integer
           queue_depth: function(channel: integer): integer
           dma_state: function(channel: integer): integer
           dma_transferred: function(channel: integer): integer
           dma_capacity: function(channel: integer): integer
           dma_generation: function(channel: integer): integer
           hwsched_status: function(): integer
           hwsched_ready_mask: function(): integer
           hwsched_fault_mask: function(): integer
           hwsched_seu_count: function(): integer
           hwsched_dispatches: function(tid: integer): integer
        end
        global rtos: RtosKernelApi
        """;
}

# Cameo.Embed — static C++20 VM, pointers, AXI MMIO, and NexRT integration

`Cameo.Embed` compiles a statically verified, zero-heap subset of Cameo — **no physical units,
no dynamic dictionaries/maps, no heap strings, integer-only by default** — into a compact,
CRC-32C-protected bytecode package (`CEMB` v1) that runs on a standalone **C++20 static VM**
(`embed/cameo_embed/include/cameo_embed_vm.hpp`) with a first-class NexRT adapter
(`embed/cameo_embed/include/cameo_embed_nexrt.hpp`, integrated directly into
`jeffadickson/rtos`).

Designed specifically for **hardware bring-up, register/AXI bus debugging, and deterministic
embedded control** without reflashing firmware.

## Design goals and static contract

- **Zero dynamic allocation**: no `malloc`, `calloc`, `realloc`, `free`, `operator new`, or
  `operator delete`. All VM storage (value stack, call frames, globals, boxes, closures,
  fixed record/array pool, AXI scratchpad, MMIO window table, hot-load receiver) is fixed-capacity
  `.bss` inside `StaticVm<...>` and `PackageReceiver<...>`.
- **Zero exceptions, zero RTTI, zero runtime C++ constructors**: compiles cleanly under
  `-std=c++20 -ffreestanding -fno-exceptions -fno-rtti -fno-builtin -Wall -Wextra -Werror -pedantic`
  and passes `jeffadickson/rtos`'s `Rtos.Tools` forbidden-symbol and `.init_array` audit.
- **Integer-only by default (`CAMEO_EMBED_ENABLE_FLOAT=0`)**: zero `double` operations, zero
  soft-float helpers, and zero `libm` footprint for RV32IM and ARM Cortex-M3 targets. Opt in to
  floats with `--floats` / `-DCAMEO_EMBED_ENABLE_FLOAT=1`.
- **Compact 32-bit value mode (`CAMEO_EMBED_COMPACT_32=1`)**: shrinks `cameo::embed::Value` from
  16 bytes to 8 bytes (~50% SRAM reduction for stack, globals, and record pools on 32-bit MCUs;
  mutually exclusive with `CAMEO_EMBED_ENABLE_FLOAT=1`).
- **Flash `.rodata`, persistent `JournalFs` slots, AND live UART hot-loading (`CEMB` v1)**: 8-byte
  aligned, little-endian, Castagnoli CRC-32C (`0x82F63B78`) verified. Load from Flash `.rodata`,
  boot from power-fail-atomic `RTOS_STORAGE` `JournalFsScriptStore` slots (`script save/boot/erase`),
  or stream raw bytes / ASCII hex over UART via `PackageReceiver` / `MonitorBridge` / `cameo embed --upload`.
- **Deterministic execution & on-target debugging**: every `run_main()` / `call_export()` /
  `axi.poll32()` enforces a hard instruction `step_budget` (`Status::StepLimitExceeded`) and
  external `volatile bool*` abort flag (`Status::Aborted`). Resumable PC breakpoints, single-stepping
  (`start`/`step`/`cont`/`regs`), hardware AXI watchpoints (`WatchFlags::kBreak`), and host
  `.cemb.map.json` source-line mapping enable interactive debugging over UART.

## Embedded surface: `Ptr`, `axi.*`, `dbg.*`, `rtos.*`

### First-class pointers & struct/bitfield overlays (`Ptr`)
- `ptr(addr)` / `Ptr.of(addr)` / `Ptr.null()`
- Arithmetic & comparisons: `p + offset`, `p - offset`, `p.diff(q)`, `p == q`, `p < q`, `p <= q`
- Alignment & inspection: `p.addr()`, `p.is_null()`, `p.is_aligned(align)`, `p.align_up(align)`
- Policy-checked memory access: `p.read8/16/32/64()`, `p.write8/16/32/64(val)`
- Zero-overhead typed struct/bitfield overlays: `p.read8/16/32/64_at(off)`, `p.write8/16/32/64_at(off, val)`,
  `p.bits32_at(off, lsb, width)`, `p.rmw_bits32_at(off, lsb, width, field_val)` (writes rejected under `--read-only`)
- Typed hardware register-map generator: `cameo embed --gen-regmap <svd.xml|riscue|mps2> --out-regmap <file.cameo>`
  emits zero-metatable Cameo peripheral overlays from CMSIS-SVD XML or built-in NexRT board specs.

### Explicit AXI MMIO, watchpoints, bus trace & burst transfers (`axi.*`)
Backed by a configurable board `MemoryWindow` policy table (read-only vs. read-write windows,
min/max transaction width for AXI4-Lite 32-bit slaves, strict alignment, volatile memory
barriers, and runtime `set_axi_read_only(true)` lock):
- `axi.read8/16/32/64(addr) -> (val, ok)` and `axi.write8/16/32/64(addr, val) -> ok`
- `axi.rmw32(addr, mask, val) -> (new_val, ok)` (read-modify-write)
- `axi.poll32(addr, mask, expect, max_polls) -> (last_val, matched)` (step-budgeted & abortable)
- `axi.burst_read32(addr, count, scratch_off?)` / `axi.burst_write32(addr, count, scratch_off?)`
- `axi.scratch_get32(i)` / `axi.scratch_set32(i, v)` / `axi.scratch_len()` (static scratchpad)
- `axi.fence()` (full memory barrier) and `axi.window_check(addr, size, write) -> boolean`
- Watchpoints & bus trace: `axi.watch(addr, size, flags) -> id`, `axi.unwatch(id)`,
  `axi.watch_hits()`, `axi.trace_enable(on)`, `axi.trace_count()` (`BusTraceRing` -> `script bustrace`,
  watchpoint hits forward event `0x0101` into NexRT `rtos::trace::TraceRing`, and
  `cameo embed --trace-to-fst <log> --out-fst <wave.fst>` converts bus/trace logs to GTKWave `.fst`).

### Hardware debugging helpers (`dbg.*`)
- Bitfields & bit ops: `dbg.bits(v, lsb, width)`, `dbg.set_bits(v, lsb, width, f)`, `dbg.bit(v, i)`,
  `dbg.popcount(v)`, `dbg.clz32(v)`, `dbg.ctz32(v)`, `dbg.bswap32(v)`
- Live output (zero heap): `dbg.log_hex(tag, val)`, `dbg.hexdump(addr_or_ptr, bytes)`
- Verification & tracing: `dbg.assert(cond, code?)`, `dbg.expect_eq(actual, expected, code?)`,
  `dbg.trace(event_id, arg)` (binds to NexRT `TraceRing`), `dbg.breakpoint(id)`

### NexRT kernel, DMA & HWSched inspection (`rtos.*`)
- Runtime: `ticks`, `sleep`, `yield`, `log`, `log_str`, `watchdog_checkin`, `queue_send`,
  `queue_recv`, `notify`, `gpio_read`, `gpio_write`
- Inspection: `task_count`, `task_state`, `task_priority`, `task_stack_bytes`, `task_guard_ok`,
  `task_cycles`, `queue_depth`, `dma_state`, `dma_transferred`, `dma_capacity`, `dma_generation`,
  `hwsched_status`, `hwsched_ready_mask`, `hwsched_fault_mask`, `hwsched_seu_count`, `hwsched_dispatches`

## Excluded constructs (compile-time `CAMEOEMBxxx` errors)

- Physical units (`units` / `SharpUnits`), `safenum`, `FloatArray`, `List`, `Dict` — `CAMEOEMB001`
- Dynamic dictionaries/maps (`{K: V}`, `{ [k] = v }`, non-integer dynamic table writes) — `CAMEOEMB018`
- Dynamic string concatenation / interpolation (`..`, `$"{...}"`) — `CAMEOEMB002`
- Varargs (`...`), generic iterators (`for x in ...`), `using`/`<close>`, unchecked dot calls — `CAMEOEMB003`–`CAMEOEMB006`
- Floating-point constants/ops unless `--floats` / `AllowFloats = true` — `CAMEOEMB014`
- Any AXI/Ptr writes (`write*`, `write*_at`, `rmw32`, `rmw_bits32_at`, `burst_write32`) when `--read-only` / `AllowMemWrites = false` — `CAMEOEMB019`

## CLI and C# API

```bash
# Compile with .cemb, C++20 header, UART hex, and source-line debug map sidecar
cameo embed \
  --boot-only-closures \
  --symbol kAxiDiag \
  --namespace firmware::diag \
  --out-bin build/diag.cemb \
  --out-header build/diag.hpp \
  --out-hex build/diag.hex \
  --out-map build/diag.cemb.map.json \
  examples/17-embed-axi-debug.cameo

# Generate zero-metatable typed Cameo peripheral overlays from CMSIS-SVD or built-in NexRT specs
cameo embed --gen-regmap mps2 --out-regmap build/mps2_regs.cameo

# Stream-upload over TCP/process/device endpoint, optionally persist to a JournalFs slot and run
cameo embed --upload tcp://127.0.0.1:4321 --save-slot 0 --run-entry diagnose_bus diag.cameo

# Convert captured MonitorBridge bustrace or NXTR binary TraceRing logs into GTKWave .fst
cameo embed --trace-to-fst build/bustrace.txt --out-fst build/bustrace.fst
```

From C# (`src/Cameo.Embed`):

```csharp
using Cameo.Embed;

var result = EmbedCompiler.CompileFile("diag.cameo", new EmbedCompileOptions
{
    BootOnlyClosures = true,
});
if (result.Success)
{
    File.WriteAllBytes("diag.cemb", result.Image!.Bytes);
    File.WriteAllText("diag.hpp", result.Image.EmitCppHeader("kAxiDiag", "firmware::diag"));
    File.WriteAllText("diag.cemb.map.json", result.Image.DebugMap!.ToJson());
    string uartHex = result.Image.ToHexString();
}
```

Host interactive debugging (`EmbedRemoteDebugSession`) maps source lines in `.cemb.map.json` to
`(proto, pc)` breakpoints over any `IEmbedLineTransport` (`TcpLineTransport`, `ProcessLineTransport`,
or `StreamLineTransport`), supporting `SetLineBreakpoint`, `Start`, `StepInstruction`, `StepLine`,
`Continue`, `ReadRegisters`, and `CaptureBusTrace`.

## Verification

- `bash embed/cameo_embed/run.sh`: builds & runs integer-only, compact-32 (`sizeof(Value) == 8`),
  and float+math C++20 unit tests, freestanding static-contract probes, `nm -C`/`objdump -h`
  audits, NexRT header integration + **byte-for-byte header parity check** (when `NEXRT_DIR` is
  set), and RV32IM / Cortex-M3 cross-compiled object audits (both 64-bit and compact-32).
- `tests/Cameo.Tests/CameoEmbedTests.cs`: validates exclusions (units/Dict/List/maps/floats/
  read-only writes including `write*_at`/`rmw_bits32_at`), flagship `17-embed-axi-debug.cameo`
  equivalence, SVD & built-in board regmap generation + compilation, `BusTraceFstExporter`
  (bustrace text + `NXTR` binary -> `.fst` verified via `FstReader`), `EmbedUploader` +
  `EmbedRemoteDebugSession` source-line stepping, CLI `.cemb`/`.hpp`/`.hex`/`.map.json` +
  freestanding `g++` check, and CsCheck differential integer/pointer/bitfield & CRC-32C properties.
- `jeffadickson/rtos`: host `ctest` (`cameo_embed` covering Flash `.rodata`, UART hot-load,
  `JournalFsScriptStore` cold-reboot persistence, PC breakpoints/stepping, watchpoints ->
  `TraceRing`, `bustrace`, DMA/HWSched bindings), `formal/CameoEmbedProof.dfy`, and live **QEMU
  ARM Cortex-M3 (`mps2-an385`)** firmware (`arm-cameo-embed` + `arm_cameo_embed` passing `Rtos.Tools`
  zero-heap/`.init_array` audit and live UART hot-load/stepping/bustrace via `NexRT.ArmSim`).


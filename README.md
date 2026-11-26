# Cameo

**Cameo** is a statically typed, deterministic, embeddable scripting language and toolchain on .NET 11 — with a companion zero-heap freestanding C++20 embedded target (**`Cameo.Embed`**) designed for hardware bring-up, register/AXI bus debugging, and real-time firmware (such as [NexRT](https://github.com/jeffadickson/rtos)).

## Highlights

- **Modern typed surface**: `fn`, `let`/`var`, braced blocks, `record` with required/optional (`field?: T`) fields and inline methods, structural & nominal `interface`, `enum`, `match` (OR-patterns, `is Type(binding)`, exhaustive enum checking), `Result<T, E>`, `try`/`catch`, `using`, pipelines `|>`, `??`, `?.`, `/regex/flags`, `export`/`import`, `List<T>`, `Dict<K, V>`, comprehensions, and `cameo fmt [--check]`.
- **Deterministic & safe by design**:
  - Strictly deterministic `--hermetic` profile (`CameoEngine.CreateHermetic`), cycle-safe deep `freeze()`, deterministic `parallel.map`, explicit-seed `math.rng(seed)`.
  - Hosted safety: `FilePolicy`, socket-level IP-validated `NetworkPolicy`, deterministic `MaxSteps` budgets, `BytecodeValidator`, and `Sandboxed` profile.
- **Scientific & engineering standard libraries**:
  - Physical units with ISO 80000 compile-time affine safety (`Cameo.Units` / [SharpUnits](https://github.com/jeffadickson/SharpUnits)).
  - Checked/saturating/wrapping integers, SIMD batch ops, Q32.32 `Fixed64`, and IEEE 754 `Decimal64`/`Decimal128` (`Cameo.SafeNumerics` / [SharpSafeNumerics](https://github.com/jeffadickson/SharpSafeNumerics)).
  - `FloatArray` (SIMD vector math, stats, quantiles, 1D interpolation, freeze), HDF5 (`Cameo.Hdf5`), GTKWave `.fst` waveforms (`Cameo.Fst`), and IL JIT tiering (`Cameo.Jit`).
- **Typed .NET host interop**: `Register`, `Expose` (`[Expose]`/`[Hide]`), `DefineValue`, and `DeclareGlobal` wired directly into the static type checker.
- **`Cameo.Embed` — static C++20 embedded VM & hardware debugger**:
  - Compiles a strict zero-heap subset (no units, no dynamic dictionaries/maps, no heap strings, integer-only by default) to 8-byte-aligned Castagnoli CRC-32C `CEMB` v1 packages, freestanding C++20 `.rodata` headers, UART hex streams, and `.cemb.map.json` source-line debug maps.
  - Standalone zero-heap, zero-exceptions, zero-RTTI, zero-constructor C++20 `StaticVm` (`embed/cameo_embed/include/cameo_embed_vm.hpp`) with optional 8-byte `CAMEO_EMBED_COMPACT_32=1` mode (~50% SRAM reduction on 32-bit MCUs).
  - First-class `Ptr` values & zero-overhead struct/bitfield overlays (`read/write*_at`, `bits32_at`, `rmw_bits32_at`), CMSIS-SVD / board register-map generator (`--gen-regmap`), explicit `axi.*` MMIO/burst/watchpoints/`BusTraceRing` → GTKWave `.fst` (`--trace-to-fst`), `dbg.*` bitfields/hexdump/trace, `rtos.*` task/queue/DMA/HWSched inspection, on-target PC breakpoints & resumable single-stepping, power-fail-atomic `JournalFsScriptStore` flash slots, and live UART hot-loading (`--upload`) verified on QEMU ARM Cortex-M3 (`mps2-an385`).

## Repository Layout

| Path | Description |
| --- | --- |
| `src/Cameo.Runtime` | Core bytecode VM, parser, static type checker, compiler, stdlib, host interop, security policies |
| `src/Cameo` | Managed `CameoEngine`, formatter (`CameoFormatter`), and command layer (`Cameo.Language`) |
| `src/Cameo.Cli` | `cameo` CLI (`run`, `check`, `test`, `fmt`, `dump`, `repl`, `embed`, `-e`/`-n`/`-p`) |
| `src/Cameo.Embed` | Embedded profile compiler, `CEMB` v1 emitter, SVD regmap generator, FST exporter, uploader & remote debugger |
| `src/Cameo.Jit` | .NET IL JIT compiler (`IlCompiler`, `JitLibrary`) |
| `src/Cameo.Fst` | GTKWave `.fst` waveform reader/writer |
| `src/Cameo.Hdf5` | HDF5 scientific data I/O |
| `src/Cameo.Units` | Physical units bridge (`external/SharpUnits`) |
| `src/Cameo.SafeNumerics` | Safe integers, `Fixed64`, `Decimal64`/`128` bridge (`external/SharpSafeNumerics`) |
| `embed/cameo_embed` | Standalone freestanding C++20 `StaticVm` + NexRT adapter (`cameo_embed_vm.hpp`, `cameo_embed_nexrt.hpp`) |
| `examples/` | 17 runnable Cameo programs + managed `CameoHost` sample |
| `editors/vscode/` | VS Code syntax highlighting (`source.cameo`) and save-time diagnostics |
| `docs/` | Language guide (`language.md`), embedded VM guide (`embed.md`), authoring, embedding, JIT |
| `tests/Cameo.Tests` | xUnit, CsCheck, and Hedgehog test suite |

## Quick Start

Clone with submodules (`SharpUnits` and `SharpSafeNumerics`):

```bash
git clone --recurse-submodules https://github.com/jeffadickson/Cameo-script.git
cd Cameo-script
./check.sh
```

Run a script or compile an embedded hardware diagnostic image:

```bash
dotnet run --project src/Cameo.Cli -- run --hermetic examples/16-hermetic-engineering.cameo
dotnet run --project src/Cameo.Cli -- embed --boot-only-closures --out-header diag.hpp --out-hex diag.hex --out-map diag.map.json examples/17-embed-axi-debug.cameo
```

See [docs/language.md](docs/language.md) and [docs/embed.md](docs/embed.md) for full documentation.

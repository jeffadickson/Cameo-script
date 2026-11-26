# AGENTS.md — Cameo

You are working on **Cameo**: a statically typed, deterministic, embeddable scripting language
and toolchain on .NET 11, including **`Cameo.Embed`** — a static profile compiler (`cameo embed`)
and standalone zero-heap freestanding C++20 VM (`embed/cameo_embed`) targeting NexRT
(`jeffadickson/rtos`) and bare-metal 32/64-bit targets.

## Toolchain & Verification

```bash
./check.sh
```

`check.sh` runs:
1. `dotnet build Cameo.slnx -c Release` (requires .NET 11 SDK + `external/SharpUnits` and `external/SharpSafeNumerics` submodules).
2. `dotnet test Cameo.slnx -c Release --no-build` (`tests/Cameo.Tests`).
3. `bash examples/run.sh` (all 17 Cameo examples, `--hermetic`, `fmt --check`, `cameo embed`, and `CameoHost`).
4. `bash embed/cameo_embed/run.sh` (C++20 integer-only, compact-32 `sizeof(Value) == 8`, and float+math unit tests; freestanding static-contract & zero-`.data` audits on x86_64, RV32IM, and Cortex-M3 when toolchains are installed; NexRT header integration and byte-for-byte header parity check when `NEXRT_DIR` is set).
5. `node editors/vscode/test.js` (Cameo TextMate grammar & save-time JSON diagnostic tests).

## Key Rules

- **`Cameo.Embed` is the single canonical embedded VM/compiler** (no separate `Cameo.Rtos`).
- Keep `embed/cameo_embed/include/cameo_embed_vm.hpp` and `embed/cameo_embed/include/cameo_embed_nexrt.hpp`
  strictly zero-heap, zero-exceptions, zero-RTTI, zero-`.data` (100% `.bss`), freestanding-safe
  (`-ffreestanding -nostdlib`), and byte-for-byte identical to `jeffadickson/rtos`'s
  `include/cameo_embed_vm.hpp` and `include/rtos_cameo_embed.hpp`.
- Verify every change with `./check.sh` (and `NEXRT_DIR=../rtos ./check.sh` when `rtos` is adjacent)
  before pushing, and report both verified and unverified items accurately.

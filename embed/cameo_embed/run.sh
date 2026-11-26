#!/usr/bin/env bash
# Builds and verifies the standalone static C++20 Cameo.Embed VM:
#   1. Native C++20 unit tests (default integer-only zero-float mode AND float+math mode)
#   2. Freestanding static-contract objects (integer-only default AND float+math)
#   3. ELF/object symbol audit matching jeffadickson/rtos Rtos.Tools forbidden-symbol policy
#   4. Builds cameo_embed_runner host tool for C# differential tests
#   5. Optional compilation + symbol audit against real jeffadickson/rtos headers if NEXRT_DIR is set
#   6. Optional RV32IM and ARM Cortex-M3 freestanding cross-compilation audits if toolchains exist
set -euo pipefail
cd "$(dirname "$0")"

CXX="${CXX:-g++}"
NM="${NM:-nm}"
OBJDUMP="${OBJDUMP:-objdump}"
OUT_DIR="${OUT_DIR:-build}"
mkdir -p "$OUT_DIR"

COMMON_FLAGS=(-std=c++20 -O2 -fno-exceptions -fno-rtti -Wall -Wextra -Werror -pedantic -Iinclude)

echo "== Cameo.Embed C++20: compiling integer-only unit tests (default zero-float, zero-libm)"
"$CXX" "${COMMON_FLAGS[@]}" tests/cameo_embed_unit_tests.cpp -o "$OUT_DIR/cameo_embed_unit_tests"
"$OUT_DIR/cameo_embed_unit_tests"

echo "== Cameo.Embed C++20: compiling compact-32 integer-only unit tests (sizeof(Value)==8)"
"$CXX" "${COMMON_FLAGS[@]}" -DCAMEO_EMBED_COMPACT_32=1 \
    tests/cameo_embed_unit_tests.cpp -o "$OUT_DIR/cameo_embed_unit_tests_c32"
"$OUT_DIR/cameo_embed_unit_tests_c32"

echo "== Cameo.Embed C++20: compiling float+math unit tests"
"$CXX" "${COMMON_FLAGS[@]}" -DCAMEO_EMBED_ENABLE_FLOAT=1 -DCAMEO_EMBED_ENABLE_MATH=1 \
    tests/cameo_embed_unit_tests.cpp -lm -o "$OUT_DIR/cameo_embed_unit_tests_float"
"$OUT_DIR/cameo_embed_unit_tests_float"

echo "== Cameo.Embed C++20: compiling freestanding static-contract objects"
"$CXX" "${COMMON_FLAGS[@]}" -ffreestanding -fno-builtin -ffunction-sections -fdata-sections -Os \
    -c tests/static_contract_probe.cpp -o "$OUT_DIR/static_contract_intonly.o"
"$CXX" "${COMMON_FLAGS[@]}" -ffreestanding -fno-builtin -ffunction-sections -fdata-sections -Os \
    -DCAMEO_EMBED_COMPACT_32=1 \
    -c tests/static_contract_probe.cpp -o "$OUT_DIR/static_contract_compact32.o"
"$CXX" "${COMMON_FLAGS[@]}" -ffreestanding -fno-builtin -ffunction-sections -fdata-sections -Os \
    -DCAMEO_EMBED_ENABLE_FLOAT=1 -DCAMEO_EMBED_ENABLE_MATH=1 \
    -c tests/static_contract_probe.cpp -o "$OUT_DIR/static_contract_float.o"

# Exact forbidden-symbol regex from jeffadickson/rtos tools/Rtos.Tools/Program.cs
FORBIDDEN_RE='\b(malloc|calloc|realloc|free|aligned_alloc|memalign|valloc|pvalloc|sbrk|_sbrk|_malloc_r|_calloc_r|_realloc_r|_free_r|_memalign_r|_sbrk_r|__cxa_guard_acquire|__cxa_atexit|__cxa_throw|__gxx_personality_v0)\b|operator (new|delete)|_GLOBAL__sub_I[._]|__cxx_global_var_init|__static_initialization_and_destruction'

audit_object() {
    local obj="$1"
    local nm_tool="${2:-$NM}"
    local objdump_tool="${3:-$OBJDUMP}"
    local syms
    syms=$("$nm_tool" -C "$obj")
    if ! printf '%s\n' "$syms" | grep -Eq '^[[:space:]]*[0-9a-fA-F]+[[:space:]]+[A-Za-z][[:space:]]+'; then
        echo "FAIL: $obj has no defined symbols" >&2
        exit 1
    fi
    if printf '%s\n' "$syms" | grep -Eq "$FORBIDDEN_RE"; then
        echo "FAIL: static-contract audit failed for $obj:" >&2
        printf '%s\n' "$syms" | grep -E "$FORBIDDEN_RE" >&2
        exit 1
    fi
    if "$objdump_tool" -h "$obj" | grep -Eq '\.(init_array|preinit_array|fini_array|ctors)'; then
        echo "FAIL: runtime C++ constructor/destructor section found in $obj" >&2
        exit 1
    fi
    echo "PASS: static-contract audit clean for $obj"
}

audit_object "$OUT_DIR/static_contract_intonly.o"
audit_object "$OUT_DIR/static_contract_compact32.o"
audit_object "$OUT_DIR/static_contract_float.o"

echo "== Cameo.Embed C++20: compiling host differential runner"
"$CXX" "${COMMON_FLAGS[@]}" -DCAMEO_EMBED_ENABLE_FLOAT=1 -DCAMEO_EMBED_ENABLE_MATH=1 \
    tools/cameo_embed_runner.cpp -lm -o "$OUT_DIR/cameo_embed_runner"

if [ -n "${NEXRT_DIR:-}" ] && [ -f "$NEXRT_DIR/include/rtos.hpp" ]; then
    echo "== Cameo.Embed C++20: verifying NexRT header integration ($NEXRT_DIR)"
    "$CXX" "${COMMON_FLAGS[@]}" -ffunction-sections -fdata-sections -Os \
        -DRTOS_SYNC=1 -DRTOS_LOGGING=1 -DRTOS_TRACE=1 -DRTOS_MONITOR=1 -DRTOS_DMA=1 -DRTOS_STORAGE=1 -DRTOS_HW_SCHED=1 \
        -I"$NEXRT_DIR/include" \
        -c tests/nexrt_compat_probe.cpp -o "$OUT_DIR/nexrt_compat_probe.o"
    audit_object "$OUT_DIR/nexrt_compat_probe.o"

    if [ -f "$NEXRT_DIR/include/cameo_embed_vm.hpp" ] && [ -f "$NEXRT_DIR/include/rtos_cameo_embed.hpp" ]; then
        echo "== Cameo.Embed C++20: checking byte-for-byte header parity with $NEXRT_DIR/include"
        cmp -s include/cameo_embed_vm.hpp "$NEXRT_DIR/include/cameo_embed_vm.hpp" || {
            echo "FAIL: cameo_embed_vm.hpp out of sync with $NEXRT_DIR/include/cameo_embed_vm.hpp" >&2
            exit 1
        }
        cmp -s include/cameo_embed_nexrt.hpp "$NEXRT_DIR/include/rtos_cameo_embed.hpp" || {
            echo "FAIL: cameo_embed_nexrt.hpp out of sync with $NEXRT_DIR/include/rtos_cameo_embed.hpp" >&2
            exit 1
        }
        echo "PASS: NexRT header parity verified (byte-for-byte identical)"
    fi
fi

if command -v riscv64-unknown-elf-g++ >/dev/null 2>&1; then
    echo "== Cameo.Embed C++20: cross-compiling freestanding RV32IM objects (64-bit & compact-32)"
    riscv64-unknown-elf-g++ "${COMMON_FLAGS[@]}" -march=rv32im -mabi=ilp32 \
        -ffreestanding -fno-builtin -ffunction-sections -fdata-sections -Os \
        -c tests/static_contract_probe.cpp -o "$OUT_DIR/static_contract_rv32im.o"
    audit_object "$OUT_DIR/static_contract_rv32im.o" riscv64-unknown-elf-nm riscv64-unknown-elf-objdump
    riscv64-unknown-elf-g++ "${COMMON_FLAGS[@]}" -march=rv32im -mabi=ilp32 -DCAMEO_EMBED_COMPACT_32=1 \
        -ffreestanding -fno-builtin -ffunction-sections -fdata-sections -Os \
        -c tests/static_contract_probe.cpp -o "$OUT_DIR/static_contract_rv32im_c32.o"
    audit_object "$OUT_DIR/static_contract_rv32im_c32.o" riscv64-unknown-elf-nm riscv64-unknown-elf-objdump
fi

if command -v arm-none-eabi-g++ >/dev/null 2>&1; then
    echo "== Cameo.Embed C++20: cross-compiling freestanding ARM Cortex-M3 objects (64-bit & compact-32)"
    arm-none-eabi-g++ "${COMMON_FLAGS[@]}" -mcpu=cortex-m3 -mthumb \
        -ffreestanding -fno-builtin -ffunction-sections -fdata-sections -Os \
        -c tests/static_contract_probe.cpp -o "$OUT_DIR/static_contract_cm3.o"
    audit_object "$OUT_DIR/static_contract_cm3.o" arm-none-eabi-nm arm-none-eabi-objdump
    arm-none-eabi-g++ "${COMMON_FLAGS[@]}" -mcpu=cortex-m3 -mthumb -DCAMEO_EMBED_COMPACT_32=1 \
        -ffreestanding -fno-builtin -ffunction-sections -fdata-sections -Os \
        -c tests/static_contract_probe.cpp -o "$OUT_DIR/static_contract_cm3_c32.o"
    audit_object "$OUT_DIR/static_contract_cm3_c32.o" arm-none-eabi-nm arm-none-eabi-objdump
fi

echo "Cameo.Embed C++20 checks passed"

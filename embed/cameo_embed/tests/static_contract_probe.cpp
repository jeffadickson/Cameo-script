/** @file static_contract_probe.cpp
 * @brief Freestanding static-contract probe compiled with -ffreestanding -fno-exceptions -fno-rtti
 *        and audited for zero heap symbols and zero .init_array/.ctors runtime constructors.
 */
#include "cameo_embed_vm.hpp"

using ProbeVm = cameo::embed::StaticVm<128, 16, 16, 16, 16, 16, 64, 48, 32, 8>;

static ProbeVm g_vm;
static cameo::embed::PackageReceiver<2048> g_rx;
static volatile bool g_abort = false;

extern "C" int cameo_embed_probe_init(const uint8_t* image, size_t len) noexcept {
    g_vm.set_abort_flag(&g_abort);
    (void)g_vm.add_memory_window({0x10000000ULL, 0x100u, cameo::embed::MemoryPerms::kRwAxi, 4u, 4u, true});
    const auto st = g_vm.load(cameo::embed::Span<const uint8_t>(image, len));
    if (st != cameo::embed::Status::Ok) return static_cast<int>(st);
    const auto r = g_vm.run_main(10000u);
    return static_cast<int>(r.status);
}

extern "C" int cameo_embed_probe_hotload(const uint8_t* chunk, size_t len) noexcept {
    if (!g_rx.feed_bytes(chunk, len)) return -1;
    const auto st = g_rx.verify();
    if (st != cameo::embed::Status::Ok) return static_cast<int>(st);
    return static_cast<int>(g_vm.load(g_rx.span()));
}

extern "C" int64_t cameo_embed_probe_step(int64_t tick) noexcept {
    const auto mark = g_vm.mark_pools();
    const cameo::embed::Value args[1] = {cameo::embed::Value::from_int(tick)};
    cameo::embed::Value out[1]{};
    const auto r = g_vm.call_export("step", args, out, 10000u);
    g_vm.reset_pools_to(mark);
    if (r.status != cameo::embed::Status::Ok || r.result_count == 0u || !out[0].is_int()) return -1;
    return out[0].as_int();
}

/** @file nexrt_compat_probe.cpp
 * @brief Compiles cameo_embed_nexrt.hpp against real jeffadickson/rtos headers to prove
 *        zero warnings, zero heap symbols, zero exceptions/RTTI, and zero .init_array.
 */
#include "cameo_embed_nexrt.hpp"

using ProbeVm = cameo::embed::StaticVm<128, 16, 16, 16, 16, 16, 64, 48, 32, 8>;

static rtos::Queue<int64_t, 8> g_sensor_q;
static rtos::Logger<8, 96> g_logger;
static rtos::watchdog::Supervisor<4> g_wdt;
static rtos::trace::TraceRing<16> g_trace;
static cameo::nexrt::HostServices g_host;
static cameo::nexrt::PeriodicScriptTask<2048, 3, ProbeVm> g_script_task;
static cameo::nexrt::MonitorBridge<ProbeVm, 2048> g_monitor_bridge;

extern "C" bool cameo_nexrt_probe_register(const uint8_t* img, size_t len) noexcept {
    g_host.bind_logger(g_logger);
    g_host.bind_watchdog(g_wdt);
    g_host.bind_trace(g_trace);
    (void)g_host.bind_queue(0u, g_sensor_q);
    g_host.install_into(g_script_task.vm());
    (void)g_script_task.vm().add_memory_window(cameo::nexrt::AxiWindowPresets::riscue_uartlite());
    (void)g_script_task.vm().add_memory_window(cameo::nexrt::AxiWindowPresets::nexrt_hwsched());
    g_monitor_bridge.attach(g_script_task.vm());
    return g_script_task.register_task(cameo::embed::Span<const uint8_t>(img, len), 10u, 25000u, "cameo_dbg", "step");
}

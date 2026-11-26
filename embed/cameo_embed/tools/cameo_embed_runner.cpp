/** @file cameo_embed_runner.cpp
 * @brief Host runner executing compiled CEMB v1 bytecode images on cameo::embed::StaticVm
 *        for differential testing against C# CameoEngine + SimulatedEmbedHost.
 */
#include "cameo_embed_vm.hpp"

#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <vector>

using namespace cameo::embed;

namespace {

struct SimWindow {
    uint64_t base{0};
    std::vector<uint8_t> data;
};

struct SimTask {
    int64_t state{1};
    int64_t priority{2};
    int64_t stack_bytes{1536};
    bool guard_ok{true};
    int64_t cycles{1000};
};

struct SimHost {
    int64_t ticks{0};
    int64_t queues[8][64]{};
    size_t q_head[8]{};
    size_t q_count[8]{};
    int64_t gpio[32]{};
    std::vector<SimWindow> windows;
    std::vector<SimTask> tasks;

    bool enqueue(size_t ch, int64_t val) {
        if (ch >= 8u || q_count[ch] >= 64u) return false;
        queues[ch][(q_head[ch] + q_count[ch]) % 64u] = val;
        ++q_count[ch];
        return true;
    }
    bool dequeue(size_t ch, int64_t& out) {
        if (ch >= 8u || q_count[ch] == 0u) return false;
        out = queues[ch][q_head[ch]];
        q_head[ch] = (q_head[ch] + 1u) % 64u;
        --q_count[ch];
        return true;
    }

    static bool bus_read(void* ctx, uint64_t addr, uint8_t width, uint64_t& out) noexcept {
        auto* self = static_cast<SimHost*>(ctx);
        for (const auto& w : self->windows) {
            if (addr >= w.base && addr - w.base + width <= w.data.size()) {
                const size_t off = static_cast<size_t>(addr - w.base);
                uint64_t v = 0;
                for (uint8_t i = 0; i < width; ++i)
                    v |= static_cast<uint64_t>(w.data[off + i]) << (i * 8u);
                out = v;
                return true;
            }
        }
        return false;
    }

    static bool bus_write(void* ctx, uint64_t addr, uint8_t width, uint64_t val) noexcept {
        auto* self = static_cast<SimHost*>(ctx);
        for (auto& w : self->windows) {
            if (addr >= w.base && addr - w.base + width <= w.data.size()) {
                const size_t off = static_cast<size_t>(addr - w.base);
                for (uint8_t i = 0; i < width; ++i)
                    w.data[off + i] = static_cast<uint8_t>((val >> (i * 8u)) & 0xFFu);
                return true;
            }
        }
        return false;
    }

    static bool debug_write(void*, const char* data, size_t len) noexcept {
        std::printf("DBG %.*s", static_cast<int>(len), data);
        return true;
    }
};

Status cb_ticks(void* u, HostCallContext& c) noexcept {
    (void)c.push_result(Value::from_int(static_cast<SimHost*>(u)->ticks));
    return Status::Ok;
}
Status cb_sleep(void* u, HostCallContext& c) noexcept {
    int64_t dt = 0;
    if (!c.try_int(0, dt) || dt < 0) { (void)c.push_result(Value::from_bool(false)); return Status::Ok; }
    static_cast<SimHost*>(u)->ticks += dt;
    (void)c.push_result(Value::from_bool(true));
    return Status::Ok;
}
Status cb_yield(void*, HostCallContext&) noexcept { return Status::Ok; }
Status cb_log(void*, HostCallContext& c) noexcept {
    std::string_view tag{}; int64_t v = 0;
    if (!c.try_string(0, tag) || !c.try_int(1, v)) return Status::TypeError;
    std::printf("LOG %.*s=%lld\n", static_cast<int>(tag.size()), tag.data(), static_cast<long long>(v));
    (void)c.push_result(Value::from_bool(true));
    return Status::Ok;
}
Status cb_log_str(void*, HostCallContext& c) noexcept {
    std::string_view msg{};
    if (!c.try_string(0, msg)) return Status::TypeError;
    std::printf("LOGSTR %.*s\n", static_cast<int>(msg.size()), msg.data());
    (void)c.push_result(Value::from_bool(true));
    return Status::Ok;
}
Status cb_wdt(void*, HostCallContext& c) noexcept {
    int64_t tid = 0;
    if (!c.try_int(0, tid)) return Status::TypeError;
    std::printf("WDT %lld\n", static_cast<long long>(tid));
    (void)c.push_result(Value::from_bool(true));
    return Status::Ok;
}
Status cb_qsend(void* u, HostCallContext& c) noexcept {
    int64_t ch = 0, val = 0;
    if (!c.try_int(0, ch) || !c.try_int(1, val)) return Status::TypeError;
    const bool ok = (ch >= 0 && static_cast<SimHost*>(u)->enqueue(static_cast<size_t>(ch), val));
    (void)c.push_result(Value::from_int(ok ? 0 : -1));
    return Status::Ok;
}
Status cb_qrecv(void* u, HostCallContext& c) noexcept {
    int64_t ch = 0, val = 0;
    if (!c.try_int(0, ch)) return Status::TypeError;
    const bool ok = (ch >= 0 && static_cast<SimHost*>(u)->dequeue(static_cast<size_t>(ch), val));
    (void)c.push_result(Value::from_int(ok ? val : 0));
    (void)c.push_result(Value::from_bool(ok));
    return Status::Ok;
}
Status cb_notify(void*, HostCallContext& c) noexcept {
    (void)c.push_result(Value::from_int(0));
    return Status::Ok;
}
Status cb_gpio_r(void* u, HostCallContext& c) noexcept {
    int64_t pin = 0;
    if (!c.try_int(0, pin)) return Status::TypeError;
    auto* s = static_cast<SimHost*>(u);
    const int64_t v = (pin >= 0 && pin < 32) ? s->gpio[pin] : 0;
    (void)c.push_result(Value::from_int(v));
    return Status::Ok;
}
Status cb_gpio_w(void* u, HostCallContext& c) noexcept {
    int64_t pin = 0, val = 0;
    if (!c.try_int(0, pin) || !c.try_int(1, val)) return Status::TypeError;
    auto* s = static_cast<SimHost*>(u);
    if (pin >= 0 && pin < 32) s->gpio[pin] = val;
    return Status::Ok;
}
Status cb_task_count(void* u, HostCallContext& c) noexcept {
    (void)c.push_result(Value::from_int(static_cast<int64_t>(static_cast<SimHost*>(u)->tasks.size())));
    return Status::Ok;
}
Status cb_task_state(void* u, HostCallContext& c) noexcept {
    int64_t i = 0; if (!c.try_int(0, i)) return Status::TypeError;
    auto* s = static_cast<SimHost*>(u);
    const int64_t v = (i >= 0 && static_cast<size_t>(i) < s->tasks.size()) ? s->tasks[static_cast<size_t>(i)].state : -1;
    (void)c.push_result(Value::from_int(v));
    return Status::Ok;
}
Status cb_task_prio(void* u, HostCallContext& c) noexcept {
    int64_t i = 0; if (!c.try_int(0, i)) return Status::TypeError;
    auto* s = static_cast<SimHost*>(u);
    const int64_t v = (i >= 0 && static_cast<size_t>(i) < s->tasks.size()) ? s->tasks[static_cast<size_t>(i)].priority : -1;
    (void)c.push_result(Value::from_int(v));
    return Status::Ok;
}
Status cb_task_stack(void* u, HostCallContext& c) noexcept {
    int64_t i = 0; if (!c.try_int(0, i)) return Status::TypeError;
    auto* s = static_cast<SimHost*>(u);
    const int64_t v = (i >= 0 && static_cast<size_t>(i) < s->tasks.size()) ? s->tasks[static_cast<size_t>(i)].stack_bytes : 0;
    (void)c.push_result(Value::from_int(v));
    return Status::Ok;
}
Status cb_task_guard(void* u, HostCallContext& c) noexcept {
    int64_t i = 0; if (!c.try_int(0, i)) return Status::TypeError;
    auto* s = static_cast<SimHost*>(u);
    const bool v = (i >= 0 && static_cast<size_t>(i) < s->tasks.size()) && s->tasks[static_cast<size_t>(i)].guard_ok;
    (void)c.push_result(Value::from_bool(v));
    return Status::Ok;
}
Status cb_task_cycles(void* u, HostCallContext& c) noexcept {
    int64_t i = 0; if (!c.try_int(0, i)) return Status::TypeError;
    auto* s = static_cast<SimHost*>(u);
    const int64_t v = (i >= 0 && static_cast<size_t>(i) < s->tasks.size()) ? s->tasks[static_cast<size_t>(i)].cycles : 0;
    (void)c.push_result(Value::from_int(v));
    return Status::Ok;
}
Status cb_qdepth(void* u, HostCallContext& c) noexcept {
    int64_t ch = 0; if (!c.try_int(0, ch)) return Status::TypeError;
    auto* s = static_cast<SimHost*>(u);
    const int64_t d = (ch >= 0 && ch < 8) ? static_cast<int64_t>(s->q_count[ch]) : 0;
    (void)c.push_result(Value::from_int(d));
    return Status::Ok;
}
Status cb_dbg_trace(void*, HostCallContext& c) noexcept {
    int64_t eid = 0, arg = 0;
    if (!c.try_int(0, eid) || !c.try_int(1, arg)) return Status::TypeError;
    std::printf("TRACE %lld %lld\n", static_cast<long long>(eid), static_cast<long long>(arg));
    (void)c.push_result(Value::from_bool(true));
    return Status::Ok;
}
Status cb_dbg_bp(void*, HostCallContext& c) noexcept {
    int64_t id = 0; if (!c.try_int(0, id)) return Status::TypeError;
    std::printf("BP %lld\n", static_cast<long long>(id));
    return Status::Ok;
}
Status cb_custom_add10(void*, HostCallContext& c) noexcept {
    int64_t x = 0; if (!c.try_int(0, x)) return Status::TypeError;
    (void)c.push_result(Value::from_int(x + 10));
    return Status::Ok;
}

void print_value(const auto& vm, Value v) {
    switch (v.kind) {
        case ValueKind::Nil: std::printf("nil"); break;
        case ValueKind::Bool: std::printf("bool:%s", v.as_bool() ? "true" : "false"); break;
        case ValueKind::Int: std::printf("int:%lld", static_cast<long long>(v.as_int())); break;
#if CAMEO_EMBED_ENABLE_FLOAT
        case ValueKind::Float: std::printf("float:%.17g", v.as_float()); break;
#endif
        case ValueKind::Ptr: std::printf("ptr:0x%llX", static_cast<unsigned long long>(v.as_ptr())); break;
        case ValueKind::String: {
            auto sv = vm.get_string(v);
            std::printf("str:%.*s", static_cast<int>(sv.size()), sv.data());
            break;
        }
        default: std::printf("kind:%u", static_cast<unsigned>(v.kind)); break;
    }
}

} // namespace

int main(int argc, char** argv) {
    if (argc < 2) {
        std::fprintf(stderr, "usage: cameo_embed_runner <image.cemb> [--budget N] [--ro] "
                             "[--win base:size:rw|ro:minw:maxw] [--seed32 addr:val] "
                             "[--enqueue ch:val] [--task st:prio:stack:guard:cyc] "
                             "[--call fn [int_args...]]\n");
        return 2;
    }

    FILE* fp = std::fopen(argv[1], "rb");
    if (!fp) { std::fprintf(stderr, "cannot open %s\n", argv[1]); return 2; }
    std::fseek(fp, 0, SEEK_END);
    const long sz = std::ftell(fp);
    std::fseek(fp, 0, SEEK_SET);
    if (sz <= 0) { std::fclose(fp); return 2; }
    std::vector<uint8_t> image(static_cast<size_t>(sz));
    if (std::fread(image.data(), 1, image.size(), fp) != image.size()) {
        std::fclose(fp); return 2;
    }
    std::fclose(fp);

    static StaticVm<512, 64, 64, 128, 128, 64, 512, 64, 64, 16> vm;
    SimHost sim{};
    vm.set_bus_backend({&sim, &SimHost::bus_read, &SimHost::bus_write, nullptr});
    vm.set_debug_writer(&SimHost::debug_write, &sim);

    (void)vm.register_host(StandardHostId::Ticks, &cb_ticks, &sim);
    (void)vm.register_host(StandardHostId::Sleep, &cb_sleep, &sim);
    (void)vm.register_host(StandardHostId::Yield, &cb_yield, &sim);
    (void)vm.register_host(StandardHostId::Log, &cb_log, &sim);
    (void)vm.register_host(StandardHostId::LogStr, &cb_log_str, &sim);
    (void)vm.register_host(StandardHostId::WatchdogCheckin, &cb_wdt, &sim);
    (void)vm.register_host(StandardHostId::QueueSend, &cb_qsend, &sim);
    (void)vm.register_host(StandardHostId::QueueRecv, &cb_qrecv, &sim);
    (void)vm.register_host(StandardHostId::Notify, &cb_notify, &sim);
    (void)vm.register_host(StandardHostId::GpioRead, &cb_gpio_r, &sim);
    (void)vm.register_host(StandardHostId::GpioWrite, &cb_gpio_w, &sim);
    (void)vm.register_host(StandardHostId::TaskCount, &cb_task_count, &sim);
    (void)vm.register_host(StandardHostId::TaskState, &cb_task_state, &sim);
    (void)vm.register_host(StandardHostId::TaskPriority, &cb_task_prio, &sim);
    (void)vm.register_host(StandardHostId::TaskStackBytes, &cb_task_stack, &sim);
    (void)vm.register_host(StandardHostId::TaskGuardOk, &cb_task_guard, &sim);
    (void)vm.register_host(StandardHostId::TaskCycles, &cb_task_cycles, &sim);
    (void)vm.register_host(StandardHostId::QueueDepth, &cb_qdepth, &sim);
    (void)vm.register_host(StandardHostId::DbgTrace, &cb_dbg_trace, &sim);
    (void)vm.register_host(StandardHostId::DbgBreakpoint, &cb_dbg_bp, &sim);
    (void)vm.register_host(StandardHostId::CustomBase, &cb_custom_add10, &sim);

    uint32_t budget = 1000000u;
    std::string call_fn;
    std::vector<Value> call_args;

    for (int i = 2; i < argc; ++i) {
        if (std::strcmp(argv[i], "--budget") == 0 && i + 1 < argc) {
            budget = static_cast<uint32_t>(std::strtoul(argv[++i], nullptr, 10));
        } else if (std::strcmp(argv[i], "--ro") == 0) {
            vm.set_axi_read_only(true);
        } else if (std::strcmp(argv[i], "--win") == 0 && i + 1 < argc) {
            // format: baseHex:sizeHex:rw|ro:minw:maxw
            unsigned long long base = 0, size = 0;
            char mode[8]{};
            unsigned minw = 1, maxw = 8;
            if (std::sscanf(argv[++i], "%llx:%llx:%7[^:]:%u:%u", &base, &size, mode, &minw, &maxw) >= 3) {
                const uint8_t perms = (std::strcmp(mode, "rw") == 0)
                    ? (MemoryPerms::kRead | MemoryPerms::kWrite | MemoryPerms::kAxi | MemoryPerms::kRam)
                    : (MemoryPerms::kRead | MemoryPerms::kAxi | MemoryPerms::kRam);
                (void)vm.add_memory_window({static_cast<uint64_t>(base), static_cast<uint64_t>(size),
                                            perms, static_cast<uint8_t>(minw), static_cast<uint8_t>(maxw), true});
                sim.windows.push_back({static_cast<uint64_t>(base), std::vector<uint8_t>(static_cast<size_t>(size), 0u)});
            }
        } else if (std::strcmp(argv[i], "--seed32") == 0 && i + 1 < argc) {
            unsigned long long addr = 0, val = 0;
            if (std::sscanf(argv[++i], "%llx:%llx", &addr, &val) == 2) {
                (void)SimHost::bus_write(&sim, static_cast<uint64_t>(addr), 4u, static_cast<uint64_t>(val));
            }
        } else if (std::strcmp(argv[i], "--enqueue") == 0 && i + 1 < argc) {
            int ch = 0; long long val = 0;
            if (std::sscanf(argv[++i], "%d:%lld", &ch, &val) == 2 && ch >= 0) {
                sim.enqueue(static_cast<size_t>(ch), val);
            }
        } else if (std::strcmp(argv[i], "--task") == 0 && i + 1 < argc) {
            long long st = 1, prio = 2, stack = 1536, guard = 1, cyc = 0;
            if (std::sscanf(argv[++i], "%lld:%lld:%lld:%lld:%lld", &st, &prio, &stack, &guard, &cyc) == 5) {
                sim.tasks.push_back({st, prio, stack, guard != 0, cyc});
            }
        } else if (std::strcmp(argv[i], "--call") == 0 && i + 1 < argc) {
            call_fn = argv[++i];
            while (i + 1 < argc && !(argv[i + 1][0] == '-' && argv[i + 1][1] == '-')) {
                call_args.push_back(Value::from_int(std::strtoll(argv[++i], nullptr, 0)));
            }
        }
    }

    const Status load_st = vm.load(image);
    if (load_st != Status::Ok) {
        std::printf("ERR %u\n", static_cast<unsigned>(load_st));
        return 0;
    }

    Value results[8]{};
    ExecResult res = vm.run_main(budget, results);
    if (res.status != Status::Ok) {
        std::printf("ERR %u\n", static_cast<unsigned>(res.status));
        return 0;
    }

    if (!call_fn.empty()) {
        res = vm.call_export(call_fn, call_args, results, budget);
        if (res.status != Status::Ok) {
            std::printf("ERR %u\n", static_cast<unsigned>(res.status));
            return 0;
        }
    }

    std::printf("OK");
    for (size_t k = 0; k < res.result_count; ++k) {
        std::printf(" ");
        print_value(vm, results[k]);
    }
    std::printf("\n");
    return 0;
}

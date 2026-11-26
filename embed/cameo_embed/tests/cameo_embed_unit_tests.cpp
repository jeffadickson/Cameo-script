#include "cameo_embed_vm.hpp"

#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

using namespace cameo::embed;

namespace {

int g_failures = 0;

#define CHECK(cond) do { \
    if (!(cond)) { \
        std::fprintf(stderr, "FAIL %s:%d: %s\n", __FILE__, __LINE__, #cond); \
        ++g_failures; \
    } \
} while (0)

struct MockBus {
    uint8_t uart_regs[256]{};
    uint8_t hwsched_regs[1024]{};
    uint8_t sram[512]{};
    uint32_t fences{0};

    static bool read_cb(void* ctx, uint64_t addr, uint8_t width, uint64_t& out) noexcept {
        auto* self = static_cast<MockBus*>(ctx);
        const uint8_t* ptr = nullptr;
        if (addr >= 0x10000000ULL && addr + width <= 0x10000100ULL)
            ptr = self->uart_regs + (addr - 0x10000000ULL);
        else if (addr >= 0x10010000ULL && addr + width <= 0x10010400ULL)
            ptr = self->hwsched_regs + (addr - 0x10010000ULL);
        else if (addr >= 0x20000000ULL && addr + width <= 0x20000200ULL)
            ptr = self->sram + (addr - 0x20000000ULL);
        else
            return false;

        uint64_t v = 0;
        for (uint8_t i = 0; i < width; ++i)
            v |= static_cast<uint64_t>(ptr[i]) << (i * 8u);
        out = v;
        return true;
    }

    static bool write_cb(void* ctx, uint64_t addr, uint8_t width, uint64_t val) noexcept {
        auto* self = static_cast<MockBus*>(ctx);
        uint8_t* ptr = nullptr;
        if (addr >= 0x10000000ULL && addr + width <= 0x10000100ULL)
            ptr = self->uart_regs + (addr - 0x10000000ULL);
        else if (addr >= 0x10010000ULL && addr + width <= 0x10010400ULL)
            ptr = self->hwsched_regs + (addr - 0x10010000ULL);
        else if (addr >= 0x20000000ULL && addr + width <= 0x20000200ULL)
            ptr = self->sram + (addr - 0x20000000ULL);
        else
            return false;

        for (uint8_t i = 0; i < width; ++i)
            ptr[i] = static_cast<uint8_t>((val >> (i * 8u)) & 0xFFu);
        return true;
    }

    static void fence_cb(void* ctx) noexcept {
        ++static_cast<MockBus*>(ctx)->fences;
    }
};

struct ImageBuilder {
    struct InstrSpec { Op op; int16_t a, b, c; };
    struct ConstSpec { ConstKind kind; uint64_t payload; };

    uint16_t flags{0};
    std::vector<InstrSpec> code;
    std::vector<ConstSpec> consts;
    std::vector<std::string_view> strings;
    uint8_t max_stack{16};

    uint16_t add_string(std::string_view s) {
        const auto id = static_cast<uint16_t>(strings.size());
        strings.push_back(s);
        return id;
    }

    int16_t add_int_k(int64_t v) {
        const auto idx = static_cast<int16_t>(consts.size());
        consts.push_back({ConstKind::Int, static_cast<uint64_t>(v)});
        return idx;
    }

    int16_t add_str_k(std::string_view s) {
        const uint16_t sid = add_string(s);
        const auto idx = static_cast<int16_t>(consts.size());
        consts.push_back({ConstKind::String, sid});
        return idx;
    }

    std::vector<uint8_t> build() const {
        auto align8 = [](size_t v) { return (v + 7u) & ~size_t(7u); };
        std::vector<uint8_t> str_blob;
        std::vector<uint32_t> str_off;
        for (auto sv : strings) {
            str_off.push_back(static_cast<uint32_t>(str_blob.size()));
            for (char ch : sv) str_blob.push_back(static_cast<uint8_t>(ch));
            str_blob.push_back(0u);
        }

        const size_t p_off = kHeaderSize;
        const size_t c_off = align8(p_off + kProtoDescSize);
        const size_t i_off = align8(c_off + consts.size() * kConstDescSize);
        const size_t u_off = align8(i_off + code.size() * kInstrSize);
        const size_t g_off = align8(u_off);
        const size_t h_off = align8(g_off);
        const size_t s_off = align8(h_off);
        const size_t sd_off = align8(s_off + strings.size() * kStringDescSize);
        const size_t total = align8(sd_off + str_blob.size());

        std::vector<uint8_t> img(total, 0u);
        auto w16 = [&](size_t pos, uint16_t v) {
            img[pos] = static_cast<uint8_t>(v & 0xFFu);
            img[pos + 1] = static_cast<uint8_t>((v >> 8) & 0xFFu);
        };
        auto w32 = [&](size_t pos, uint32_t v) {
            img[pos] = static_cast<uint8_t>(v & 0xFFu);
            img[pos + 1] = static_cast<uint8_t>((v >> 8) & 0xFFu);
            img[pos + 2] = static_cast<uint8_t>((v >> 16) & 0xFFu);
            img[pos + 3] = static_cast<uint8_t>((v >> 24) & 0xFFu);
        };
        auto w64 = [&](size_t pos, uint64_t v) {
            w32(pos, static_cast<uint32_t>(v & 0xFFFFFFFFULL));
            w32(pos + 4, static_cast<uint32_t>(v >> 32));
        };

        w32(0, kMagic);
        w16(4, kVersion);
        w16(6, flags);
        w32(8, static_cast<uint32_t>(total));
        w16(16, 1u);
        w16(18, static_cast<uint16_t>(consts.size()));
        w32(20, static_cast<uint32_t>(code.size()));
        w16(30, static_cast<uint16_t>(strings.size()));
        w16(32, 32u);
        w16(34, 4u);
        w16(36, 1u);
        w16(38, 1u);
        w16(40, 1u);
        w16(42, 4u);
        w32(44, static_cast<uint32_t>(p_off));
        w32(48, static_cast<uint32_t>(c_off));
        w32(52, static_cast<uint32_t>(i_off));
        w32(56, static_cast<uint32_t>(u_off));
        w32(60, static_cast<uint32_t>(g_off));
        w32(64, static_cast<uint32_t>(h_off));
        w32(68, static_cast<uint32_t>(s_off));
        w32(72, static_cast<uint32_t>(sd_off));
        w32(76, static_cast<uint32_t>(str_blob.size()));

        // Proto 0
        w32(p_off + 0, 0u);
        w32(p_off + 4, static_cast<uint32_t>(code.size()));
        w32(p_off + 8, 0u);
        w16(p_off + 12, static_cast<uint16_t>(consts.size()));
        w16(p_off + 18, 0xFFFFu);
        img[p_off + 20] = 0u;
        img[p_off + 21] = max_stack;

        for (size_t k = 0; k < consts.size(); ++k) {
            img[c_off + k * kConstDescSize] = static_cast<uint8_t>(consts[k].kind);
            w64(c_off + k * kConstDescSize + 8, consts[k].payload);
        }
        for (size_t pc = 0; pc < code.size(); ++pc) {
            const size_t p = i_off + pc * kInstrSize;
            img[p] = static_cast<uint8_t>(code[pc].op);
            w16(p + 2, static_cast<uint16_t>(code[pc].a));
            w16(p + 4, static_cast<uint16_t>(code[pc].b));
            w16(p + 6, static_cast<uint16_t>(code[pc].c));
        }
        for (size_t si = 0; si < strings.size(); ++si) {
            const size_t p = s_off + si * kStringDescSize;
            w32(p + 0, str_off[si]);
            w32(p + 4, static_cast<uint32_t>(strings[si].size()));
            w32(p + 8, fnv1a32(strings[si]));
        }
        if (!str_blob.empty()) std::memcpy(img.data() + sd_off, str_blob.data(), str_blob.size());

        const uint32_t crc = crc32c(img.data() + 16, img.size() - 16u);
        w32(12, crc);
        return img;
    }
};

void test_crc_and_package_receiver() {
    const uint8_t sample[] = {'1', '2', '3', '4', '5', '6', '7', '8', '9'};
    CHECK(crc32c(sample, sizeof(sample)) == 0xE3069283u);

    ImageBuilder ib;
    ib.code.push_back({Op::LoadInt, 0, 77, 0});
    ib.code.push_back({Op::Return, 0, 2, 0});
    auto img = ib.build();

    // Hot-load via hex lines
    PackageReceiver<1024> rx;
    std::string hex;
    for (uint8_t b : img) {
        char pair[3]{};
        detail::write_hex_digits(pair, b, 2u);
        hex.append(pair, 2u);
    }
    CHECK(rx.feed_hex(std::string_view(hex.data(), hex.size() / 2)));
    CHECK(rx.feed_hex(std::string_view(hex.data() + hex.size() / 2, hex.size() - hex.size() / 2)));
    CHECK(rx.verify() == Status::Ok);
    CHECK(rx.is_ready());

    StaticVm<> vm;
    CHECK(vm.load(rx.span()) == Status::Ok);
    Value out[1]{};
    auto res = vm.run_main(1000u, out);
    CHECK(res.status == Status::Ok);
    CHECK(res.result_count == 1u);
    CHECK(out[0].is_int() && out[0].as_int() == 77);

    // Corrupt 1 bit -> ChecksumMismatch
    img[img.size() - 1] ^= 0x01u;
    rx.reset();
    CHECK(rx.feed_bytes(img.data(), img.size()));
    CHECK(rx.verify() == Status::ChecksumMismatch);
    CHECK(vm.load(img) == Status::ChecksumMismatch);
}

void test_ptr_and_memory_windows() {
    MockBus bus{};
    bus.sram[0] = 0x78u; bus.sram[1] = 0x56u; bus.sram[2] = 0x34u; bus.sram[3] = 0x12u;

    StaticVm<> vm;
    vm.set_bus_backend({&bus, &MockBus::read_cb, &MockBus::write_cb, &MockBus::fence_cb});
    CHECK(vm.add_memory_window({0x10000000ULL, 0x100u, MemoryPerms::kRoAxi, 4u, 4u, true}));
    CHECK(vm.add_memory_window({0x20000000ULL, 0x200u, MemoryPerms::kRwRam, 1u, 8u, true}));

    // Check window policy enforcement directly
    CHECK(vm.check_memory_access(0x10000000ULL, 4u, false));
    CHECK(!vm.check_memory_access(0x10000000ULL, 4u, true));  // read-only AXI window!
    CHECK(!vm.check_memory_access(0x10000000ULL, 1u, false)); // AXI4-Lite requires 4-byte access!
    CHECK(!vm.check_memory_access(0x10000002ULL, 4u, false)); // unaligned!
    CHECK(vm.check_memory_access(0x20000000ULL, 4u, true));
    CHECK(!vm.check_memory_access(0x20000001ULL, 4u, true));  // unaligned 32-bit write!
    CHECK(!vm.check_memory_access(0xDEADBEEFULL, 4u, false)); // outside all windows!

    // Execute bytecode: p = ptr(0x20000000); v = p.read32(); return v
    ImageBuilder ib;
    const int16_t kAddr = ib.add_int_k(0x20000000LL);
    const int16_t kRead32 = ib.add_str_k("read32");
    ib.code.push_back({Op::LoadBuiltin, 0, static_cast<int16_t>(BuiltinId::PtrOf), 0});
    ib.code.push_back({Op::LoadK, 1, kAddr, 0});
    ib.code.push_back({Op::Call, 0, 2, 2});
    ib.code.push_back({Op::Self, 1, 0, kRead32});
    ib.code.push_back({Op::Call, 1, 2, 2});
    ib.code.push_back({Op::Return, 1, 2, 0});

    auto img = ib.build();
    CHECK(vm.load(img) == Status::Ok);
    Value out[1]{};
    auto res = vm.run_main(1000u, out);
    CHECK(res.status == Status::Ok);
    CHECK(res.result_count == 1u);
    CHECK(out[0].is_int() && out[0].as_int() == 0x12345678LL);

    // Fault on unmapped pointer read
    ImageBuilder bad;
    const int16_t kBadAddr = bad.add_int_k(0x30000000LL);
    const int16_t kR32 = bad.add_str_k("read32");
    bad.code.push_back({Op::LoadBuiltin, 0, static_cast<int16_t>(BuiltinId::PtrOf), 0});
    bad.code.push_back({Op::LoadK, 1, kBadAddr, 0});
    bad.code.push_back({Op::Call, 0, 2, 2});
    bad.code.push_back({Op::Self, 1, 0, kR32});
    bad.code.push_back({Op::Call, 1, 2, 2});
    bad.code.push_back({Op::Return, 1, 2, 0});
    auto bad_img = bad.build();
    CHECK(vm.load(bad_img) == Status::Ok);
    auto fault = vm.run_main(1000u, out);
    CHECK(fault.status == Status::MemoryFault);
    CHECK(vm.last_fault_addr() == 0x30000000ULL);
}

void test_float_gate_in_integer_only_mode() {
#if !CAMEO_EMBED_ENABLE_FLOAT
    ImageBuilder ib;
    ib.flags = ImageFlags::kHasFloat;
    ib.code.push_back({Op::Return, 0, 1, 0});
    auto img = ib.build();
    StaticVm<> vm;
    CHECK(vm.load(img) == Status::FloatDisabled);
#endif
}

void test_axi_read_only_gate() {
    ImageBuilder ib;
    ib.flags = ImageFlags::kHasMemWrite;
    ib.code.push_back({Op::Return, 0, 1, 0});
    auto img = ib.build();
    StaticVm<> vm;
    vm.set_axi_read_only(true);
    CHECK(vm.load(img) == Status::WriteDenied);
}

void test_step_budget_and_abort() {
    ImageBuilder ib;
    ib.code.push_back({Op::Jmp, 0, -1, 0});
    auto img = ib.build();

    StaticVm<> vm;
    CHECK(vm.load(img) == Status::Ok);
    auto r1 = vm.run_main(150u);
    CHECK(r1.status == Status::StepLimitExceeded);
    CHECK(r1.steps_executed == 150u);

    volatile bool abort_flag = true;
    vm.set_abort_flag(&abort_flag);
    auto r2 = vm.run_main(1000u);
    CHECK(r2.status == Status::Aborted);
    CHECK(r2.steps_executed == 0u);
}

void test_watchpoints_bus_trace_and_stepping() {
    // Program:
    //   r0 = 10
    //   r1 = 20
    //   r2 = r0 + r1   (PC 2 -> breakpoint here!)
    //   r3 = ptr(0x20000000)
    //   r3.write32_at(4, r2)  (triggers watchpoint on 0x20000004!)
    //   r4 = r3.bits32_at(4, 0, 8)
    //   return r4
    uint32_t mock_ram[4]{0u, 0u, 0u, 0u};
    auto bus_r = [](void* c, uint64_t a, uint8_t w, uint64_t& o) noexcept -> bool {
        auto* ram = static_cast<uint32_t*>(c);
        if (w != 4u || a < 0x20000000ULL || a >= 0x20000010ULL) return false;
        o = ram[(a - 0x20000000ULL) / 4u];
        return true;
    };
    auto bus_w = [](void* c, uint64_t a, uint8_t w, uint64_t v) noexcept -> bool {
        auto* ram = static_cast<uint32_t*>(c);
        if (w != 4u || a < 0x20000000ULL || a >= 0x20000010ULL) return false;
        ram[(a - 0x20000000ULL) / 4u] = static_cast<uint32_t>(v);
        return true;
    };

    ImageBuilder ib;
    const int16_t kAddr = ib.add_int_k(0x20000000LL);
    const int16_t kW32At = ib.add_str_k("write32_at");
    const int16_t kBitsAt = ib.add_str_k("bits32_at");
    ib.code.push_back({Op::LoadInt, 0, 10, 0});                             // PC 0
    ib.code.push_back({Op::LoadInt, 1, 20, 0});                             // PC 1
    ib.code.push_back({Op::Add, 2, 0, 1});                                  // PC 2 (bp)
    ib.code.push_back({Op::LoadBuiltin, 3, static_cast<int16_t>(BuiltinId::PtrOf), 0}); // PC 3
    ib.code.push_back({Op::LoadK, 4, kAddr, 0});                            // PC 4
    ib.code.push_back({Op::Call, 3, 2, 2});                                 // PC 5: r3 = ptr(0x20000000)
    ib.code.push_back({Op::Self, 4, 3, kW32At});                            // PC 6: r4=fn, r5=r3
    ib.code.push_back({Op::LoadInt, 6, 4, 0});                              // PC 7: offset=4
    ib.code.push_back({Op::Move, 7, 2, 0});                                 // PC 8: val=30
    ib.code.push_back({Op::Call, 4, 4, 2});                                 // PC 9: write32_at(4, 30)
    ib.code.push_back({Op::Self, 4, 3, kBitsAt});                           // PC 10
    ib.code.push_back({Op::LoadInt, 6, 4, 0});                              // PC 11: offset=4
    ib.code.push_back({Op::LoadInt, 7, 0, 0});                              // PC 12: lsb=0
    ib.code.push_back({Op::LoadInt, 8, 8, 0});                              // PC 13: width=8
    ib.code.push_back({Op::Call, 4, 5, 2});                                 // PC 14: bits32_at
    ib.code.push_back({Op::Return, 4, 2, 0});                               // PC 15
    auto img = ib.build();

    StaticVm<> vm;
    vm.set_bus_backend({mock_ram, bus_r, bus_w, nullptr});
    CHECK(vm.add_memory_window({0x20000000ULL, 0x10u, MemoryPerms::kRwRam, 4u, 4u, true}));
    vm.set_bus_trace_enabled(true);
    const int wp = vm.add_watchpoint(0x20000004ULL, 4u, WatchFlags::kWrite | WatchFlags::kBreak);
    CHECK(wp == 0);
    CHECK(vm.set_breakpoint(0u, 2u));
    CHECK(vm.load(img) == Status::Ok);

    // Run until PC 2 breakpoint
    Value out[1]{};
    auto r_bp = vm.run_main(1000u, out);
    CHECK(r_bp.status == Status::Breakpoint);
    CHECK(r_bp.fault_proto == 0u && r_bp.fault_pc == 2u);
    CHECK(vm.is_paused());
    Value r0{}, r1{};
    CHECK(vm.inspect_reg(0, r0) && r0.is_int() && r0.as_int() == 10);
    CHECK(vm.inspect_reg(1, r1) && r1.is_int() && r1.as_int() == 20);

    // Single-step 1 instruction across PC 2 (Add) -> PC 3
    auto r_step = vm.step_one(out);
    CHECK(r_step.status == Status::Stepped);
    CHECK(vm.current_pc() == 3u);
    Value r2{};
    CHECK(vm.inspect_reg(2, r2) && r2.is_int() && r2.as_int() == 30);

    // Continue until WatchFlags::kBreak hits on write32_at(4, 30)
    auto r_wp = vm.continue_run(out, 1000u);
    CHECK(r_wp.status == Status::WatchpointHit);
    CHECK(vm.watch_hits() == 1u);
    CHECK(vm.last_watch_addr() == 0x20000004ULL);
    CHECK(mock_ram[1] == 30u);

    // Continue to completion
    auto r_done = vm.continue_run(out, 1000u);
    CHECK(r_done.status == Status::Ok);
    CHECK(r_done.result_count == 1u && out[0].is_int() && out[0].as_int() == 30);
    CHECK(!vm.is_paused());

    // Verify bus trace captured both the watched write and the subsequent bits32_at read
    CHECK(vm.bus_trace_count() == 2u);
    BusTraceEvent ev0{}, ev1{};
    CHECK(vm.read_bus_trace_oldest(0, ev0));
    CHECK(vm.read_bus_trace_oldest(1, ev1));
    CHECK(ev0.addr() == 0x20000004ULL && ev0.value() == 30u && (ev0.flags & BusTraceFlags::kWrite) != 0u &&
          (ev0.flags & BusTraceFlags::kWatchpointHit) != 0u);
    CHECK(ev1.addr() == 0x20000004ULL && ev1.value() == 30u && (ev1.flags & BusTraceFlags::kWrite) == 0u);
}

} // namespace

int main() {
    test_crc_and_package_receiver();
    test_ptr_and_memory_windows();
    test_float_gate_in_integer_only_mode();
    test_axi_read_only_gate();
    test_step_budget_and_abort();
    test_watchpoints_bus_trace_and_stepping();
    if (g_failures != 0) {
        std::fprintf(stderr, "cameo_embed_unit_tests: %d failure(s)\n", g_failures);
        return 1;
    }
    std::printf("cameo_embed_unit_tests: all passed (float=%d, math=%d, compact32=%d, sizeof(Value)=%zu)\n",
                CAMEO_EMBED_ENABLE_FLOAT, CAMEO_EMBED_ENABLE_MATH, CAMEO_EMBED_COMPACT_32, sizeof(Value));
    return 0;
}

using Cameo;
using Cameo.Runtime;
using Cameo.Runtime.Interop;
using Cameo.Runtime.Syntax;
using Cameo.Runtime.Vm;
using CsCheck;
using SharpUnits;
using SharpUnits.Interop.Lua;
using Xunit;

namespace Cameo.Tests;

public sealed class UnitsDimensionCheckingTests
{
    private sealed class NetlistHostFixture
    {
        public List<(string Kind, string Id, double Value, double Secondary)> Parts { get; } = new();
        public List<(double WidthMm, double HeightMm)> Boards { get; } = new();
        public List<(double StartHz, double StopHz)> AcSweeps { get; } = new();
        public List<(double StepS, double StopS)> TranSweeps { get; } = new();
        public double[]? LastLadderOhms { get; private set; }
        public Quantity<double>? LastStrictResistance { get; private set; }

        [Expose("resistor")]
        public void Resistor(string id, string nodeP, string nodeN, [Unit("Ohm")] double ohms, string footprint = "") =>
            Parts.Add(("R", id, ohms, 0.0));

        [Expose("capacitor")]
        public void Capacitor(string id, string nodeP, string nodeN, [Unit("F")] double farads, string footprint = "") =>
            Parts.Add(("C", id, farads, 0.0));

        [Expose("inductor")]
        public void Inductor(string id, string nodeP, string nodeN, [Unit("H")] double henries, string footprint = "") =>
            Parts.Add(("L", id, henries, 0.0));

        [Expose("vsrc")]
        public void VoltageSource(string id, string nodeP, string nodeN, [Unit("V")] double dcVolts, [Unit("V")] double acMag = 0.0) =>
            Parts.Add(("V", id, dcVolts, acMag));

        [Expose("isrc")]
        public void CurrentSource(string id, string nodeP, string nodeN, [Unit("A")] double dcAmps, [Unit("A")] double acMag = 0.0) =>
            Parts.Add(("I", id, dcAmps, acMag));

        [Expose("pcb_board")]
        public void PcbBoard([Unit("mm")] double widthMm, [Unit("mm")] double heightMm, int layers = 2) =>
            Boards.Add((widthMm, heightMm));

        [Expose("ac")]
        public void Ac([Unit("Hz")] double fStartHz, [Unit("Hz")] double fStopHz, int pointsPerDecade = 10) =>
            AcSweeps.Add((fStartHz, fStopHz));

        [Expose("tran")]
        public void Tran([Unit("s")] double stepSeconds, [Unit("s")] double stopSeconds) =>
            TranSweeps.Add((stepSeconds, stopSeconds));

        [Expose("resistor_strict")]
        public void ResistorStrict(string id, [Unit("Ohm", AllowNumber = false)] double ohms) =>
            Parts.Add(("R_STRICT", id, ohms, 0.0));

        [Expose("resistor_ladder")]
        public void ResistorLadder([Unit("Ohm")] double[] resistances) =>
            LastLadderOhms = resistances;

        [Expose("record_qty_resistance")]
        [return: Unit("V")]
        public Quantity<double> RecordQuantityResistance([Unit("Ohm")] Quantity<double> r, [Unit("A")] Quantity<double> current)
        {
            LastStrictResistance = r;
            return current * r;
        }
    }

    private sealed class BadUnitHost
    {
        [Expose("bad")]
        public void Bad([Unit("not_a_real_unit_xyz")] double x) { }
    }

    [Fact]
    public void Host_net_accepts_plain_numbers_and_SharpUnits_quantities_with_auto_unit_conversion()
    {
        var net = new NetlistHostFixture();
        using var engine = CameoEngine.CreateHermetic();
        engine.Expose("net", net);

        const string script = """
            let r_raw = 1000.0
            let r_pullup = units.qty(2.2, "kOhm")
            let r_parsed = units.parse("4.7 kOhm")
            let v_supply = units.qty(3300.0, "mV")
            let i_bias = units.qty(2.0, "mA")
            let r_derived = v_supply / i_bias
            let r_scaled = 2.0 * units.qty(1.1, "kOhm")

            net.resistor("R0", "in", "out", r_raw)
            net.resistor("R1", "in", "out", r_pullup)
            net.resistor("R2", "in", "out", r_parsed)
            net.resistor("R3", "in", "out", r_derived)
            net.resistor("R4", "in", "out", r_scaled)
            net.capacitor("C1", "out", "0", units.qty(100.0, "nF"))
            net.inductor("L1", "in", "sw", units.qty(4.7, "uH"))
            net.vsrc("V1", "in", "0", v_supply, units.qty(10.0, "mV"))
            net.isrc("I1", "in", "0", i_bias)
            net.pcb_board(units.qty(4.0, "in"), units.qty(2.5, "cm"))

            let fc = 1.0 / (2.0 * math.pi * r_pullup * units.qty(100.0, "nF"))
            net.ac(units.qty(10.0, "Hz"), fc)
            net.tran(units.qty(10.0, "ns"), units.qty(1.0, "ms"))
            """;

        IReadOnlyList<Diagnostic> diags = engine.Check(script);
        Assert.Empty(diags);

        engine.RunString(script);

        Assert.Equal(9, net.Parts.Count);
        Assert.Equal(("R", "R0", 1000.0, 0.0), net.Parts[0]);
        Assert.Equal("R1", net.Parts[1].Id); Assert.Equal(2200.0, net.Parts[1].Value, 9);
        Assert.Equal("R2", net.Parts[2].Id); Assert.Equal(4700.0, net.Parts[2].Value, 9);
        Assert.Equal("R3", net.Parts[3].Id); Assert.Equal(1650.0, net.Parts[3].Value, 9);
        Assert.Equal("R4", net.Parts[4].Id); Assert.Equal(2200.0, net.Parts[4].Value, 9);
        Assert.Equal("C1", net.Parts[5].Id); Assert.Equal(1e-7, net.Parts[5].Value, 15);
        Assert.Equal("L1", net.Parts[6].Id); Assert.Equal(4.7e-6, net.Parts[6].Value, 15);
        Assert.Equal("V1", net.Parts[7].Id); Assert.Equal(3.3, net.Parts[7].Value, 9); Assert.Equal(0.01, net.Parts[7].Secondary, 9);
        Assert.Equal("I1", net.Parts[8].Id); Assert.Equal(0.002, net.Parts[8].Value, 12);

        Assert.Single(net.Boards);
        Assert.Equal(101.6, net.Boards[0].WidthMm, 6);
        Assert.Equal(25.0, net.Boards[0].HeightMm, 6);

        Assert.Single(net.AcSweeps);
        Assert.Equal(10.0, net.AcSweeps[0].StartHz, 9);
        Assert.Equal(1.0 / (2.0 * Math.PI * 2200.0 * 1e-7), net.AcSweeps[0].StopHz, 6);

        Assert.Single(net.TranSweeps);
        Assert.Equal(1e-8, net.TranSweeps[0].StepS, 15);
        Assert.Equal(1e-3, net.TranSweeps[0].StopS, 12);
    }

    [Theory]
    [InlineData("""net.resistor("R1", "in", "out", units.qty(5.0, "V"))""")]
    [InlineData("""net.resistor("R1", "in", "out", units.parse("10 uF"))""")]
    [InlineData("""net.capacitor("C1", "in", "0", units.qty(2.2, "kOhm"))""")]
    [InlineData("""net.inductor("L1", "in", "0", units.qty(5.0, "V"))""")]
    [InlineData("""net.vsrc("V1", "in", "0", units.qty(20.0, "mA"))""")]
    [InlineData("""net.isrc("I1", "in", "0", units.qty(3.3, "V"))""")]
    [InlineData("""net.pcb_board(units.qty(100.0, "Ohm"), units.qty(50.0, "mm"))""")]
    [InlineData("""net.ac(units.qty(1.0, "ms"), units.qty(1.0, "MHz"))""")]
    [InlineData("""net.tran(units.qty(10.0, "kHz"), units.qty(1.0, "ms"))""")]
    [InlineData("""let p = units.qty(5.0, "V") * units.qty(2.0, "A"); net.resistor("R1", "a", "b", p)""")]
    [InlineData("""let s = units.series([3.3, 5.0], "V"); net.resistor("R1", "a", "b", s.mean())""")]
    public void Host_net_rejects_mismatched_physical_dimensions_at_compile_time(string badCall)
    {
        var net = new NetlistHostFixture();
        using var engine = CameoEngine.CreateHermetic();
        engine.Expose("net", net);

        IReadOnlyList<Diagnostic> diags = engine.Check(badCall);
        Assert.NotEmpty(diags);
    }

    [Fact]
    public void Host_strict_quantity_sequences_direct_Quantity_marshalling_and_DefineValue()
    {
        var net = new NetlistHostFixture();
        var bridge = new LuaUnitsBridge();
        using var engine = CameoEngine.CreateHermetic();
        engine.Expose("net", net);
        engine.DefineValue("v_rail", bridge.Qty(3.3, "V"));
        engine.DefineValue("r_std", bridge.Qty(4.7, "kOhm"));

        // AllowNumber = false rejects plain numbers at compile time
        Assert.NotEmpty(engine.Check("""net.resistor_strict("R1", 2200.0)"""));
        // DefineValue preserves static dimension: v_rail (Voltage) rejected by resistor!
        Assert.NotEmpty(engine.Check("""net.resistor("R1", "a", "b", v_rail)"""));
        // Sequences reject mismatched element dimension at compile time
        Assert.NotEmpty(engine.Check("""net.resistor_ladder([units.qty(5.0, "V")])"""));
        // Direct Quantity<double> return preserves [return: Unit("V")]: passing returned voltage to resistor fails at compile time!
        Assert.NotEmpty(engine.Check("""
            let vdrop = net.record_qty_resistance(r_std, units.qty(1.0, "mA"))
            net.resistor("Rbad", "a", "b", vdrop)
            """));

        // Valid end-to-end execution
        Value[] res = engine.RunString("""
            net.resistor_strict("Rs", units.qty(2.2, "kOhm"))
            net.resistor_ladder([units.qty(1.0, "kOhm"), 2200.0, units.parse("4.7 kOhm")])
            let vdrop = net.record_qty_resistance(r_std, units.qty(2.0, "mA"))
            net.vsrc("Vdrop", "a", "0", vdrop)
            net.vsrc("Vrail", "vdd", "0", v_rail)
            return vdrop.in_unit("V").value
            """);

        Assert.Equal(9.4, res[0].N, 9);
        Assert.Equal(("R_STRICT", "Rs", 2200.0, 0.0), net.Parts[0]);
        Assert.NotNull(net.LastLadderOhms);
        Assert.Equal(new[] { 1000.0, 2200.0, 4700.0 }, net.LastLadderOhms!);
        Assert.NotNull(net.LastStrictResistance);
        Assert.Equal(4700.0, net.LastStrictResistance!.Value.SiValue, 9);
        Assert.Equal(9.4, net.Parts[1].Value, 9);
        Assert.Equal(3.3, net.Parts[2].Value, 9);

        // Dynamic unit expression bypasses compile-time tag, still caught cleanly at runtime!
        RunResult dynFail = engine.TryRunString("""
            fn getUnit() -> string { return "V" }
            let dynQ = units.qty(5.0, getUnit())
            net.resistor("Rdyn", "a", "b", dynQ)
            """);
        Assert.True(dynFail.Value is RuntimeFailed rf && rf.Message.Contains("incompatible physical dimensions", StringComparison.OrdinalIgnoreCase));

        // Invalid [Unit("...")] on host C# method fails loudly at Expose() time
        Assert.Throws<ArgumentException>(() => engine.Expose("badhost", new BadUnitHost()));
    }

    [Fact]
    public void Compile_time_dimensional_algebra_annotations_and_zero_PrepareDotCall()
    {
        using var engine = CameoEngine.CreateHermetic();

        const string validScript = """
            let r1: SharpUnits.Quantity<"Ohm"> = units.qty(2.2, "kOhm")
            let r2: SharpUnits.Resistance = units.parse("1.8 kOhm")
            let r3: SharpUnits.Quantity<SharpUnits.Resistance> = r1 + r2
            let u_ohm = units.unit("V") / units.unit("A")
            let r4: SharpUnits.Resistance = units.qty(500.0, u_ohm)
            let total: SharpUnits.Resistance = (r3 + r4).in_unit("Ohm").to_si()
            let noise = units.qty(10.0, "nV/Hz^(1/2)")
            return total.value, r3 > r4, noise.value
            """;

        Assert.Empty(engine.Check(validScript));
        Proto proto = engine.Compile(validScript);
        static bool HasPrepareDotCall(Proto p)
        {
            for (int i = 0; i < p.Code.Length; i++)
                if (p.At(i).Op == OpCode.PrepareDotCall) return true;
            foreach (Proto child in p.Protos)
                if (HasPrepareDotCall(child)) return true;
            return false;
        }
        Assert.False(HasPrepareDotCall(proto));

        Value[] res = engine.RunString(validScript);
        Assert.Equal(4500.0, res[0].N, 9);
        Assert.True(res[1].AsBool);
        Assert.Equal(10.0, res[2].N, 9);

        // Compile-time rejections of illegal dimensional operations & typos
        string[] invalidSnippets =
        [
            """let x = units.qty(5.0, "V") + units.qty(2.0, "kOhm")""",
            """let x = units.qty(5.0, "V") - units.qty(2.0, "A")""",
            """let x = units.qty(5.0, "V") < units.qty(2.0, "kOhm")""",
            """let x = units.qty(5.0, "V") == units.qty(2.0, "kOhm")""",
            """let x = units.qty(5.0, "V").in_unit("Ohm")""",
            """let x = units.qty(5.0, "V").is_equivalent(units.qty(5.0, "A"))""",
            """let x = units.qty(5.0, "kOhmm")""",
            """let x = units.parse("5 blarg")""",
            """let x: SharpUnits.Resistance = units.qty(5.0, "V")""",
            """let x: SharpUnits.Quantity<"Ohm"> = units.qty(10.0, "uF")""",
        ];
        foreach (string snippet in invalidSnippets)
        {
            Assert.NotEmpty(engine.Check(snippet));
        }
    }

    [Fact]
    public void Property_unit_conversions_and_cross_dimension_compile_rejection()
    {
        (string Unit, double Factor)[] rUnits =
        [
            ("mOhm", 1e-3),
            ("Ohm", 1.0),
            ("kOhm", 1e3),
            ("MOhm", 1e6),
        ];
        string[] nonRUnits = ["V", "mV", "A", "mA", "F", "uF", "H", "mH", "Hz", "s", "mm"];

        Gen.Select(Gen.Int[1, 10_000], Gen.Int[0, rUnits.Length - 1], Gen.Int[0, nonRUnits.Length - 1]).Sample(t =>
        {
            var (mag, rIdx, badIdx) = t;
            var (rUnit, factor) = rUnits[rIdx];
            string badUnit = nonRUnits[badIdx];

            var net = new NetlistHostFixture();
            using var engine = CameoEngine.CreateHermetic();
            engine.Expose("net", net);

            engine.RunString($"net.resistor(\"R1\", \"a\", \"b\", units.qty({mag}.0, \"{rUnit}\"))");
            Assert.Single(net.Parts);
            Assert.Equal(mag * factor, net.Parts[0].Value, 6);

            Assert.NotEmpty(engine.Check($"net.resistor(\"Rbad\", \"a\", \"b\", units.qty({mag}.0, \"{badUnit}\"))"));
        }, iter: 40, threads: 1);
    }
}

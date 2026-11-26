using Cameo;
using CsCheck;
using Cameo.Jit;
using Cameo.Runtime.Vm;
using Cameo.Tests.Properties;
using Xunit;

namespace Cameo.Tests;

/// <summary>Verifies unified Cameo dot calls across all built-in instance types and zero PrepareDotCall overhead in checked bytecode.</summary>
public sealed class CameoUnifiedDotTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Builtin_instances_support_direct_dot_calls_uniformly(bool check)
    {
        using var engine = new CameoEngine(new CameoRuntimeOptions { TypeCheck = check });
        var values = engine.RunString("""
            let r: Result<integer, string> = ok(21)
            let doubled = r.map((n) => n * 2)
            let bad: Result<integer, string> = err("nope")
            let fallback = bad.unwrapOr(99)

            let q = Query.from({1, 2, 3, 4})
                .where((n) => n > 1)
                .select((n) => n * 10)
                .toList()

            let s = "  hello,world  ".trim().upper()
            let parts = s.split(",")

            let fa = FloatArray.from({1.0, 2.0, 3.0})
            let fsum = fa.sum()

            let u = utf16.new("hi").upper()

            let f = assert(io.tmpfile())
            f.write("cameo-file")
            f.seek("set", 0)
            let content = f.read("*a")
            f.close()

            let re = assert(regex.new("^[a-z]+$"))
            let reOk = re.test("abc")

            return doubled.isOk(), doubled.value(), bad.isErr(), fallback,
                table.concat(q, "-"), parts[1], parts[2], fsum, u.tostring(), content, reOk
            """);
        Assert.True(values[0].IsTruthy);
        Assert.Equal(42, values[1].I);
        Assert.True(values[2].IsTruthy);
        Assert.Equal(99, values[3].I);
        Assert.Equal("20-30-40", values[4].AsString);
        Assert.Equal("HELLO", values[5].AsString);
        Assert.Equal("WORLD", values[6].AsString);
        Assert.Equal(6.0, values[7].N);
        Assert.Equal("HI", values[8].AsString);
        Assert.Equal("cameo-file", values[9].AsString);
        Assert.True(values[10].IsTruthy);
    }

    [Fact]
    public void Checked_cameo_emits_zero_PrepareDotCall_for_module_and_known_instance_calls()
    {
        using var engine = new CameoEngine(new CameoRuntimeOptions { TypeCheck = true });
        Proto proto = engine.Compile("""
            fn compute(xs: {integer}, text: string) -> number {
                let s = text.trim().upper()
                let total = Query.from(xs)
                    .where((n) => n > 0)
                    .select((n) => math.abs(n))
                    .sum()
                let job = Future.completedFuture(total + s.len())
                let res: Result<number, string> = ok(job.get())
                return res.unwrapOr(0)
            }
            return compute({-1, 2, 3}, " hi ")
            """, "zero-overhead.cameo");

        static void AssertNoPrepareDotCall(Proto p)
        {
            Assert.DoesNotContain(p.Code, ins => ins.Op == OpCode.PrepareDotCall);
            foreach (Proto child in p.Protos) AssertNoPrepareDotCall(child);
        }

        AssertNoPrepareDotCall(proto);
        Value[] result = engine.State.Run(proto);
        Assert.Equal(7.0, result[0].AsDouble);
    }

    [Fact]
    public void Checked_cameo_module_calls_keep_jit_numeric_specialization()
    {
        using var engine = new CameoEngine(new CameoRuntimeOptions { TypeCheck = true });
        JitLibrary.Enable(engine.Runtime);
        Value[] values = engine.RunString("""
            fn sumAbs(n: integer) -> integer {
                var acc = 0
                for i = 1, n {
                    acc += math.abs(-i)
                }
                return acc
            }
            return sumAbs
            """);
        Assert.True(JitLibrary.TryCompile(values[0], out Value compiled, out var reason), reason?.ToString());
        Assert.Equal(5050, engine.State.Call(compiled, 100)[0].I);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Static_module_calls_and_extracted_methods_remain_unbound(bool check)
    {
        using var engine = new CameoEngine(new CameoRuntimeOptions { TypeCheck = check });
        var values = engine.RunString("""
            let r: Result<integer, string> = Result.fromPair(42, nil)
            let readValue = r.value
            let upper = ("hi").upper
            return readValue(r), Result.isOk(r), upper("ok"), string.upper("ok")
            """);
        Assert.Equal(42, values[0].I);
        Assert.True(values[1].IsTruthy);
        Assert.Equal("OK", values[2].AsString);
        Assert.Equal("OK", values[3].AsString);
    }

    [Fact]
    public void Counterfeit_result_table_does_not_bind_self()
    {
        using var engine = new CameoEngine(new CameoRuntimeOptions { TypeCheck = false });
        var values = engine.RunString("""
            let fake = { isOk = Result.isOk }
            let okCall, _ = pcall(() => fake.isOk())
            return okCall
            """);
        Assert.False(values[0].IsTruthy);
    }

    [Fact]
    public void Property_string_dot_methods_match_teal_string_module_functions()
    {
        Prop.Cs(Gen.String[Gen.Char.AlphaNumeric, 0, 32], input =>
        {
            string padded = "  " + input + "  ";
            using var cameo = new CameoEngine();
            using var teal = new ScriptEngine();
            cameo.State.SetGlobal("s", Value.FromText(padded));
            teal.State.SetGlobal("s", Value.FromText(padded));
            Value[] c = cameo.RunString("global s: string return s.trim().upper(), s.len()");
            Value[] t = teal.RunString("global s: string return string.upper(string.trim(s)), string.len(s)");
            Assert.Equal(t[0].AsString, c[0].AsString);
            Assert.Equal(t[1].I, c[1].I);
        });
    }
}

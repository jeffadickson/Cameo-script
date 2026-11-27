using Cameo;
using CsCheck;
using Cameo.Jit;
using Cameo.Runtime.Vm;
using Cameo.Tests.Properties;
using Xunit;

namespace Cameo.Tests;

/// <summary>Verifies Cameo ergonomic features: !=, |>, rich try/catch, Result worker transfer, and Perl-style CLI one-liners.</summary>
public sealed class CameoErgonomicsTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Not_equal_operator_works_in_cameo_and_is_rejected_in_teal(bool check)
    {
        using var cameo = new CameoEngine(new CameoRuntimeOptions { TypeCheck = check });
        var values = cameo.RunString("return 1 != 2, 2 != 2, nil != false");
        Assert.True(values[0].IsTruthy);
        Assert.False(values[1].IsTruthy);
        Assert.True(values[2].IsTruthy);

        using var teal = new ScriptEngine(new CameoRuntimeOptions { TypeCheck = check });
        Assert.IsType<CompileFailed>(teal.TryRunString("return 1 != 2").Value);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Pipeline_operator_composes_functions_multiargs_and_unwrap(bool check)
    {
        using var cameo = new CameoEngine(new CameoRuntimeOptions { TypeCheck = check });
        var values = cameo.RunString("""
            fn add(a: integer, b: integer) -> integer { return a + b }
            fn mul(a: integer, b: integer) -> integer { return a * b }

            let n = 3
                |> add(4)
                |> mul(6)

            let text = "  hello  "
                |> string.trim
                |> string.upper

            let decoded = '{"count": 42}'
                |> json.decode
                |> unwrap

            return n, text, (decoded as {string: integer}).count
            """);
        Assert.Equal(42, values[0].I);
        Assert.Equal("HELLO", values[1].AsString);
        Assert.Equal(42, values[2].I);

        using var teal = new ScriptEngine(new CameoRuntimeOptions { TypeCheck = check });
        Assert.IsType<CompileFailed>(teal.TryRunString("return 'hi' |> string.upper").Value);
    }

    [Fact]
    public void Pipeline_works_with_jit_compilation()
    {
        using var engine = new CameoEngine();
        JitLibrary.Enable(engine.Runtime);
        Value[] values = engine.RunString("""
            fn inc(n: integer) -> integer { return n + 1 }
            fn twice(n: integer) -> integer { return n * 2 }
            fn f(x: integer) -> integer { return x |> inc |> twice }
            return f
            """);
        Assert.True(JitLibrary.TryCompile(values[0], out Value compiled, out var reason), reason?.ToString());
        Assert.Equal(42, engine.State.Call(compiled, 20)[0].I);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Try_supports_var_bindings_parenthesized_operands_and_catch_all(bool check)
    {
        using var engine = new CameoEngine(new CameoRuntimeOptions { TypeCheck = check });
        var values = engine.RunString("""
            fn parse(s: string) -> Result<integer, string> {
                let n = math.tointeger(tonumber(s))
                if n == nil { return err("bad:" .. s) }
                return ok(n)
            }

            fn parseOrZero(s: string) -> integer {
                var value = try (parse(s)) catch _ { 0 }
                value += 1
                return value
            }

            fn parseRecoverString(s: string) -> string {
                let text = try parse(s).map((n) => tostring(n))
                    catch (reason) { "err=" .. reason }
                return text
            }

            return parseOrZero("41"), parseOrZero("oops"), parseRecoverString("7"), parseRecoverString("abc")
            """);
        Assert.Equal(42, values[0].I);
        Assert.Equal(1, values[1].I);
        Assert.Equal("7", values[2].AsString);
        Assert.Equal("err=bad:abc", values[3].AsString);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Exhaustive_named_catch_with_binding_works_in_non_result_function(bool check)
    {
        using var engine = new CameoEngine(new CameoRuntimeOptions { TypeCheck = check });
        var values = engine.RunString("""
            record Problem {
                enum Kind {
                    "timeout", "cancelled"
                }
                kind: Kind
                detail: string
            }

            fn fetch(kind: Problem.Kind) -> Result<string, Problem> {
                return err({kind = kind, detail = "from-" .. kind})
            }

            fn describe(kind: Problem.Kind) -> string {
                let answer = try fetch(kind)
                    catch timeout(e) { "T:" .. e.detail }
                    catch cancelled(e) { "C:" .. e.detail }
                return answer
            }

            return describe("timeout"), describe("cancelled")
            """);
        Assert.Equal("T:from-timeout", values[0].AsString);
        Assert.Equal("C:from-cancelled", values[1].AsString);
    }

    [Fact]
    public void Catch_all_must_be_last_and_not_duplicated()
    {
        using var engine = new CameoEngine();
        Assert.NotEmpty(engine.Check("""
            fn f(r: Result<integer, FutureError>) -> integer {
                let x = try r catch _ { 0 } catch timeout { 1 }
                return x
            }
            """));
        Assert.NotEmpty(engine.Check("""
            fn f(r: Result<integer, string>) -> integer {
                let x = try r catch (a) { 0 } catch (b) { 1 }
                return x
            }
            """));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Tagged_result_transfers_across_parallel_workers_and_sandboxes(bool check)
    {
        using var engine = new CameoEngine(new CameoRuntimeOptions { TypeCheck = check });
        var values = engine.RunString("""
            fn worker(flag: boolean) -> Result<{string: integer}, boolean> {
                if flag { return ok({answer = 42}) }
                return err(false)
            }

            let r1 = Future.parallel(worker, true).get()
            let r2 = Future.parallel(worker, false).get()
            let r3 = sandbox.run("return Result.ok(99)").get() as Result<integer, string>
            return r1.isOk(), r1.value().answer, r2.isErr(), r2.issue(), r3.isOk(), r3.value()
            """);
        Assert.True(values[0].IsTruthy);
        Assert.Equal(42, values[1].I);
        Assert.True(values[2].IsTruthy);
        Assert.True(values[3].IsBoolean && !values[3].IsTruthy);
        Assert.True(values[4].IsTruthy);
        Assert.Equal(99, values[5].I);
    }

    [Fact]
    public void Cameo_cli_supports_inline_eval_and_perl_style_oneliners()
    {
        // Inline -e
        var parsedEval = CameoCommands.Parse(["-e", "let x = 21 |> (n: integer) => n * 2 print(x)"]);
        Assert.Null(parsedEval.Error);
        var outEval = new StringWriter { NewLine = "\n" };
        var resEval = CameoCommands.Execute(parsedEval.Command, outEval, TextWriter.Null);
        Assert.Equal(0, resEval.ExitCode);
        Assert.Equal("42\n", outEval.ToString());

        // -n -a -F ',' with --begin/--end over a file
        string dir = Directory.CreateTempSubdirectory("cameo-oneliner-").FullName;
        string csv = Path.Combine(dir, "items.csv");
        try
        {
            File.WriteAllText(csv, "ada,10\nbob,25\nclaire,7\n");
            var parsed = CameoCommands.Parse([
                "-n", "-a", "-F", ",",
                "--begin", "var total = 0",
                "--end", "print($\"sum={total}\")",
                "-e", "if fields[1] != 'bob' { total += tonumber(fields[2]) }",
                csv
            ]);
            Assert.Null(parsed.Error);
            var stdout = new StringWriter { NewLine = "\n" };
            var result = CameoCommands.Execute(parsed.Command, stdout, TextWriter.Null);
            Assert.Equal(0, result.ExitCode);
            Assert.Equal("sum=17\n", stdout.ToString());

            // -p -i.bak in-place transform
            var parsedInPlace = CameoCommands.Parse(["-p", "-i.bak", "-e", "line = line.upper()", csv]);
            Assert.Null(parsedInPlace.Error);
            var resInPlace = CameoCommands.Execute(parsedInPlace.Command, TextWriter.Null, TextWriter.Null);
            Assert.Equal(0, resInPlace.ExitCode);
            Assert.Equal("ADA,10\nBOB,25\nCLAIRE,7\n", File.ReadAllText(csv));
            Assert.Equal("ada,10\nbob,25\nclaire,7\n", File.ReadAllText(csv + ".bak"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Property_pipelines_and_parallel_result_roundtrip_preserve_values()
    {
        Prop.Cs(Gen.Select(Gen.Int[-1000, 1000], Gen.Int[-100, 100], Gen.Bool), input =>
        {
            var (a, b, isOk) = input;
            using var engine = new CameoEngine();
            engine.State.SetGlobal("a", a);
            engine.State.SetGlobal("b", b);
            engine.State.SetGlobal("flag", isOk);
            var values = engine.RunString("""
                global a: integer
                global b: integer
                global flag: boolean
                fn add(x: integer, y: integer) -> integer { return x + y }
                fn square(x: integer) -> integer { return x * x }
                fn make(f: boolean, v: integer) -> Result<integer, string> {
                    if f { return ok(v) }
                    return err("e" .. tostring(v))
                }
                let piped = a |> add(b) |> square
                let direct = square(add(a, b))
                let res = Future.parallel(make, flag, piped).get()
                return piped, direct, res.isOk(), res.isOk() && res.value() || res.issue()
                """);
            long expected = (long)(a + b) * (a + b);
            Assert.Equal(expected, values[0].I);
            Assert.Equal(expected, values[1].I);
            Assert.Equal(isOk, values[2].IsTruthy);
            if (isOk) Assert.Equal(expected, values[3].I);
            else Assert.Equal("e" + expected, values[3].AsString);
        }, iter: 40);
    }
}

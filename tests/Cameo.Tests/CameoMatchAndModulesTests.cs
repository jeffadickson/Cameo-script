using Cameo;
using CsCheck;
using Cameo.Runtime;
using Cameo.Runtime.Vm;
using Cameo.Runtime.Syntax;
using Xunit;

namespace Cameo.Tests;

public sealed class CameoMatchAndModulesTests
{
    private static Value[] Run(string source, bool typeCheck = true, bool hermetic = false)
    {
        using var engine = hermetic
            ? CameoEngine.CreateHermetic(typeCheck: typeCheck)
            : new CameoEngine(new CameoRuntimeOptions { TypeCheck = typeCheck });
        return engine.RunString(source);
    }

    private static IReadOnlyList<Diagnostic> Check(string source)
    {
        using var engine = new CameoEngine();
        return engine.Check(source);
    }

    // ------------------------------------------------------------------ match

    [Fact]
    public void Match_literals_or_patterns_blocks_and_statements()
    {
        Value[] res = Run("""
            fn classify(n: integer) -> string {
                return match n {
                    -1 => "neg",
                    0 => "zero",
                    1 | 2 | 3 => "small",
                    _ => {
                        let s = tostring(n)
                        "big:" .. s
                    }
                }
            }
            var acc = 0
            for x in [-1, 0, 2, 10] {
                match x {
                    -1 => { acc += 1 }
                    0 | 2 => { acc += 10 }
                    _ => { acc += 100 }
                }
            }
            return classify(-1), classify(0), classify(2), classify(9), acc
            """);

        Assert.Equal("neg", res[0].AsString);
        Assert.Equal("zero", res[1].AsString);
        Assert.Equal("small", res[2].AsString);
        Assert.Equal("big:9", res[3].AsString);
        Assert.Equal(121L, res[4].I);
    }

    [Fact]
    public void Match_type_narrowing_with_bindings_and_records()
    {
        Value[] res = Run("""
            record Bot {
                name: string
                fn greet() -> string { return "beep:" .. self.name }
            }
            record Human {
                name: string
                fn wave() -> string { return "hi:" .. self.name }
            }
            fn describe(x: Bot | Human | integer | string | nil) -> string {
                return match x {
                    is Bot(b) => b.greet(),
                    is Human(h) => h.wave(),
                    is integer(n) => "int:" .. tostring(n * 2),
                    is string(s) => "str:" .. s.upper(),
                    nil => "none",
                    _ => "other",
                }
            }
            return describe(Bot { name = "r2" }),
                   describe(Human { name = "ada" }),
                   describe(21),
                   describe("ok"),
                   describe(nil)
            """);

        Assert.Equal("beep:r2", res[0].AsString);
        Assert.Equal("hi:ada", res[1].AsString);
        Assert.Equal("int:42", res[2].AsString);
        Assert.Equal("str:OK", res[3].AsString);
        Assert.Equal("none", res[4].AsString);
    }

    [Fact]
    public void Match_enforces_enum_exhaustiveness_and_rejects_unknown_members()
    {
        Value[] res = Run("""
            enum Color { "red", "green", "blue" }
            fn code(c: Color) -> integer {
                return match c {
                    "red" => 1,
                    "green" | "blue" => 2,
                }
            }
            return code("red"), code("blue")
            """);
        Assert.Equal(1L, res[0].I);
        Assert.Equal(2L, res[1].I);

        IReadOnlyList<Diagnostic> missing = Check("""
            enum Color { "red", "green", "blue" }
            fn code(c: Color) -> integer {
                return match c {
                    "red" => 1,
                    "green" => 2,
                }
            }
            """);
        Assert.Contains(missing, d => d.Message.Contains("non-exhaustive") && d.Message.Contains("blue"));

        IReadOnlyList<Diagnostic> bogus = Check("""
            enum Color { "red", "green" }
            fn code(c: Color) -> integer {
                return match c {
                    "red" => 1,
                    "purple" => 2,
                    _ => 0,
                }
            }
            """);
        Assert.Contains(bogus, d => d.Message.Contains("purple") && d.Message.Contains("Color"));
    }

    [Fact]
    public void Match_remains_contextual_so_string_match_and_identifiers_work()
    {
        Value[] res = Run("""
            let match = "hello 42"
            let found = match.match("%d+")
            return found
            """);
        Assert.Equal("42", res[0].AsString);
    }

    [Fact]
    public void Property_match_expression_equals_csharp_switch()
    {
        using var engine = CameoEngine.CreateHermetic();
        engine.RunString("""
            fn route(n: integer) -> integer {
                return match n {
                    -2 | -1 => -10,
                    0 => 0,
                    1 | 2 => n * 5,
                    3 => {
                        let k = 7
                        k * k
                    },
                    _ => n + 100,
                }
            }
            global runRoute: function(integer): integer = route
            """);
        Value fn = engine.State.GetGlobal("runRoute");

        Gen.Int[-5, 10].Sample(n =>
        {
            long expected = n switch
            {
                -2 or -1 => -10,
                0 => 0,
                1 or 2 => n * 5,
                3 => 49,
                _ => n + 100,
            };
            Value[] r = engine.State.Call(fn, Value.FromInt(n));
            Assert.Equal(expected, r[0].I);
        }, iter: 80, threads: 1);
    }

    // ------------------------------------------------------------------ required vs optional record fields

    [Fact]
    public void Cameo_record_required_and_optional_fields_checked()
    {
        Value[] res = Run("""
            record Server {
                host: string
                port: integer
                label?: string
                fn endpoint() -> string {
                    let tag = self.label ?? "default"
                    return $"{self.host}:{self.port} ({tag})"
                }
            }
            let s1: Server = { host = "127.0.0.1", port = 8080 }
            let s2 = Server { host = "10.0.0.1", port = 443, label = "edge" }
            return s1.endpoint(), s2.endpoint(), s1.label == nil
            """);

        Assert.Equal("127.0.0.1:8080 (default)", res[0].AsString);
        Assert.Equal("10.0.0.1:443 (edge)", res[1].AsString);
        Assert.True(res[2].AsBool);

        IReadOnlyList<Diagnostic> missingPort = Check("""
            record Server {
                host: string
                port: integer
                label?: string
            }
            let bad: Server = { host = "127.0.0.1" }
            """);
        Assert.Contains(missingPort, d => d.Message.Contains("missing required field 'port'"));

        IReadOnlyList<Diagnostic> emptyBad = Check("""
            record Server {
                host: string
                label?: string
            }
            let bad: Server = {}
            """);
        Assert.Contains(emptyBad, d => d.Message.Contains("missing required field 'host'"));
    }

    // ------------------------------------------------------------------ import / export modules

    [Fact]
    public void Import_and_export_support_values_records_interfaces_enums_and_hermetic_freezing()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cameo-mod-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "geom.cameo"), """
                export enum Kind { "circle", "box" }

                export interface Measurable {
                    fn area() -> number
                }

                export record Box implements Measurable {
                    w: number
                    h: number
                    label?: string
                    fn area() -> number { return self.w * self.h }
                }

                export let DEFAULTS = { scale = 2, tags = ["metric"] }

                export fn describe(k: Kind, m: Measurable) -> string {
                    return match k {
                        "circle" => $"circle:{m.area()}",
                        "box" => $"box:{m.area()}",
                    }
                }
                """);

            using var engine = CameoEngine.CreateHermetic(modulePaths: [dir]);
            Value[] res = engine.RunString("""
                import { Kind, Measurable, Box, DEFAULTS, describe as fmt } from "geom"
                import geom from "geom"

                let b: Measurable = Box { w = 3.0, h = 4.0 }
                let k: Kind = "box"
                let frozen = isFrozen(DEFAULTS) && isFrozen(DEFAULTS.tags)
                let okMut, err = pcall(() => { DEFAULTS.scale = 99 })
                return fmt(k, b), geom.DEFAULTS.scale, frozen, okMut, tostring(err)
                """);

            Assert.Equal("box:12.0", res[0].AsString);
            Assert.Equal(2L, res[1].I);
            Assert.True(res[2].AsBool);
            Assert.False(res[3].AsBool);
            Assert.Contains("frozen", res[4].AsString, StringComparison.OrdinalIgnoreCase);

            // Missing export is a compile-time error
            IReadOnlyList<Diagnostic> badImport = engine.Check("""
                import { doesNotExist } from "geom"
                return doesNotExist
                """);
            Assert.Contains(badImport, d => d.Message.Contains("no export 'doesNotExist'"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Export_with_explicit_top_level_return_is_rejected_and_identifiers_remain_contextual()
    {
        Assert.Throws<CameoCompileException>(() => Run("""
            export let x = 1
            return { x = x }
            """));

        Value[] res = Run("""
            let import = 10
            let export = 32
            return import + export
            """);
        Assert.Equal(42L, res[0].I);
    }
}

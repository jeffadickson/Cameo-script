using Cameo;
using CsCheck;
using Cameo.Runtime.Vm;
using Cameo.Runtime.Stdlib;
using Xunit;

namespace Cameo.Tests;

/// <summary>
/// Verifies Cameo's interface model: braced interfaces, inline record methods, explicit `implements`
/// verification, structural conformance for records and plain table literals, unified `.` dispatch
/// (zero PrepareDotCall in checked mode), generics, optional chaining, cross-module records,
/// runtime `is`/`instanceof`, unchecked/sandboxed execution, and JIT parity.
/// </summary>
public class CameoInterfaceTests
{
    [Fact]
    public void Explicit_implements_with_inline_methods_and_unified_dot_calls()
    {
        const string source = """
            interface Greeter {
                fn greet(name: string) -> string
            }

            interface Named {
                name: string
                fn label() -> string
            }

            interface LoudGreeter extends Greeter, Named {
                fn shout(name: string) -> string
            }

            record Bot implements LoudGreeter {
                name: string
                prefix: string

                fn greet(name: string) -> string {
                    return $"{self.prefix}, {name}!"
                }

                fn label() -> string {
                    return $"Bot({self.name})"
                }

                fn shout(name: string) -> string {
                    return self.greet(name).upper()
                }

                static fn create(name: string, prefix: string) -> Bot {
                    return { name = name, prefix = prefix }
                }
            }

            fn welcome(g: Greeter, who: string) -> string {
                return g.greet(who)
            }

            let b1: Bot = { name = "R2", prefix = "Beep" }
            let b2 = Bot { name = "C3", prefix = "Hello" }
            let b3 = Bot.create("BB8", "Bloop")
            let b4 = { name = "K2", prefix = "Hi" } as Bot
            let lg: LoudGreeter = b1

            return welcome(b1, "Luke"),
                   b2.greet("Leia"),
                   b3.label(),
                   b4.shout("Cassian"),
                   lg.shout("Han"),
                   lg:greet("Chewie"),
                   Bot.greet(b1, "Obi"),
                   b1 is Bot,
                   instanceof(b2, Bot)
            """;

        using var engine = new CameoEngine();
        Assert.Empty(engine.Check(source));

        // Checked Cameo must emit zero PrepareDotCall for interface/record method and static calls.
        Proto proto = engine.Compile(source, "interfaces.cameo");
        static void AssertNoPrepareDotCall(Proto p)
        {
            Assert.DoesNotContain(p.Code, ins => ins.Op == OpCode.PrepareDotCall);
            foreach (Proto child in p.Protos) AssertNoPrepareDotCall(child);
        }
        AssertNoPrepareDotCall(proto);

        Value[] r = engine.RunString(source);
        Assert.Equal("Beep, Luke!", r[0].ToText());
        Assert.Equal("Hello, Leia!", r[1].ToText());
        Assert.Equal("Bot(BB8)", r[2].ToText());
        Assert.Equal("HI, CASSIAN!", r[3].ToText());
        Assert.Equal("BEEP, HAN!", r[4].ToText());
        Assert.Equal("Beep, Chewie!", r[5].ToText());
        Assert.Equal("Beep, Obi!", r[6].ToText());
        Assert.True(r[7].IsTruthy);
        Assert.True(r[8].IsTruthy);
    }

    [Fact]
    public void Explicit_implements_catches_missing_and_mismatched_members()
    {
        using var engine = new CameoEngine();

        // Missing method
        var d1 = engine.Check("""
            interface Greeter { fn greet(name: string) -> string }
            record Silent implements Greeter {
                prefix: string
            }
            """);
        Assert.Contains(d1, d => d.Message.Contains("does not implement 'greet'"));

        // Missing data field
        var d2 = engine.Check("""
            interface Named { name: string, fn label() -> string }
            record Nameless implements Named {
                fn label() -> string { return "?" }
            }
            """);
        Assert.Contains(d2, d => d.Message.Contains("does not implement 'name'"));

        // Wrong return type
        var d3 = engine.Check("""
            interface Greeter { fn greet(name: string) -> string }
            record BadReturn implements Greeter {
                fn greet(name: string) -> integer { return #name }
            }
            """);
        Assert.Contains(d3, d => d.Message.Contains("does not match definition in interface"));

        // Wrong parameter type
        var d4 = engine.Check("""
            interface Greeter { fn greet(name: string) -> string }
            record BadParam implements Greeter {
                fn greet(n: integer) -> string { return tostring(n) }
            }
            """);
        Assert.Contains(d4, d => d.Message.Contains("does not match definition in interface"));

        // Static fn cannot satisfy an instance method
        var d5 = engine.Check("""
            interface Greeter { fn greet(name: string) -> string }
            record StaticOnly implements Greeter {
                static fn greet(name: string) -> string { return name }
            }
            """);
        Assert.Contains(d5, d => d.Message.Contains("is not an instance method") || d.Message.Contains("does not match definition"));
    }

    [Fact]
    public void Structural_conformance_works_for_records_and_plain_table_literals()
    {
        const string source = """
            interface Greeter {
                fn greet(name: string) -> string
            }

            -- Explicit `implements`
            record Bot implements Greeter {
                prefix: string
                fn greet(name: string) -> string {
                    return $"{self.prefix}, {name}!"
                }
            }

            -- No `implements` clause: satisfies Greeter structurally
            record Human {
                first: string
                fn greet(name: string) -> string {
                    return $"Hi {name}, I'm {self.first}"
                }
            }

            let bot: Bot = { prefix = "Beep" }
            let human: Human = { first = "Ada" }

            -- Plain table literal with arrow (contextual parameter type inferred, self dropped)
            let arrowGreeter: Greeter = {
                greet = (name) => $"Yo, {name}!"
            }

            -- Plain table literal with a pre-existing function variable
            let helper = (name: string) => $"Hey {name}!"
            let varGreeter: Greeter = {
                greet = helper
            }

            -- Plain table literal with explicit `self`
            let explicitGreeter: Greeter = {
                greet = function(self: Greeter, name: string): string
                    return $"Self({type(self)}): {name}"
                end
            }

            let greeters: {Greeter} = { bot, human, arrowGreeter, varGreeter, explicitGreeter }
            let out: {string} = {}
            for _, g in ipairs(greeters) {
                table.insert(out, g.greet("Grace"))
            }
            return table.concat(out, " | "), human is Bot
            """;

        using var engine = new CameoEngine();
        Assert.Empty(engine.Check(source));
        Value[] r = engine.RunString(source);
        Assert.Equal(
            "Beep, Grace! | Hi Grace, I'm Ada | Yo, Grace! | Hey Grace! | Self(table): Grace",
            r[0].ToText());
        Assert.False(r[1].IsTruthy);
    }

    [Fact]
    public void Structural_conformance_rejects_non_matching_records_and_incomplete_table_literals()
    {
        using var engine = new CameoEngine();

        // Record missing required method
        var d1 = engine.Check("""
            interface Greeter { fn greet(name: string) -> string }
            record Rock { weight: integer }
            let r: Rock = { weight = 5 }
            let g: Greeter = r
            """);
        Assert.NotEmpty(d1);

        // Record with incompatible return type
        var d2 = engine.Check("""
            interface Greeter { fn greet(name: string) -> string }
            record Counter {
                fn greet(name: string) -> integer { return #name }
            }
            let c: Counter = {}
            let g: Greeter = c
            """);
        Assert.NotEmpty(d2);

        // Plain table literal missing required method
        var d3 = engine.Check("""
            interface Greeter { fn greet(name: string) -> string }
            let bad: Greeter = {}
            """);
        Assert.Contains(d3, d => d.Message.Contains("missing 'greet'"));

        // Plain Teal stays nominal (does NOT structurally match)
        using var teal = new ScriptEngine();
        var dt = teal.Check("""
            local interface Named name: string end
            local record Person name: string end
            local p: Person = { name = "Ada" }
            local n: Named = p
            """, "test.tl");
        Assert.NotEmpty(dt);
    }

    [Fact]
    public void Generic_interfaces_optional_chaining_and_recursive_interfaces_work()
    {
        const string source = """
            interface Readable<T> {
                fn get() -> T
            }

            interface Chainable {
                fn next() -> Chainable
                fn value() -> integer
            }

            record Cell<T> implements Readable<T> {
                item: T
                fn get() -> T {
                    return self.item
                }
            }

            record Holder<T> {
                item: T
                fn get() -> T {
                    return self.item
                }
            }

            record Step {
                n: integer
                fn next() -> Chainable {
                    let s: Step = { n = self.n + 1 }
                    return s
                }
                fn value() -> integer {
                    return self.n
                }
            }

            let ci: Cell<integer> = { item = 42 }
            let cs = Cell { item = "ok" }
            let hi: Holder<integer> = { item = 99 }
            let r1: Readable<integer> = ci
            let r2: Readable<string> = cs
            let r3: Readable<integer> = hi

            let maybeNil: Readable<integer> = nil
            let fallback = maybeNil?.get() ?? -1
            let present = r1?.get() ?? -1

            let st: Step = { n = 10 }
            let ch: Chainable = st
            let chained = ch.next().next().value()

            return r1.get(), r2.get(), r3.get(), fallback, present, chained
            """;

        using var engine = new CameoEngine();
        Assert.Empty(engine.Check(source));
        Value[] r = engine.RunString(source);
        Assert.Equal(42, r[0].I);
        Assert.Equal("ok", r[1].ToText());
        Assert.Equal(99, r[2].I);
        Assert.Equal(-1, r[3].I);
        Assert.Equal(42, r[4].I);
        Assert.Equal(12, r[5].I);
    }

    [Fact]
    public void Cross_module_unchecked_sandboxed_and_jit_execution_work()
    {
        // 1. Cross-module record construction & interface satisfaction
        string dir = Path.Combine(Path.GetTempPath(), "cameo-iface-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "bots.cameo"), """
                record Bot {
                    prefix: string
                    fn greet(who: string) -> string {
                        return $"{self.prefix}, {who}!"
                    }
                }
                return { Bot = Bot }
                """);

            using var modEngine = new CameoEngine(new CameoRuntimeOptions { ModulePaths = [dir] });
            const string mainSrc = """
                let bots = require("bots")
                interface Greeter {
                    fn greet(who: string) -> string
                }
                let b1 = bots.Bot { prefix = "Mod" }
                let b2: bots.Bot = { prefix = "Lit" }
                let g1: Greeter = b1
                let g2: Greeter = b2
                return g1.greet("A"), g2.greet("B")
                """;
            Assert.Empty(modEngine.Check(mainSrc));
            Value[] mr = modEngine.RunString(mainSrc);
            Assert.Equal("Mod, A!", mr[0].ToText());
            Assert.Equal("Lit, B!", mr[1].ToText());
        }
        finally
        {
            Directory.Delete(dir, true);
        }

        // 2. Unchecked mode (`TypeCheck = false`) uses PrepareDotCall on blessed record instances
        using (var uncheckedEngine = new CameoEngine(new CameoRuntimeOptions { TypeCheck = false }))
        {
            Value[] ur = uncheckedEngine.RunString("""
                record Bot {
                    prefix: string
                    fn greet(who: string) -> string {
                        return $"{self.prefix}, {who}!"
                    }
                }
                let b = Bot { prefix = "Unchecked" }
                return b.greet("World")
                """);
            Assert.Equal("Unchecked, World!", ur[0].ToText());
        }

        // 3. Sandboxed mode (`CameoLibraries.Sandboxed`) supports interfaces and records
        using (var sandboxed = new CameoEngine(new CameoRuntimeOptions { Libraries = CameoLibraries.Sandboxed }))
        {
            Value[] sr = sandboxed.RunString("""
                interface Adder { fn add(x: integer) -> integer }
                record Acc implements Adder {
                    base: integer
                    fn add(x: integer) -> integer { return self.base + x }
                }
                let a: Adder = Acc { base = 10 }
                return a.add(32), io == nil
                """);
            Assert.Equal(42, sr[0].I);
            Assert.True(sr[1].IsTruthy);
        }

        // 4. JIT compiled function calling interface methods matches interpreter
        using (var jitEngine = new CameoEngine())
        {
            const string jitSrc = """
                interface Stepper {
                    fn step(acc: integer, i: integer) -> integer
                }
                record Linear implements Stepper {
                    scale: integer
                    fn step(acc: integer, i: integer) -> integer {
                        return acc + i * self.scale
                    }
                }
                fn run(s: Stepper, n: integer) -> integer {
                    var total = 0
                    for i = 1, n {
                        total = s.step(total, i)
                    }
                    return total
                }
                let lin: Stepper = Linear { scale = 3 }
                let interp = run(lin, 200)
                let compiled = assert(jit.compile(run))
                let jitted = compiled(lin, 200)
                return interp, jitted
                """;
            Assert.Empty(jitEngine.Check(jitSrc));
            Value[] jr = jitEngine.RunString(jitSrc);
            Assert.Equal(60300, jr[0].I);
            Assert.Equal(jr[0].I, jr[1].I);
        }
    }

    [Fact]
    public void Polymorphic_interface_dispatch_matches_reference_model_property()
    {
        using var engine = new CameoEngine();
        const string setup = """
            interface Scorer {
                fn score(x: integer) -> integer
            }
            record Offset implements Scorer {
                k: integer
                fn score(x: integer) -> integer { return x + self.k }
            }
            record Scale {
                m: integer
                fn score(x: integer) -> integer { return x * self.m }
            }
            fn evalAll(k: integer, m: integer, c: integer, x: integer) -> integer {
                let s1: Scorer = Offset { k = k }
                let s2: Scorer = Scale { m = m }
                let s3: Scorer = { score = (v) => v - c }
                let list: {Scorer} = { s1, s2, s3 }
                var sum = 0
                for _, s in ipairs(list) {
                    sum += s.score(x)
                }
                return sum
            }
            global runEval: function(integer, integer, integer, integer): integer = evalAll
            """;
        Assert.Empty(engine.Check(setup));
        engine.RunString(setup);
        Value fn = engine.State.GetGlobal("runEval");

        Gen.Int[-1000, 1000].Select(Gen.Int[-100, 100], Gen.Int[-1000, 1000], Gen.Int[-1000, 1000])
            .Sample((k, m, c, x) =>
            {
                long expected = (long)(x + k) + (long)(x * m) + (long)(x - c);
                Value[] res = engine.State.Call(fn, Value.FromInt(k), Value.FromInt(m), Value.FromInt(c), Value.FromInt(x));
                Assert.Equal(expected, res[0].I);
            }, iter: 100, threads: 1);
    }
}

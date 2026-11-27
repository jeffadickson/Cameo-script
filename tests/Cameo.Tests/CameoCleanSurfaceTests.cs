using Cameo;
using Cameo.Runtime.Syntax;
using Cameo.Runtime.Vm;
using Xunit;

namespace Cameo.Tests;

/// <summary>Verifies the clean C/Rust/SystemVerilog-coherent Cameo surface, diagnostics, and cameo fmt --migrate.</summary>
public sealed class CameoCleanSurfaceTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Logical_bitwise_power_floordiv_and_precedence_execute_correctly(bool check)
    {
        using var engine = new CameoEngine(new CameoRuntimeOptions { TypeCheck = check });
        Value[] r = engine.RunString("""
            // Logical operators and short-circuiting
            var side = 0
            fn bump() -> boolean { side += 1 return true }
            let sc1 = false && bump()
            let sc2 = true || bump()
            let neg = !false && !nil && !(1 == 2)
            let notIs = !("hi" is integer) && !42 is string

            // Bitwise XOR (^ / ^=), unary NOT (~), and bitwise-over-comparison precedence
            let mask = 0x0F
            var bits = 0xAA
            bits ^= 0xFF
            let precAnd = (0x12 & 0x0F) == 2 && (0x12 & 0x0F == 2)
            let precXor = 0x0A ^ 0x0F == 5
            let bnot = ~0

            // Exponentiation (** / **=): right-associative and tighter than unary minus
            let powRight = 2 ** 3 ** 2      // 2 ** (3 ** 2) == 512
            let negPow = -2 ** 2            // -(2 ** 2) == -4
            let powNeg = 2 ** -3            // 0.125
            var p = 3.0
            p **= 2

            // Floor / integer division (~/ and ~/=)
            let idivPos = 7 ~/ 2            // 3
            let idivNeg = -7 ~/ 2           // -4 (Lua floor division)
            var q = 25
            q ~/= 4                         // 6

            // Null-coalescing right-associativity and precedence below ||
            let c1 = nil ?? nil ?? 42
            let c2 = false ?? 99
            let c3 = (nil || nil) ?? 7
            let c4 = nil || nil ?? 7

            // Interpolated expressions use Cameo lexing too
            let interp = $"{!false && true}:{7 ~/ 2}:{5 ^ 3}"

            return side, sc1, sc2, neg, notIs, bits, precAnd, precXor, bnot,
                   powRight, negPow, powNeg, p, idivPos, idivNeg, q,
                   c1, c2, c3, c4, interp
            """);

        Assert.Equal(0, r[0].I);
        Assert.False(r[1].IsTruthy);
        Assert.True(r[2].IsTruthy);
        Assert.True(r[3].IsTruthy);
        Assert.True(r[4].IsTruthy);
        Assert.Equal(0x55, r[5].I);
        Assert.True(r[6].IsTruthy);
        Assert.True(r[7].IsTruthy);
        Assert.Equal(-1, r[8].I);
        Assert.Equal(512.0, r[9].N, 9);
        Assert.Equal(-4.0, r[10].N, 9);
        Assert.Equal(0.125, r[11].N, 9);
        Assert.Equal(9.0, r[12].N, 9);
        Assert.Equal(3, r[13].I);
        Assert.Equal(-4, r[14].I);
        Assert.Equal(6, r[15].I);
        Assert.Equal(42, r[16].I);
        Assert.False(r[17].IsTruthy);
        Assert.Equal(7, r[18].I);
        Assert.Equal(7, r[19].I);
        Assert.Equal("true:3:6", r[20].AsString);
    }

    [Fact]
    public void Comments_nestable_blocks_and_regex_literals_coexist_unambiguously()
    {
        using var engine = new CameoEngine();
        Value[] r = engine.RunString("""
            // Top-level line comment with /regex/ and /* inside
            /* Outer block comment
               /* Nested block comment */
               still inside outer comment
            */
            let m1 = "abc123" =~ /^[a-z]+\d+$/i
            let m2 = "abc123" !~ /^\d+$/
            let m3 = "abc" !~/^\d+$/ && "abc" =~/^[a-z]+$/
            var ratio = 10.0
            ratio /= 2.0
            let half = 20.0 / 4.0 // trailing line comment after division
            return m1, m2, m3, ratio, half
            """);
        Assert.True(r[0].IsTruthy);
        Assert.True(r[1].IsTruthy);
        Assert.True(r[2].IsTruthy);
        Assert.Equal(5.0, r[3].N, 9);
        Assert.Equal(5.0, r[4].N, 9);
    }

    [Fact]
    public void Triple_quoted_strings_and_nested_list_literals_work_without_spaces()
    {
        using var engine = new CameoEngine();
        Value[] r = engine.RunString(""""
            let matrix = [[1, 2], [3, 4]]
            let cube = [[[10, 20]]]
            let raw = """
            line1 \n literal
            "quoted"
            """
            let who = "Cameo"
            let interp = $"""
            hello {who}! {matrix[1][2] + matrix[2][1]}
            """
            return matrix[2][2], cube[1][1][2], raw, interp
            """");
        Assert.Equal(4, r[0].I);
        Assert.Equal(20, r[1].I);
        Assert.Equal("line1 \\n literal\n\"quoted\"\n", r[2].AsString);
        Assert.Equal("hello Cameo! 5\n", r[3].AsString);
    }

    [Fact]
    public void Native_type_aliases_and_fn_function_types_work_in_cameo()
    {
        using var engine = new CameoEngine();
        Value[] r = engine.RunString("""
            type IntOp = fn(integer, integer) -> integer
            type Pair<T> = {T, T}
            fn apply(op: IntOp, p: Pair<integer>) -> integer {
                return op(p[1], p[2])
            }
            let sum = apply((a, b) => a + b, {19, 23})
            let xor = apply((a, b) => a ^ b, {0x0F, 0x05})
            return sum, xor
            """);
        Assert.Equal(42, r[0].I);
        Assert.Equal(0x0A, r[1].I);
    }

    [Theory]
    [InlineData("-- old comment\nreturn 1", "use '//' for line comments")]
    [InlineData("return not true", "use '!' instead of 'not'")]
    [InlineData("return true and false", "use '&&' instead of 'and'")]
    [InlineData("return true or false", "use '||' instead of 'or'")]
    [InlineData("return 1 ~= 2", "use '!=' instead of '~='")]
    [InlineData("return 5 ~ 3", "use '^' for bitwise XOR")]
    [InlineData("local x = 1", "use 'let' (immutable) or 'var' (mutable) instead of 'local'")]
    [InlineData("if true then return 1 end", "use '{ ... }' instead of 'then ... end'")]
    [InlineData("if false { } elseif true { }", "use 'else if' instead of 'elseif'")]
    [InlineData("while false do end", "use '{ ... }' instead of 'do ... end'")]
    [InlineData("do return 1 end", "use 'do { ... }' instead of 'do ... end'")]
    [InlineData("repeat break until true", "'repeat ... until' is not supported")]
    [InlineData("function f() return 1 end", "use 'fn name(...) { ... }'")]
    [InlineData("let f = function() return 1 end", "use '(...) => ...'")]
    [InlineData("end", "use '}' instead of 'end'")]
    public void Retired_lua_surface_forms_report_actionable_migration_diagnostics(string source, string expectedHint)
    {
        var ex = Assert.Throws<CameoSyntaxException>(() => Parser.ParseCameo(source, "old.cameo"));
        Assert.Contains(expectedHint, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Teal_retains_full_legacy_lua_operators_comments_and_blocks()
    {
        using var teal = new ScriptEngine();
        Value[] r = teal.RunString("""
            -- Teal line comment
            --[[ Teal block comment ]]
            local x = 2 ^ 3
            local y = 7 // 2
            local z = 0x0F ~ 0x05
            local b = (not false) and (1 ~= 2) or false
            local s = [[hello]]
            if b then
                return x, y, z, s
            end
            """);
        Assert.Equal(8.0, r[0].N, 9);
        Assert.Equal(3, r[1].I);
        Assert.Equal(0x0A, r[2].I);
        Assert.Equal("hello", r[3].AsString);
    }

    [Fact]
    public void Cameo_fmt_migrate_rewrites_legacy_syntax_and_preserves_runtime_semantics()
    {
        const string legacy = """
            -- Legacy Cameo script
            --[[ block
               comment ]]
            fn compute(a: integer, b: integer) -> (integer, number, boolean, string) {
                let idiv = a // b
                let xorv = a ~ b
                let bnot = ~0
                let pwr = 2 ^ 3
                var flag = not (a == 0) and (b ~= 0 or a > 0)
                let msg = $[[val={a // b} not untouched]]
                let raw = [[literal and or -- stay]]
                let okRe = "abc123" =~ /^[a-z]+/ && "abc" !~ /^\d+$/
                if not flag {
                    return 0, 0.0, false, raw
                } elseif okRe {
                    return idiv + xorv + bnot, pwr, flag, msg
                } else {
                    return 0, 0.0, false, raw
                }
            }
            return compute(15, 4)
            """;

        string migrated = CameoFormatter.Migrate(legacy, "legacy.cameo");

        // Verify clean Cameo syntax in output
        Assert.Contains("// Legacy Cameo script", migrated);
        Assert.Contains("/* block\n   comment */", migrated);
        Assert.Contains("let idiv = a ~/ b", migrated);
        Assert.Contains("let xorv = a ^ b", migrated);
        Assert.Contains("let bnot = ~0", migrated);
        Assert.Contains("let pwr = 2 ** 3", migrated);
        Assert.Contains("var flag = !(a == 0) && (b != 0 || a > 0)", migrated);
        Assert.Contains("$\"\"\"val={a ~/ b} not untouched\"\"\"", migrated);
        Assert.Contains("\"\"\"literal and or -- stay\"\"\"", migrated);
        Assert.Contains("} else if okRe {", migrated);
        Assert.Contains("if !flag {", migrated);

        // Verify migrated output executes cleanly and is fmt-idempotent
        Assert.Equal(migrated, CameoFormatter.Format(migrated, "legacy.cameo"));
        using var engine = new CameoEngine();
        Value[] r = engine.RunString(migrated, "legacy.cameo");
        Assert.Equal((15 / 4) + (15 ^ 4) + (-1), r[0].I);
        Assert.Equal(8.0, r[1].N, 9);
        Assert.True(r[2].IsTruthy);
        Assert.Equal("val=3 not untouched", r[3].AsString);

        // Also test CLI `cameo fmt --migrate --check` and `cameo fmt --migrate`
        string dir = Path.Combine(Path.GetTempPath(), "cameo-migrate-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "old.cameo");
            File.WriteAllText(file, "-- comment\nlet x = not false and (7 // 2 == 3)\nreturn x\n");

            var checkParsed = CameoCommands.Parse(["fmt", "--migrate", "--check", file]);
            Assert.Null(checkParsed.Error);
            var checkRes = CameoCommands.Execute(checkParsed.Command, TextWriter.Null, TextWriter.Null);
            Assert.Equal(1, checkRes.ExitCode);

            var migParsed = CameoCommands.Parse(["fmt", "--migrate", file]);
            Assert.Null(migParsed.Error);
            var migRes = CameoCommands.Execute(migParsed.Command, TextWriter.Null, TextWriter.Null);
            Assert.Equal(0, migRes.ExitCode);
            string updated = File.ReadAllText(file);
            Assert.Equal("// comment\nlet x = !false && (7 ~/ 2 == 3)\nreturn x\n", updated);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

using System.Text;
using Cameo;
using CsCheck;
using Xunit;

namespace Cameo.Tests;

public class CameoFormatterTests
{
    [Fact]
    public void Formats_Nested_Cameo_Blocks_And_Continuations_Idempotently()
    {
        string messy = """
            
            
              record Item {
            name: string
            note?: string
              fn label() -> string {
            return self.name
              }
              }



            fn run(xs: List<integer>) -> integer {
            if xs.len() > 0 {
            let ys = xs
            .filter((x) => {
            return x > 0
            })
            .map((x) => x * 2)
            return ys.len()
            } else {
            return 0
            }
            }   
            
            """;

        string expected =
            "record Item {\n" +
            "    name: string\n" +
            "    note?: string\n" +
            "    fn label() -> string {\n" +
            "        return self.name\n" +
            "    }\n" +
            "}\n" +
            "\n" +
            "fn run(xs: List<integer>) -> integer {\n" +
            "    if xs.len() > 0 {\n" +
            "        let ys = xs\n" +
            "            .filter((x) => {\n" +
            "                return x > 0\n" +
            "            })\n" +
            "            .map((x) => x * 2)\n" +
            "        return ys.len()\n" +
            "    } else {\n" +
            "        return 0\n" +
            "    }\n" +
            "}\n";

        string formatted = CameoFormatter.Format(messy, "sample.cameo");
        Assert.Equal(expected, formatted);
        Assert.Equal(expected, CameoFormatter.Format(formatted, "sample.cameo"));
    }

    [Fact]
    public void Preserves_Multiline_Strings_Block_Comments_Regexes_And_Shebang()
    {
        string src =
            "#!/usr/bin/env cameo\r\n" +
            "--[[\r\n" +
            "  keep   internal   spacing {\r\n" +
            "    and braces }\r\n" +
            "]]\r\n" +
            "fn demo() -> string {\r\n" +
            "let raw = [[\r\n" +
            "  line one {\r\n" +
            "    line two\r\n" +
            "]]\r\n" +
            "let ok = \"abc{}\" =~ /\\{\\}/i\r\n" +
            "let msg = $\"{ok}:{raw}\"\r\n" +
            "return msg -- keep inline comment   \r\n" +
            "}\r\n";

        string formatted = CameoFormatter.Format(src, "preserve.cameo");
        Assert.StartsWith("#!/usr/bin/env cameo\n", formatted);
        Assert.Contains("  keep   internal   spacing {\n    and braces }\n", formatted);
        Assert.Contains("    let raw = [[\n  line one {\n    line two\n]]\n", formatted);
        Assert.Contains("    let ok = \"abc{}\" =~ /\\{\\}/i\n", formatted);
        Assert.Contains("    return msg -- keep inline comment\n", formatted);
        Assert.Equal(formatted, CameoFormatter.Format(formatted, "preserve.cameo"));
    }

    [Fact]
    public void Syntax_Error_Returns_Structured_Diagnostic_Without_Modifying_File()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cameo-fmt-err-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "broken.cameo");
            string bad = "fn broken( {\n";
            File.WriteAllText(file, bad);

            var parse = CameoCommands.Parse(["fmt", file]);
            Assert.Null(parse.Error);
            var res = CameoCommands.Execute(parse.Command, TextWriter.Null, TextWriter.Null);
            Assert.Equal(1, res.ExitCode);
            Assert.Contains(res.Diagnostics, d => d.Code == "CAMEOSYNTAX");
            Assert.Equal(bad, File.ReadAllText(file));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Cli_Fmt_And_Check_RoundTrip()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cameo-fmt-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "app.cameo");
            File.WriteAllText(file, "fn add(a: integer, b: integer) -> integer {\r\nreturn a + b  \r\n}\r\n");

            var checkBefore = CameoCommands.Parse(["fmt", "--check", file]);
            Assert.Null(checkBefore.Error);
            var resBefore = CameoCommands.Execute(checkBefore.Command, TextWriter.Null, TextWriter.Null);
            Assert.Equal(1, resBefore.ExitCode);
            Assert.Contains("needs formatting:", resBefore.Text);

            var fmtCmd = CameoCommands.Parse(["fmt", file]);
            Assert.Null(fmtCmd.Error);
            var resFmt = CameoCommands.Execute(fmtCmd.Command, TextWriter.Null, TextWriter.Null);
            Assert.Equal(0, resFmt.ExitCode);
            Assert.Equal("fn add(a: integer, b: integer) -> integer {\n    return a + b\n}\n", File.ReadAllText(file));

            var checkAfter = CameoCommands.Parse(["fmt", "--check", file]);
            var resAfter = CameoCommands.Execute(checkAfter.Command, TextWriter.Null, TextWriter.Null);
            Assert.Equal(0, resAfter.ExitCode);
            Assert.Null(resAfter.Text);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Formatting_All_Gallery_Examples_Is_Valid_And_Idempotent()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string cameoDir = Path.Combine(root, "examples");
        string[] files = Directory.GetFiles(cameoDir, "*.cameo");
        Assert.NotEmpty(files);

        foreach (string file in files)
        {
            string src = File.ReadAllText(file);
            string once = CameoFormatter.Format(src, file);
            string twice = CameoFormatter.Format(once, file);
            Assert.Equal(once, twice);
        }
    }

    [Fact]
    public void CsCheck_Formatter_Idempotency_And_Execution_Equivalence()
    {
        var gen =
            from a in Gen.Int[-50, 50]
            from b in Gen.Int[-50, 50]
            from leadSpaces in Gen.Int[0, 6].Array[8]
            from blankRuns in Gen.Int[0, 3].Array[4]
            from useCrlf in Gen.Bool
            select (a, b, leadSpaces, blankRuns, useCrlf);

        gen.Sample(t =>
        {
            string nl = t.useCrlf ? "\r\n" : "\n";
            string Pad(int idx) => new(' ', t.leadSpaces[idx % t.leadSpaces.Length]);
            string Blanks(int idx) => string.Concat(Enumerable.Repeat(nl, t.blankRuns[idx % t.blankRuns.Length]));

            var sb = new StringBuilder();
            sb.Append(Blanks(0));
            sb.Append(Pad(0)).Append("enum Sign { \"Neg\", \"Zero\", \"Pos\" }").Append(nl);
            sb.Append(Blanks(1));
            sb.Append(Pad(1)).Append("fn classify(n: integer) -> Sign {").Append(nl);
            sb.Append(Pad(2)).Append("if n < 0 {").Append(nl);
            sb.Append(Pad(3)).Append("return \"Neg\"").Append(nl);
            sb.Append(Pad(4)).Append("} else if n == 0 {").Append(nl);
            sb.Append(Pad(5)).Append("return \"Zero\"").Append(nl);
            sb.Append(Pad(6)).Append("} else {").Append(nl);
            sb.Append(Pad(7)).Append("return \"Pos\"").Append(nl);
            sb.Append(Pad(0)).Append("}").Append(nl);
            sb.Append(Pad(1)).Append("}").Append(nl);
            sb.Append(Blanks(2));
            sb.Append(Pad(2)).Append($"let xs = [{t.a}, {t.b}]").Append(nl);
            sb.Append(Pad(3)).Append("let labels = [match classify(x) { \"Neg\" => -1, \"Zero\" => 0, \"Pos\" => 1 } for x in xs]").Append(nl);
            sb.Append(Pad(4)).Append("return labels[1] + labels[2]  ").Append(nl);
            sb.Append(Blanks(3));

            string messy = sb.ToString();
            string formatted1 = CameoFormatter.Format(messy, "prop.cameo");
            string formatted2 = CameoFormatter.Format(formatted1, "prop.cameo");

            // Idempotency + canonical LF-only invariants
            Assert.Equal(formatted1, formatted2);
            Assert.DoesNotContain("\r", formatted1);
            Assert.DoesNotContain("\n\n\n", formatted1);
            Assert.EndsWith("\n", formatted1);

            // Runtime equivalence
            using var engine1 = CameoEngine.CreateHermetic();
            using var engine2 = CameoEngine.CreateHermetic();
            var r1 = engine1.RunString(messy, "prop1.cameo");
            var r2 = engine2.RunString(formatted1, "prop2.cameo");
            Assert.Equal(r1[0].I, r2[0].I);
        }, iter: 40, threads: 1);
    }
}

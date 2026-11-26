using System.IO.Enumeration;
using System.Text.RegularExpressions;
using CsCheck;
using Cameo.Runtime.Vm;
using Cameo.Runtime.Stdlib;
using Xunit;

namespace Cameo.Tests.Properties;

/// <summary>CsCheck properties for the scripting tools: glob against
/// .NET's own wildcard matcher, rendering and CSV laws.</summary>
public class ScriptingProperties
{
    private static readonly LuaHost Host = new();
    private static readonly object Gate = new();

    private static Regex Glob(string pattern) =>
        (Regex)typeof(TextToolsLib).GetMethod("GlobToRegex", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, new object[] { pattern })!;

    [Fact]
    public void Glob_agrees_with_dotnet_wildcards_on_single_names()
    {
        // within one path segment, * and ? mean what they mean to .NET's file-name matcher
        var genPattern = Gen.Char["ab.*?"].Array[0, 6].Select(cs => new string(cs));
        var genName = Gen.Char["ab."].Array[0, 6].Select(cs => new string(cs));
        Prop.Cs(Gen.Select(genPattern, genName), t =>
        {
            var (pattern, name) = t;
            if (pattern.Length == 0 || name.Length == 0) return; // .NET's matcher never matches an empty expression or an empty name
            Assert.Equal(FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: false), Glob(pattern).IsMatch(name));
        }, iter: 1000);
    }

    [Fact]
    public void Glob_finds_exactly_the_files_a_reference_matcher_selects()
    {
        string dir = Directory.CreateTempSubdirectory("cameo-globprop").FullName;
        try
        {
            string[] all = { "a.txt", "b.csv", "sub/a.txt", "sub/c.csv", "sub/deep/a.csv", "sub/deep/e.txt", "other/f.csv" };
            foreach (string f in all)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(dir, f))!);
                File.WriteAllText(Path.Combine(dir, f), "");
            }
            string[] dirs = { "sub", "sub/deep", "other" };
            var genPattern = Gen.OneOf(
                Gen.Const("*.txt"), Gen.Const("*.csv"), Gen.Const("**/*.csv"), Gen.Const("**/*.txt"), Gen.Const("sub/*"), Gen.Const("sub/**"),
                Gen.Const("**/a.*"), Gen.Const("*/*.csv"), Gen.Const("sub/*/?.txt"), Gen.Const("**/{a,e}.txt"), Gen.Const("[ab].*"), Gen.Const("**"));
            Prop.Cs(genPattern, pattern =>
            {
                Value[] r;
                lock (Gate) r = Host.Run($"return table.concat(fs.glob('{pattern}', [[{dir}]]), ' ')");
                Regex re = Glob(pattern);
                IEnumerable<string> expected = all.Concat(dirs).Where(p => re.IsMatch(p)).OrderBy(p => p, StringComparer.Ordinal);
                Assert.Equal(string.Join(" ", expected), r[0].AsString);
            }, iter: 60);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Rendered_tables_have_aligned_columns_and_one_line_per_row()
    {
        var genRow = Gen.Select(Gen.Char["ab é"].Array[0, 8].Select(cs => new string(cs)), Gen.Int[-9999, 9999]);
        Prop.Cs(genRow.Array[0, 8], rows =>
        {
            var list = new Table();
            foreach (var (name, n) in rows)
            {
                var row = new Table();
                row.SetStr("name", Value.FromText(name));
                row.SetStr("n", n);
                list.Append(Value.FromTable(row));
            }
            Value[] r;
            lock (Gate) r = Host.L.Call(Host.Run("return function(t) return table.render(t, {'name', 'n'}) end")[0], Value.FromTable(list));
            string[] lines = r[0].ToText().Split('\n', StringSplitOptions.None)[..^1];
            Assert.Equal(rows.Length + 2, lines.Length); // header, rule, one per row
            // the numeric column is right-aligned: every line ends at the same character column
            Assert.Single(lines.Select(l => l.Length).Distinct());
            for (int i = 0; i < rows.Length; i++)
            {
                Assert.EndsWith(rows[i].Item2.ToString(), lines[i + 2]);
                Assert.StartsWith(rows[i].Item1.TrimEnd(), lines[i + 2].TrimEnd());
            }
        }, iter: 200);
    }
}

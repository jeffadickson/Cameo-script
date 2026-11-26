using System.Globalization;
using System.Text;
using CsCheck;
using Cameo.Runtime.Vm;
using Cameo.Runtime.Syntax;
using Xunit;

namespace Cameo.Tests.Properties;

/// <summary>
/// Differential testing of statements (CsCheck): random small typed programs — locals,
/// assignments, if/else, numeric for, while, repeat, break, a table, a function that updates an
/// upvalue, closures capturing values — are run by a reference interpreter written here and
/// by the VM (unchecked and type-checked), and must leave the same final state.
/// Also: the front end never fails on arbitrary input other than with a diagnostic.
/// </summary>
public class ProgramProperties
{
    private readonly LuaHost _unchecked = new(typeCheck: false);
    private readonly LuaHost _checked = new(typeCheck: true);

    private const int NVars = 4;

    // ------------------------------------------------------------------ program model

    private abstract record IExpr;
    private sealed record Lit(long V) : IExpr;
    private sealed record Var(int I) : IExpr;
    private sealed record LoopVar(int Level) : IExpr;
    private sealed record Bin(string Op, IExpr A, IExpr B) : IExpr;
    private sealed record DivMod(string Op, IExpr A, long D) : IExpr;
    private sealed record TGet(IExpr K) : IExpr;
    private sealed record Pick(Cond C, IExpr A, IExpr B) : IExpr;

    private abstract record Cond;
    private sealed record Cmp(string Op, IExpr A, IExpr B) : Cond;
    private sealed record Logic(string Op, Cond A, Cond B) : Cond;
    private sealed record Not(Cond A) : Cond;

    private abstract record Stmt;
    private sealed record Assign(int I, IExpr E) : Stmt;
    private sealed record Swap(int I, int J) : Stmt;
    private sealed record TSet(IExpr K, IExpr? E) : Stmt;
    private sealed record If(Cond C, Stmt[] Then, Stmt[] Else) : Stmt;
    private sealed record For(long From, long To, long Step, Stmt[] Body) : Stmt;
    private sealed record While(int Count, Stmt[] Body) : Stmt;
    private sealed record Repeat(int Count, Stmt[] Body) : Stmt;
    private sealed record BreakIf(Cond C) : Stmt;
    private sealed record CallAdd(IExpr E) : Stmt;
    private sealed record Capture(IExpr E) : Stmt;

    // ------------------------------------------------------------------ generators

    private static Gen<IExpr> GenExpr(int depth, int loops)
    {
        var leaves = new List<(int, IGen<IExpr>)>
        {
            (3, Gen.Long[-9, 9].Select(v => (IExpr)new Lit(v))),
            (1, Gen.Long.Select(v => (IExpr)new Lit(v))),
            (4, Gen.Int[0, NVars - 1].Select(i => (IExpr)new Var(i))),
        };
        if (loops > 0) leaves.Add((3, Gen.Int[1, loops].Select(l => (IExpr)new LoopVar(l))));
        Gen<IExpr> leaf = Gen.Frequency(leaves.ToArray());
        if (depth <= 0) return leaf;
        Gen<IExpr> sub = GenExpr(depth - 1, loops);
        return Gen.Frequency(
            (3, leaf),
            (5, Gen.Select(Gen.OneOf("+", "-", "*", "&", "|", "~"), sub, sub, (op, a, b) => (IExpr)new Bin(op, a, b))),
            (2, Gen.Select(Gen.OneOf("//", "%"), sub, Gen.Long[-7, 7].Where(d => d != 0), (op, a, d) => (IExpr)new DivMod(op, a, d))),
            (2, sub.Select(k => (IExpr)new TGet(k))),
            (1, Gen.Select(GenCond(depth - 1, loops), sub, sub, (c, a, b) => (IExpr)new Pick(c, a, b))));
    }

    private static Gen<Cond> GenCond(int depth, int loops)
    {
        Gen<IExpr> e = GenExpr(Math.Max(depth, 0), loops);
        Gen<Cond> cmp = Gen.Select(Gen.OneOf("==", "~=", "<", "<=", ">", ">="), e, e, (op, a, b) => (Cond)new Cmp(op, a, b));
        if (depth <= 0) return cmp;
        Gen<Cond> sub = GenCond(depth - 1, loops);
        return Gen.Frequency(
            (4, cmp),
            (2, Gen.Select(Gen.OneOf("and", "or"), sub, sub, (op, a, b) => (Cond)new Logic(op, a, b))),
            (1, sub.Select(a => (Cond)new Not(a))));
    }

    private static Gen<Stmt[]> GenBlock(int depth, int loops) => GenStmt(depth, loops).Array[0, 4];

    private static Gen<Stmt> GenStmt(int depth, int loops)
    {
        Gen<IExpr> e = GenExpr(2, loops);
        var simple = new List<(int, IGen<Stmt>)>
        {
            (6, Gen.Select(Gen.Int[0, NVars - 1], e, (i, x) => (Stmt)new Assign(i, x))),
            (1, Gen.Select(Gen.Int[0, NVars - 1], Gen.Int[0, NVars - 1], (i, j) => (Stmt)new Swap(i, j))),
            (3, Gen.Select(e, e, (k, x) => (Stmt)new TSet(k, x))),
            (1, e.Select(k => (Stmt)new TSet(k, null))),
            (2, e.Select(x => (Stmt)new CallAdd(x))),
            (1, e.Select(x => (Stmt)new Capture(x))),
        };
        if (loops > 0) simple.Add((2, GenCond(1, loops).Select(c => (Stmt)new BreakIf(c))));
        Gen<Stmt> leaf = Gen.Frequency(simple.ToArray());
        if (depth <= 0) return leaf;
        return Gen.Frequency(
            (6, leaf),
            (2, Gen.Select(GenCond(2, loops), GenBlock(depth - 1, loops), GenBlock(depth - 1, loops), (c, t, f) => (Stmt)new If(c, t, f))),
            (2, Gen.Select(Gen.Long[-3, 5], Gen.Long[-3, 6], Gen.OneOf(1L, 1L, 2L, 3L, -1L, -2L), GenBlock(depth - 1, loops + 1),
                (a, b, s, body) => (Stmt)new For(a, b, s, body))),
            (1, Gen.Select(Gen.Int[0, 4], GenBlock(depth - 1, loops + 1), (n, body) => (Stmt)new While(n, body))),
            (1, Gen.Select(Gen.Int[1, 3], GenBlock(depth - 1, loops + 1), (n, body) => (Stmt)new Repeat(n, body))));
    }

    private static readonly Gen<(long[] Init, Stmt[] Body)> GenProgram =
        Gen.Select(Gen.Long[-20, 20].Array[NVars], GenStmt(3, 0).Array[1, 8]);

    // ------------------------------------------------------------------ printing as Teal

    private static string P(long v) => v == long.MinValue ? "math.mininteger" : v < 0 ? "(" + v.ToString(CultureInfo.InvariantCulture) + ")" : v.ToString(CultureInfo.InvariantCulture);

    private static string P(IExpr e) => e switch
    {
        Lit l => P(l.V),
        Var v => "v" + v.I,
        LoopVar l => "i" + l.Level,
        Bin b => "(" + P(b.A) + " " + b.Op + " " + P(b.B) + ")",
        DivMod d => "(" + P(d.A) + " " + d.Op + " " + P(d.D) + ")",
        TGet t => "(t[" + P(t.K) + " & 7] or 0)",
        Pick p => "(" + P(p.C) + " and " + P(p.A) + " or " + P(p.B) + ")",
        _ => throw new InvalidOperationException(),
    };

    private static string P(Cond c) => c switch
    {
        Cmp x => "(" + P(x.A) + " " + x.Op + " " + P(x.B) + ")",
        Logic l => "(" + P(l.A) + " " + l.Op + " " + P(l.B) + ")",
        Not n => "(not " + P(n.A) + ")",
        _ => throw new InvalidOperationException(),
    };

    private static void P(StringBuilder sb, Stmt[] block, int loops, string ind)
    {
        foreach (Stmt s in block)
        {
            switch (s)
            {
                case Assign a: sb.Append(ind).Append('v').Append(a.I).Append(" = ").Append(P(a.E)).Append('\n'); break;
                case Swap w: sb.Append(ind).Append($"v{w.I}, v{w.J} = v{w.J}, v{w.I}\n"); break;
                case TSet t: sb.Append(ind).Append("t[").Append(P(t.K)).Append(" & 7] = ").Append(t.E == null ? "nil" : P(t.E)).Append('\n'); break;
                case CallAdd c: sb.Append(ind).Append("add(").Append(P(c.E)).Append(")\n"); break;
                case Capture c:
                    sb.Append(ind).Append("caps[#caps + 1] = (function(): function(): integer local c = ").Append(P(c.E))
                      .Append(" return function(): integer return c + v1 end end)()\n");
                    break;
                case BreakIf b: sb.Append(ind).Append("if ").Append(P(b.C)).Append(" then break end\n"); break;
                case If i:
                    sb.Append(ind).Append("if ").Append(P(i.C)).Append(" then\n");
                    P(sb, i.Then, loops, ind + "  ");
                    if (i.Else.Length > 0) { sb.Append(ind).Append("else\n"); P(sb, i.Else, loops, ind + "  "); }
                    sb.Append(ind).Append("end\n");
                    break;
                case For f:
                    sb.Append(ind).Append($"for i{loops + 1} = {P(f.From)}, {P(f.To)}, {P(f.Step)} do\n");
                    P(sb, f.Body, loops + 1, ind + "  ");
                    sb.Append(ind).Append("end\n");
                    break;
                case While w:
                    sb.Append(ind).Append($"do local i{loops + 1} = {w.Count}\n");
                    sb.Append(ind).Append($"while i{loops + 1} > 0 do\n");
                    sb.Append(ind).Append($"  i{loops + 1} = i{loops + 1} - 1\n");
                    P(sb, w.Body, loops + 1, ind + "  ");
                    sb.Append(ind).Append("end end\n");
                    break;
                case Repeat r:
                    sb.Append(ind).Append($"do local i{loops + 1} = 0\n");
                    sb.Append(ind).Append("repeat\n");
                    sb.Append(ind).Append($"  i{loops + 1} = i{loops + 1} + 1\n");
                    P(sb, r.Body, loops + 1, ind + "  ");
                    sb.Append(ind).Append($"until i{loops + 1} >= {r.Count} end\n");
                    break;
            }
        }
    }

    private static string Source(long[] init, Stmt[] body)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < NVars; i++) sb.Append($"local v{i}: integer = {P(init[i])}\n");
        sb.Append("local t: {integer: integer} = {}\n");
        sb.Append("local caps: {function(): integer} = {}\n");
        sb.Append("local calls = 0\n");
        sb.Append("local function add(x: integer): integer v0 = v0 + x; calls = calls + 1; return v0 end\n");
        P(sb, body, 0, "");
        sb.Append("local capsum = 0\n");
        sb.Append("for _, f in ipairs(caps) do capsum = capsum + f() end\n");
        sb.Append("return v0, v1, v2, v3, calls, capsum");
        for (int k = 0; k < 8; k++) sb.Append($", (t[{k}] or -1)");
        sb.Append('\n');
        return sb.ToString();
    }

    // ------------------------------------------------------------------ reference interpreter

    private sealed class Machine
    {
        public long[] V = new long[NVars];
        public long?[] T = new long?[8];
        public List<long> Caps = new();
        public long Calls;
        public List<long> Loop = new();   // loop variables, innermost last

        public long Eval(IExpr e)
        {
            switch (e)
            {
                case Lit l: return l.V;
                case Var v: return V[v.I];
                case LoopVar l: return Loop[l.Level - 1];
                case Bin b:
                    {
                        long x = Eval(b.A), y = Eval(b.B);
                        return b.Op switch
                        {
                            "+" => unchecked(x + y), "-" => unchecked(x - y), "*" => unchecked(x * y),
                            "&" => x & y, "|" => x | y, _ => x ^ y,
                        };
                    }
                case DivMod d:
                    {
                        long x = Eval(d.A);
                        if (d.D == -1) return d.Op == "//" ? unchecked(-x) : 0;
                        long q = x / d.D, r = x % d.D;
                        if (r != 0 && (r < 0) != (d.D < 0)) { q--; r += d.D; }
                        return d.Op == "//" ? q : r;
                    }
                case TGet t: return T[Eval(t.K) & 7] ?? 0;
                case Pick p: return Test(p.C) ? Eval(p.A) : Eval(p.B);
                default: throw new InvalidOperationException();
            }
        }

        public bool Test(Cond c)
        {
            switch (c)
            {
                case Cmp x:
                    {
                        long a = Eval(x.A), b = Eval(x.B);
                        return x.Op switch { "==" => a == b, "~=" => a != b, "<" => a < b, "<=" => a <= b, ">" => a > b, _ => a >= b };
                    }
                case Logic l: return l.Op == "and" ? Test(l.A) && Test(l.B) : Test(l.A) || Test(l.B);
                case Not n: return !Test(n.A);
                default: throw new InvalidOperationException();
            }
        }

        /// <summary>Runs a block; returns true if a break is propagating.</summary>
        public bool Run(Stmt[] block)
        {
            foreach (Stmt s in block)
            {
                switch (s)
                {
                    case Assign a: V[a.I] = Eval(a.E); break;
                    case Swap w: (V[w.I], V[w.J]) = (V[w.J], V[w.I]); break;
                    case TSet t:
                        {
                            long k = Eval(t.K) & 7;          // key first, then value
                            T[k] = t.E == null ? null : Eval(t.E);
                            break;
                        }
                    case CallAdd c: { long x = Eval(c.E); V[0] = unchecked(V[0] + x); Calls++; break; }
                    case Capture c: Caps.Add(Eval(c.E)); break;
                    case BreakIf b: if (Test(b.C)) return true; break;
                    case If i: if (Run(Test(i.C) ? i.Then : i.Else)) return true; break;
                    case For f:
                        for (long i = f.From; f.Step > 0 ? i <= f.To : i >= f.To; i += f.Step)
                        {
                            Loop.Add(i);
                            bool brk = Run(f.Body);
                            Loop.RemoveAt(Loop.Count - 1);
                            if (brk) break;
                        }
                        break;
                    case While w:
                        {
                            Loop.Add(w.Count);
                            while (Loop[^1] > 0)
                            {
                                Loop[^1]--;
                                if (Run(w.Body)) break;
                            }
                            Loop.RemoveAt(Loop.Count - 1);
                            break;
                        }
                    case Repeat r:
                        {
                            Loop.Add(0);
                            do
                            {
                                Loop[^1]++;
                                if (Run(r.Body)) break;
                            } while (Loop[^1] < r.Count);
                            Loop.RemoveAt(Loop.Count - 1);
                            break;
                        }
                }
            }
            return false;
        }
    }

    private static long[] Expected(long[] init, Stmt[] body)
    {
        var m = new Machine();
        init.CopyTo(m.V, 0);
        m.Run(body);
        var r = new List<long>(m.V) { m.Calls };
        long capsum = 0;
        foreach (long c in m.Caps) capsum = unchecked(capsum + unchecked(c + m.V[1]));
        r.Add(capsum);
        foreach (long? x in m.T) r.Add(x ?? -1);
        return r.ToArray();
    }

    // ------------------------------------------------------------------ properties

    [Fact]
    public void Unchecked_programs_agree_with_the_reference_interpreter()
    {
        Prop.Cs(GenProgram, p =>
        {
            string src = Source(p.Init, p.Body);
            Value[] r = _unchecked.Run(src);
            Assert.True(Expected(p.Init, p.Body).SequenceEqual(r.Select(v => v.I)), src + "\nexpected " + string.Join(",", Expected(p.Init, p.Body)) + "\ngot      " + string.Join(",", r.Select(v => v.ToString())));
            Assert.All(r, v => Assert.True(v.IsInteger));
        }, iter: 1500, print: p => Source(p.Init, p.Body));
    }

    [Fact]
    public void Type_checked_programs_are_accepted_and_agree_with_the_reference_interpreter()
    {
        Prop.Cs(GenProgram, p =>
        {
            string src = Source(p.Init, p.Body);
            Value[] r = _checked.Run(src);
            Assert.True(Expected(p.Init, p.Body).SequenceEqual(r.Select(v => v.I)), src);
        }, iter: 400, print: p => Source(p.Init, p.Body));
    }

    [Fact]
    public void Compiled_programs_are_deterministic_and_reusable()
    {
        // compiling once and running the prototype twice gives the same answer both times
        Prop.Cs(GenProgram, p =>
        {
            Proto proto = _unchecked.Engine.Compile(Source(p.Init, p.Body), "prop");
            long[] a = _unchecked.L.Run(proto).Select(v => v.I).ToArray();
            long[] b = _unchecked.L.Run(proto).Select(v => v.I).ToArray();
            Assert.Equal(a, b);
            Assert.Equal(Expected(p.Init, p.Body), a);
        }, iter: 300);
    }

    // ------------------------------------------------------------------ robustness of the front end

    private static readonly string[] Tokens =
    {
        "local", "function", "end", "if", "then", "else", "elseif", "while", "do", "for", "in", "repeat", "until", "return", "break",
        "goto", "::", "nil", "true", "false", "and", "or", "not", "global", "record", "enum", "interface", "type", "is", "as", "where",
        "macroexp", "x", "y", "f", "T", "self", "1", "2.5", "0x10", "\"s\"", "'c'", "[[long]]", "...", "..", ".", ":", ",", ";", "=", "==",
        "~=", "<", ">", "<=", ">=", "+", "-", "*", "/", "//", "%", "^", "#", "&", "|", "~", "<<", ">>", "(", ")", "{", "}", "[", "]", "?",
        "<const>", "<close>", "integer", "string", "number", "--c\n", "--[[ c ]]", "\n", "$", "`", "!", "\\", "\"", "@", "\0", "é",
    };

    [Fact]
    public void Front_end_reports_diagnostics_and_never_crashes()
    {
        var engine = new ScriptEngine();
        Prop.Cs(Gen.OneOf(Gen.OneOf(Tokens), Gen.String[0, 3]).Array[0, 25], parts =>
        {
            string src = string.Join(" ", parts);
            try
            {
                Parser.Parse(src, "fuzz");
            }
            catch (CameoSyntaxException e)
            {
                Assert.True(e.Diagnostic.Line >= 1 && e.Diagnostic.Col >= 1, src);
                Assert.False(string.IsNullOrEmpty(e.Diagnostic.Message));
            }
            // the full pipeline (parse + check) either accepts or returns diagnostics
            var errors = engine.Check(src, "fuzz");
            foreach (Diagnostic d in errors) Assert.False(string.IsNullOrEmpty(d.Message));
            // and compiling without checking either works or reports a diagnostic
            try
            {
                new ScriptEngine(new CameoRuntimeOptions { TypeCheck = false }).Compile(src, "fuzz");
            }
            catch (CameoCompileException e)
            {
                Assert.NotEmpty(e.Diagnostics);
            }
        }, iter: 3000, print: parts => string.Join(" ", parts));
    }

    [Fact]
    public void Lexer_accepts_every_byte_inside_strings_and_comments()
    {
        Prop.Cs(Gen.String[Gen.Char[(char)0, (char)255], 0, 20], s =>
        {
            // a long string with a level that cannot occur in the content keeps every byte
            string content = s.Replace("\r", "");
            string src = "return [==========[" + content + "]==========]";
            var tokens = Lexer.Tokenize(src, "prop");
            Assert.Equal(TokenKind.String, tokens[1].Kind);
            Assert.Equal(content.StartsWith('\n') ? content.Substring(1) : content, tokens[1].Text);
            // the same bytes in a comment disappear
            var none = Lexer.Tokenize("--[==========[" + content + "]==========] x", "prop");
            Assert.Equal("x", none[0].Text);
            // decimal escapes reproduce any byte string
            string escaped = "\"" + string.Concat(s.Select(c => "\\" + ((int)c).ToString("000"))) + "\"";
            Assert.Equal(s, Lexer.Tokenize(escaped, "prop")[0].Text);
            string hex = "'" + string.Concat(s.Select(c => "\\x" + ((int)c).ToString("x2"))) + "'";
            Assert.Equal(s, Lexer.Tokenize(hex, "prop")[0].Text);
        }, iter: 1000);
    }
}

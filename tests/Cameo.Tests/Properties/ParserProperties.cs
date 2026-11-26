using System.Globalization;
using Hedgehog;
using Hedgehog.Linq;
using Cameo.Runtime.Syntax;
using Xunit;
using Gen = Hedgehog.Linq.Gen;
using Range = Hedgehog.Linq.Range;

namespace Cameo.Tests.Properties;

/// <summary>
/// Parser round-trips (Hedgehog): random expression trees are printed with the minimum of
/// parentheses that operator precedence and associativity require, parsed back, and must
/// produce the same tree.
/// </summary>
public class ParserProperties
{
    // ------------------------------------------------------------------ a tiny expression model

    private abstract record E;
    private sealed record Num(long V) : E;
    private sealed record Flt(double V) : E;
    private sealed record Name(string N) : E;
    private sealed record Str(string S) : E;
    private sealed record Un(UnaryOp Op, E A) : E;
    private sealed record Bin(BinaryOp Op, E L, E R) : E;
    private sealed record Call(E F, List<E> Args) : E
    {
        public bool Equals(Call? o) => o != null && F.Equals(o.F) && Args.SequenceEqual(o.Args);
        public override int GetHashCode() => F.GetHashCode();
    }
    private sealed record Index(E O, E K) : E;

    // precedence table of the language (documentation, not copied from the parser's code):
    // or < and < comparison < | < ~ < & < shift < .. < +- < */ // % < unary < ^
    private static (int Prec, bool RightAssoc, string Text) Info(BinaryOp op) => op switch
    {
        BinaryOp.Or => (1, false, "or"),
        BinaryOp.And => (2, false, "and"),
        BinaryOp.Lt => (3, false, "<"), BinaryOp.Gt => (3, false, ">"), BinaryOp.Le => (3, false, "<="),
        BinaryOp.Ge => (3, false, ">="), BinaryOp.Ne => (3, false, "~="), BinaryOp.Eq => (3, false, "=="),
        BinaryOp.BOr => (4, false, "|"),
        BinaryOp.BXor => (5, false, "~"),
        BinaryOp.BAnd => (6, false, "&"),
        BinaryOp.Shl => (7, false, "<<"), BinaryOp.Shr => (7, false, ">>"),
        BinaryOp.Concat => (8, true, ".."),
        BinaryOp.Add => (9, false, "+"), BinaryOp.Sub => (9, false, "-"),
        BinaryOp.Mul => (10, false, "*"), BinaryOp.Div => (10, false, "/"), BinaryOp.IDiv => (10, false, "//"), BinaryOp.Mod => (10, false, "%"),
        BinaryOp.Pow => (12, true, "^"),
        _ => throw new InvalidOperationException(),
    };

    private const int UnaryPrec = 11;

    private static int PrecOf(E e) => e switch { Bin b => Info(b.Op).Prec, Un => UnaryPrec, _ => 100 };

    private static string UnText(UnaryOp op) => op switch { UnaryOp.Neg => "-", UnaryOp.Not => "not ", UnaryOp.Len => "#", _ => "~" };

    /// <summary>Prints with only the parentheses that are needed.</summary>
    private static string Print(E e)
    {
        switch (e)
        {
            case Num n: return n.V.ToString(CultureInfo.InvariantCulture);
            case Flt f:
                {
                    string s = f.V.ToString("R", CultureInfo.InvariantCulture);
                    return s.Contains('.') || s.Contains('E') ? s : s + ".0";
                }
            case Name n: return n.N;
            case Str s: return "\"" + s.S + "\"";
            case Un u:
                {
                    // the operand of a unary operator binds tighter than every binary operator but ^
                    string a = Print(u.A);
                    if (u.A is Bin b && Info(b.Op).Prec < UnaryPrec) a = "(" + a + ")";
                    string op = UnText(u.Op);
                    // "- -x" must not become a comment, "~ ~x"/"~="-style confusion is avoided the same way
                    return op + (a.StartsWith('-') || a.StartsWith('~') ? " " : "") + a;
                }
            case Bin b:
                {
                    var (prec, right, text) = Info(b.Op);
                    string l = Print(b.L), r = Print(b.R);
                    int lp = PrecOf(b.L), rp = PrecOf(b.R);
                    if (lp < prec || (lp == prec && right) || (b.L is Un && prec > UnaryPrec)) l = "(" + l + ")";
                    // a unary operator on the right needs no parentheses: 2 ^ -x, a + -b
                    if (b.R is not Un && (rp < prec || (rp == prec && !right))) r = "(" + r + ")";
                    return l + " " + text + " " + r;
                }
            case Call c:
                {
                    string f = Print(c.F);
                    if (c.F is not (Name or Call or Index)) f = "(" + f + ")";
                    return f + "(" + string.Join(", ", c.Args.Select(Print)) + ")";
                }
            case Index i:
                {
                    string o = Print(i.O);
                    if (i.O is not (Name or Call or Index)) o = "(" + o + ")";
                    return i.K is Str s && s.S.Length > 0 && s.S.All(char.IsAsciiLetter) ? o + "." + s.S : o + "[" + Print(i.K) + "]";
                }
            default: throw new InvalidOperationException();
        }
    }

    /// <summary>What the parser is expected to build (it folds a minus sign into numeric literals).</summary>
    private static E Normalize(E e) => e switch
    {
        // folding applies bottom-up, so `- -5` becomes the literal 5
        Un { Op: UnaryOp.Neg } u when Normalize(u.A) is Num n => new Num(-n.V),
        Un { Op: UnaryOp.Neg } u when Normalize(u.A) is Flt f => new Flt(-f.V),
        Un u => new Un(u.Op, Normalize(u.A)),
        Bin b => new Bin(b.Op, Normalize(b.L), Normalize(b.R)),
        Call c => new Call(Normalize(c.F), c.Args.Select(Normalize).ToList()),
        Index i => new Index(Normalize(i.O), Normalize(i.K)),
        _ => e,
    };

    private static E FromAst(Expr e) => e switch
    {
        NumberExpr n => n.IsInteger ? new Num(n.IntValue) : new Flt(n.FloatValue),
        NameExpr n => new Name(n.Name),
        StringExpr s => new Str(s.Value),
        ParenExpr p => FromAst(p.Inner),
        UnaryExpr u => new Un(u.Op, FromAst(u.Operand)),
        BinaryExpr b => new Bin(b.Op, FromAst(b.Left), FromAst(b.Right)),
        CallExpr c => new Call(FromAst(c.Callee), c.Args.Select(FromAst).ToList()),
        IndexExpr i => new Index(FromAst(i.Object), FromAst(i.Key)),
        _ => throw new InvalidOperationException("unexpected node " + e.GetType().Name),
    };

    // ------------------------------------------------------------------ generators

    private static readonly BinaryOp[] AllBinary = Enum.GetValues<BinaryOp>().Where(op => op != BinaryOp.Coalesce).ToArray();
    private static readonly UnaryOp[] AllUnary = Enum.GetValues<UnaryOp>();

    private static readonly Gen<E> GenLeaf = Gen.Frequency(
        (3, Gen.Int64(Range.LinearInt64(0, 1000)).Select(v => (E)new Num(v))),
        (3, Gen.Item("a", "b", "c", "x1", "_y").Select(n => (E)new Name(n))),
        (1, Gen.Item(0.5, 2.25, 100.0, 1e20).Select(v => (E)new Flt(v))),
        (1, Gen.Item("s", "key", "", "two words").Select(s => (E)new Str(s))));

    private static Gen<E> GenExpr(int depth) => depth <= 0
        ? GenLeaf
        : Gen.Frequency(
            (2, GenLeaf),
            (8, from op in Gen.Item(AllBinary) from l in GenExpr(depth - 1) from r in GenExpr(depth - 1) select (E)new Bin(op, l, r)),
            (3, from op in Gen.Item(AllUnary) from a in GenExpr(depth - 1) select (E)new Un(op, a)),
            (1, from f in GenExpr(depth - 1) from args in GenExpr(depth - 1).List(Range.LinearInt32(0, 3)) select (E)new Call(f, args)),
            (1, from o in GenExpr(depth - 1) from k in GenExpr(depth - 1) select (E)new Index(o, k)));

    private static readonly Gen<E> GenTree = from d in Gen.Int32(Range.LinearInt32(0, 5)) from e in GenExpr(d) select e;

    // ------------------------------------------------------------------ properties

    [Fact]
    public void Printing_with_minimal_parentheses_and_parsing_gives_the_same_tree()
    {
        Prop.Hh(GenTree, tree =>
        {
            string src = Print(tree);
            E parsed = FromAst(Parser.ParseExpression(src, "prop"));
            Assert.True(Normalize(tree).Equals(parsed), $"source: {src}\n  expected {Normalize(tree)}\n  parsed   {parsed}");
        }, 3000);
    }

    [Fact]
    public void Fully_parenthesized_and_minimal_forms_parse_alike()
    {
        static string Full(E e) => e switch
        {
            Un u => "(" + UnText(u.Op) + " " + Full(u.A) + ")",
            Bin b => "(" + Full(b.L) + " " + Info(b.Op).Text + " " + Full(b.R) + ")",
            Call c => "(" + Full(c.F) + ")(" + string.Join(", ", c.Args.Select(Full)) + ")",
            Index i => "(" + Full(i.O) + ")[" + Full(i.K) + "]",
            _ => Print(e),
        };
        Prop.Hh(GenTree, tree =>
        {
            E a = FromAst(Parser.ParseExpression(Full(tree), "prop"));
            E b = FromAst(Parser.ParseExpression(Print(tree), "prop"));
            // Parentheses can defer numeric-negation folding. FromAst removes those
            // parentheses, so compare the model's declared normal forms on both sides.
            Assert.True(Normalize(a).Equals(Normalize(b)), $"{Full(tree)}\n  vs {Print(tree)}");
            // the same expression inside a statement parses to the same tree
            Chunk chunk = Parser.Parse("local v = " + Print(tree) + "\nreturn v", "prop");
            var local = Assert.IsType<LocalStat>(chunk.Body.Stats[0]);
            Assert.True(Normalize(b).Equals(Normalize(FromAst(local.Values[0]))));
        }, 1000);
    }

    [Theory]
    [InlineData("((0)((- (- 0))) + 0)", "(0)(- -0) + 0")]
    [InlineData("(- (5))", "-5")]
    public void Numeric_negation_is_compared_in_its_declared_normal_form(string full, string minimal)
    {
        E left = FromAst(Parser.ParseExpression(full, "regression"));
        E right = FromAst(Parser.ParseExpression(minimal, "regression"));
        Assert.Equal(Normalize(left), Normalize(right));
    }

    [Fact]
    public void Positions_point_into_the_source()
    {
        Prop.Hh(GenTree, tree =>
        {
            string src = "\n\n  " + Print(tree);
            Expr e = Parser.ParseExpression(src, "prop");
            void Walk(Expr x)
            {
                Assert.Equal(3, x.Line);
                Assert.InRange(x.Col, 3, src.Length);
                switch (x)
                {
                    case BinaryExpr b: Walk(b.Left); Walk(b.Right); break;
                    case UnaryExpr u: Walk(u.Operand); break;
                    case ParenExpr p: Walk(p.Inner); break;
                    case CallExpr c: Walk(c.Callee); c.Args.ForEach(Walk); break;
                    case IndexExpr i: Walk(i.Object); Walk(i.Key); break;
                }
            }
            Walk(e);
        }, 300);
    }
}

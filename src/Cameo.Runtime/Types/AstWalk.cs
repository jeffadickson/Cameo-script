using Cameo.Runtime.Syntax;

namespace Cameo.Runtime.Types;

/// <summary>Small syntax-tree traversals used by the type checker's flow and scope rules.</summary>
internal static class AstWalk
{
    /// <summary>Direct sub-expressions of an expression (not descending into function bodies).</summary>
    public static IEnumerable<Expr> Children(Expr e)
    {
        switch (e)
        {
            case IndexExpr i: yield return i.Object; yield return i.Key; break;
            case CallExpr c:
                yield return c.Callee;
                foreach (Expr a in c.Args) yield return a;
                break;
            case MethodCallExpr m:
                yield return m.Object;
                foreach (Expr a in m.Args) yield return a;
                break;
            case TableExpr t:
                foreach (TableItem it in t.Items)
                {
                    if (it.Key != null && it.Kind == TableItemKind.Keyed) yield return it.Key;
                    yield return it.Value;
                }
                break;
            case CameoListExpr l:
                foreach (Expr item in l.Items) yield return item;
                break;
            case CameoListCompExpr c:
                yield return c.Iterable;
                if (c.Filter != null) yield return c.Filter;
                yield return c.Body;
                break;
            case CameoMatchExpr m:
                yield return m.Scrutinee;
                foreach (MatchArm arm in m.Arms)
                {
                    foreach (MatchPattern p in arm.Patterns)
                        if (p.Literal != null) yield return p.Literal;
                    if (arm.ExprBody != null) yield return arm.ExprBody;
                }
                break;
            case BinaryExpr b: yield return b.Left; yield return b.Right; break;
            case UnaryExpr u: yield return u.Operand; break;
            case ParenExpr p: yield return p.Inner; break;
            case CastExpr c: yield return c.Inner; break;
            case IsExpr i: yield return i.Inner; break;
        }
    }

    /// <summary>All expressions in a tree, pre-order (not descending into function bodies).</summary>
    public static IEnumerable<Expr> All(Expr e)
    {
        yield return e;
        foreach (Expr c in Children(e))
            foreach (Expr x in All(c)) yield return x;
    }

    /// <summary>Names assigned to (with `=`) anywhere inside a block, including nested blocks and functions.</summary>
    public static void AssignedNames(Block b, HashSet<string> into)
    {
        foreach (Stat s in b.Stats) AssignedNames(s, into);
    }

    private static void AssignedNames(Stat s, HashSet<string> into)
    {
        switch (s)
        {
            case AssignStat a:
                foreach (Expr t in a.Targets) if (t is NameExpr n) into.Add(n.Name);
                foreach (Expr v in a.Values) AssignedInExpr(v, into);
                break;
            case LocalStat l: foreach (Expr v in l.Values) AssignedInExpr(v, into); break;
            case GlobalStat g: foreach (Expr v in g.Values) AssignedInExpr(v, into); break;
            case CallStat c: AssignedInExpr(c.Call, into); break;
            case MatchStat ms:
                AssignedInExpr(ms.Match.Scrutinee, into);
                foreach (MatchArm arm in ms.Match.Arms)
                {
                    if (arm.BlockBody != null) AssignedNames(arm.BlockBody, into);
                    if (arm.ExprBody != null) AssignedInExpr(arm.ExprBody, into);
                }
                break;
            case ReturnStat r: foreach (Expr v in r.Values) AssignedInExpr(v, into); break;
            case DoStat d: AssignedNames(d.Body, into); break;
            case WhileStat w: AssignedNames(w.Body, into); break;
            case RepeatStat r: AssignedNames(r.Body, into); break;
            case IfStat i:
                foreach (var (_, body) in i.Clauses) AssignedNames(body, into);
                if (i.Else != null) AssignedNames(i.Else, into);
                break;
            case ForNumStat f: AssignedNames(f.Body, into); break;
            case ForInStat f: AssignedNames(f.Body, into); break;
            case FunctionStat f: AssignedNames(f.Body.Body, into); break;
            case LocalFunctionStat f: AssignedNames(f.Body.Body, into); break;
            case GlobalFunctionStat f: AssignedNames(f.Body.Body, into); break;
        }
    }

    private static void AssignedInExpr(Expr e, HashSet<string> into)
    {
        foreach (Expr x in All(e))
            if (x is FunctionExpr f) AssignedNames(f.Body.Body, into);
    }

    /// <summary>Gotos inside a statement (nested blocks included, nested functions excluded) that
    /// can reach a label of the enclosing block.</summary>
    public static IEnumerable<GotoStat> Gotos(Stat s, string label)
    {
        switch (s)
        {
            case GotoStat g when g.Label == label: yield return g; break;
            case DoStat d: foreach (var g in Gotos(d.Body, label)) yield return g; break;
            case WhileStat w: foreach (var g in Gotos(w.Body, label)) yield return g; break;
            case RepeatStat r: foreach (var g in Gotos(r.Body, label)) yield return g; break;
            case IfStat i:
                foreach (var (_, body) in i.Clauses) foreach (var g in Gotos(body, label)) yield return g;
                if (i.Else != null) foreach (var g in Gotos(i.Else, label)) yield return g;
                break;
            case ForNumStat f: foreach (var g in Gotos(f.Body, label)) yield return g; break;
            case ForInStat f: foreach (var g in Gotos(f.Body, label)) yield return g; break;
        }
    }

    private static IEnumerable<GotoStat> Gotos(Block b, string label)
    {
        // a nested block that defines the same label captures its gotos
        foreach (Stat s in b.Stats) if (s is LabelStat l && l.Name == label) yield break;
        foreach (Stat s in b.Stats)
            foreach (var g in Gotos(s, label)) yield return g;
    }
}

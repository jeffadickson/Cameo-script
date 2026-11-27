using Cameo.Runtime.Vm;

namespace Cameo.Runtime.Syntax;

public abstract class Node
{
    public int Line, Col;
}

// =====================================================================
// Type syntax
// =====================================================================

public closed class TypeNode : Node { }

/// <summary>A (possibly dotted, possibly generic) type name: <c>string</c>, <c>Foo.Bar&lt;T&gt;</c>.</summary>
public sealed class NameTypeNode : TypeNode
{
    public List<string> Names = new();
    public List<TypeNode>? TypeArgs;
    public string FullName => string.Join(".", Names);
}

public sealed class ArrayTypeNode : TypeNode { public TypeNode Element = null!; }
public sealed class TupleTypeNode : TypeNode { public List<TypeNode> Elements = new(); }
public sealed class MapTypeNode : TypeNode { public TypeNode Key = null!, Value = null!; }
public sealed class UnionTypeNode : TypeNode { public List<TypeNode> Members = new(); }

public sealed class TypeParam : Node
{
    public string Name = "";
    /// <summary><c>&lt;T is Constraint&gt;</c></summary>
    public TypeNode? Constraint;
}

public sealed class ParamTypeNode
{
    public string? Name;
    public TypeNode Type = null!;
    public bool Optional;
}

public sealed class FunctionTypeNode : TypeNode
{
    public List<TypeParam>? TypeParams;
    public List<ParamTypeNode> Params = new();
    /// <summary>Type of the trailing vararg (<c>...: T</c> or <c>T...</c>), if any.</summary>
    public TypeNode? VarArg;
    public List<TypeNode> Returns = new();
    /// <summary>The last return type is variadic (<c>T...</c>).</summary>
    public bool ReturnsVarArg;
    /// <summary>Bare <c>function</c> with no signature.</summary>
    public bool IsBare;
}

public sealed class RecordField : Node
{
    public string Name = "";
    public TypeNode Type = null!;
    public bool IsMetamethod;
    /// <summary>0: public; 1: protected (the class and its subclasses); 2: private (the class only).</summary>
    public int Visibility;
    /// <summary>Set for <c>name: functiontype = macroexp(...) return ... end</c> fields.</summary>
    public MacroExp? Macro;
    /// <summary>Cameo optional record field (<c>name?: T</c>): nullable and may be omitted in literals.</summary>
    public bool IsOptional;
}

public sealed class NestedTypeDecl : Node
{
    public string Name = "";
    /// <summary>RecordTypeNode, EnumTypeNode, or any other type (alias).</summary>
    public TypeNode Def = null!;
}

/// <summary>Body of a <c>record</c> or <c>interface</c> declaration.</summary>
public sealed class RecordTypeNode : TypeNode
{
    public bool IsInterface;
    public bool IsUserdata;
    /// <summary>True for Cameo braced <c>record Name { ... }</c> declarations, enabling required-field checking.</summary>
    public bool IsCameoRecord;
    public List<TypeParam>? TypeParams;
    /// <summary>Element type when the record is also an array (<c>is {T}</c> or a <c>{T}</c> entry).</summary>
    public TypeNode? ArrayElement;
    public List<TypeNode> Interfaces = new();
    /// <summary><c>where</c> clause (uses <c>self</c>).</summary>
    public Expr? Where;
    /// <summary>Source text of the where clause, so that it can be re-parsed for each use site.</summary>
    public string? WhereSource;
    public List<RecordField> Fields = new();
    public List<NestedTypeDecl> NestedTypes = new();
    /// <summary>For a record that a <c>class</c> declaration became: where that declaration starts
    /// and ends in the source (line, column), which is where its private members may be used.</summary>
    public (int StartLine, int StartCol, int EndLine, int EndCol)? ClassRange;
    /// <summary>For a class: the names of the members it defines itself (functions, properties, fields).</summary>
    public HashSet<string>? ClassDefines;
    /// <summary>For a class: the functions it declares <c>abstract</c> (no body).</summary>
    public HashSet<string>? ClassAbstract;
    /// <summary>For a class: properties with a getter and no setter.</summary>
    public HashSet<string>? ClassReadOnly;
    /// <summary>Cameo <c>record ... implements ...</c>: require every field and method of listed interfaces.</summary>
    public bool CheckCameoImplements;
    /// <summary>Cameo record with inline methods: hidden local holding the instance metatable.</summary>
    public string? CameoMetatableVar;
    /// <summary>Cameo record with inline methods: unique runtime id registered via <c>__cameo_record</c>.</summary>
    public string? CameoRecordId;
    /// <summary>Inline <c>fn</c> / <c>static fn</c> method bodies parsed inside a Cameo braced record.</summary>
    public List<FunctionStat>? CameoMethodStats;
}

public sealed class EnumTypeNode : TypeNode { public List<string> Values = new(); }

/// <summary><c>require("module").A.B</c> used as a type.</summary>
public sealed class RequireTypeNode : TypeNode
{
    public string Module = "";
    public List<string> Path = new();
}

public sealed class MacroExp : Node
{
    public FunctionBody Body = null!;
    /// <summary>Source text of the macro as a function expression, so that a fresh copy of its
    /// body can be parsed for every expansion (see <c>Parser.ParseMacroBody</c>).</summary>
    public string Source = "";
}

/// <summary>
/// Set by the type checker on a call or operator expression that resolves to a <c>macroexp</c>:
/// the expression is compiled as the macro's return expression with the parameters bound to
/// the arguments (each evaluated exactly once, in order).
/// </summary>
public sealed class MacroExpansion
{
    public List<string> ParamNames = new();
    /// <summary>Argument expressions (original child nodes of the expanded expression).</summary>
    public List<Expr> Args = new();
    /// <summary>A fresh copy of the macro's return expression.</summary>
    public Expr Body = null!;
    /// <summary>Negate the result (<c>~=</c> through an <c>__eq</c> macro).</summary>
    public bool Negate;
    /// <summary>Set by the resolver: one hidden local per parameter.</summary>
    public List<object> ParamSymbols = new();
}

// =====================================================================
// Runtime type tests produced by the checker for `is` expressions
// =====================================================================

public closed class TypeTest { }
/// <summary>Test on the primitive runtime kind (type()/math.type()).</summary>
public sealed class KindTest : TypeTest
{
    public TypeKindTest Kind;
    public KindTest(TypeKindTest kind) { Kind = kind; }
}
/// <summary>True if any option matches (union types).</summary>
public sealed class AnyOfTest : TypeTest { public List<TypeTest> Options = new(); }
/// <summary>A record/interface <c>where</c> clause. The expression refers to the tested value as <c>self</c>;
/// it must be a fresh AST (see <c>Parser.ParseExpression</c>), not shared with other nodes.</summary>
public sealed class WhereTest : TypeTest
{
    public Expr Condition = null!;
    /// <summary>Name the condition uses for the tested value (<c>self</c> for where clauses).</summary>
    public string SelfName = "self";
    /// <summary>Set by the resolver: the hidden local bound to the tested value.</summary>
    public object? SelfSymbol;
}

// =====================================================================
// Expressions
// =====================================================================

public closed class Expr : Node
{
    /// <summary>Slot for the type checker: the inferred type of this expression.</summary>
    public object? TypeInfo;
    /// <summary>Set by the type checker when this expression is a macroexp use.</summary>
    public MacroExpansion? Macro;
}

public sealed class NilExpr : Expr { }
public sealed class BoolExpr : Expr { public bool Value; }
public sealed class NumberExpr : Expr
{
    public bool IsInteger;
    public long IntValue;
    public double FloatValue;
}
public sealed class StringExpr : Expr { public string Value = ""; }
public sealed class VarargExpr : Expr { }

public sealed class NameExpr : Expr
{
    public string Name = "";
    /// <summary>Set by the resolver: the local variable this name refers to, or null for a global.</summary>
    public object? Symbol;
}

/// <summary><c>obj[key]</c> or <c>obj.name</c> (then Key is a StringExpr and IsDot is true).</summary>
public sealed class IndexExpr : Expr
{
    public Expr Object = null!;
    public Expr Key = null!;
    public bool IsDot;
    /// <summary>Cameo optional chaining (<c>obj?.name</c> or <c>obj?.[key]</c>): evaluates to nil when Object is nil.</summary>
    public bool IsOptional;
}

/// <summary>How a direct Cameo dot call (<c>obj.name(...)</c>) binds <c>self</c>.</summary>
public enum CameoDotDispatch : byte
{
    /// <summary>Unchecked or unknown receiver type: decide dynamically with <c>PrepareDotCall</c>.</summary>
    Dynamic = 0,
    /// <summary>Checked static/module/table function call: emit a normal call with no receiver binding.</summary>
    Function = 1,
    /// <summary>Checked built-in instance method call: emit <c>Self</c> with fixed arity and no <c>PrepareDotCall</c>.</summary>
    Method = 2,
}

public sealed class CallExpr : Expr
{
    /// <summary>Source-written direct Cameo dot call; lookup remains unbound outside this syntax.</summary>
    public bool CameoDotCall;
    /// <summary>Cameo optional call (<c>fn?.(args)</c> or <c>obj?.m(args)</c>): evaluates to nil when target is nil.</summary>
    public bool IsOptional;
    /// <summary>Resolved dispatch selected by the type checker for <see cref="CameoDotCall"/>.</summary>
    public CameoDotDispatch DotDispatch;
    public Expr Callee = null!;
    public List<Expr> Args = new();
}

/// <summary><c>obj:name(args)</c></summary>
public sealed class MethodCallExpr : Expr
{
    public Expr Object = null!;
    public string Name = "";
    public List<Expr> Args = new();
}

public sealed class FunctionExpr : Expr
{
    public FunctionBody Body = null!;
}

public enum TableItemKind { Positional, Named, Keyed }

public sealed class TableItem : Node
{
    public TableItemKind Kind;
    /// <summary>Key expression: null for Positional, a StringExpr for Named.</summary>
    public Expr? Key;
    public Expr Value = null!;
    /// <summary>Declared type of a named field (<c>{ x: number = 1 }</c>).</summary>
    public TypeNode? DeclaredType;
    /// <summary>Set by the type checker when a plain function value fills a Cameo interface method slot.</summary>
    public bool WrapDropSelf;
}

public sealed class TableExpr : Expr
{
    public List<TableItem> Items = new();
    /// <summary>Set by the type checker when this table literal constructs a Cameo record with inline methods.</summary>
    public string? CameoMetatableVar;
    public string? CameoRecordId;
    public object? CameoMetatableSym;
}

/// <summary>Cameo list literal: <c>[e1, e2, ...]</c> (creates a C#-backed <c>List&lt;T&gt;</c>).</summary>
public sealed class CameoListExpr : Expr
{
    public List<Expr> Items = new();
}

/// <summary>Cameo list comprehension: <c>[body for v1[, v2] in iterable [if filter]]</c>.</summary>
public sealed class CameoListCompExpr : Expr
{
    public Expr Body = null!;
    public List<AttribName> Vars = new();
    public Expr Iterable = null!;
    public Expr? Filter;
    /// <summary>Set by the type checker: 1 for <c>__cameo_iter1</c>, 2 for <c>__cameo_iter2</c>, 0 if unchecked.</summary>
    public int IterCount;
}

/// <summary>Single pattern inside a Cameo <c>match</c> arm (<c>_</c>, literal, or <c>is Type[(binding)]</c>).</summary>
public sealed class MatchPattern : Node
{
    public bool IsWildcard;
    public TypeNode? TypeTest;
    public AttribName? Binding;
    public TypeTest? CompiledTest;
    public Expr? Literal;
}

/// <summary>Single arm inside a Cameo <c>match</c> expression or statement.</summary>
public sealed class MatchArm : Node
{
    public List<MatchPattern> Patterns = new();
    public Expr? ExprBody;
    public Block? BlockBody;
}

/// <summary>Cameo <c>match scrutinee { pattern => body, ... }</c>.</summary>
public sealed class CameoMatchExpr : Expr
{
    public Expr Scrutinee = null!;
    public List<MatchArm> Arms = new();
    public bool IsStatement;
    /// <summary>Set by the resolver: hidden local storing the evaluated scrutinee.</summary>
    public object? ScrutineeSymbol;
}

public enum BinaryOp
{
    Add, Sub, Mul, Div, IDiv, Mod, Pow, Concat,
    Eq, Ne, Lt, Le, Gt, Ge,
    And, Or, Coalesce,
    BAnd, BOr, BXor, Shl, Shr,
}

public sealed class BinaryExpr : Expr
{
    public BinaryOp Op;
    public Expr Left = null!, Right = null!;
}

public enum UnaryOp { Neg, Not, Len, BNot }

public sealed class UnaryExpr : Expr
{
    public UnaryOp Op;
    public Expr Operand = null!;
}

/// <summary>Parenthesized expression (truncates multiple values to one).</summary>
public sealed class ParenExpr : Expr { public Expr Inner = null!; }

/// <summary><c>expr as T</c> or <c>expr as (T1, T2)</c>. No runtime effect.</summary>
public sealed class CastExpr : Expr
{
    public Expr Inner = null!;
    public List<TypeNode> Types = new();
    public bool IsTuple;
}

/// <summary><c>expr is T</c></summary>
public sealed class IsExpr : Expr
{
    public Expr Inner = null!;
    public TypeNode Type = null!;
    /// <summary>Set by the type checker: how to test the value at run time.</summary>
    public TypeTest? Test;
}

// =====================================================================
// Functions
// =====================================================================

public sealed class Param : Node
{
    public string Name = "";
    public TypeNode? Type;
    public bool Optional;
    /// <summary>The implicit <c>self</c> of <c>function R:method()</c>.</summary>
    public bool IsImplicitSelf;
    public object? Symbol;
    /// <summary>Resolved by the checker for speculative IL entry specialization.
    /// This is a hint only: the IL target must guard the actual argument kind.</summary>
    public ValueKind? NumericHint;
}

public sealed class FunctionBody : Node
{
    public string Name = "anonymous";
    public List<TypeParam>? TypeParams;
    public List<Param> Params = new();
    public bool IsVarArg;
    public TypeNode? VarArgType;
    public List<TypeNode> Returns = new();
    public bool ReturnsVarArg;
    /// <summary>False when no return type annotation was written.</summary>
    public bool HasReturnAnnotation;
    /// <summary>Cameo-only expression function: infer one result after checking source arity.</summary>
    public bool IsExpressionFunction;
    /// <summary>True for inline instance methods declared inside a Cameo braced record.</summary>
    public bool IsCameoMethod;
    /// <summary>Set by the type checker when a function literal fills a Cameo interface method slot without declaring self.</summary>
    public bool PrependIgnoredSelf;
    public Block Body = null!;
    public int EndLine;
    /// <summary>Slot for the resolver (captured variables etc.).</summary>
    public object? ScopeInfo;
}

// =====================================================================
// Statements
// =====================================================================

public closed class Stat : Node { }

public sealed class Block : Node
{
    public List<Stat> Stats = new();
}

public sealed class AttribName : Node
{
    public string Name = "";
    /// <summary>"const", "close", "total" or null.</summary>
    public string? Attrib;
    /// <summary>Initialized Cameo binding, immutable even when type checking is disabled.</summary>
    public bool IsCameoLet;
    /// <summary>Inside a macro quote: the name is supplied by this macro variable (<c>local $name = ...</c>).</summary>
    public string? MacroVar;
    public object? Symbol;
}

/// <summary><c>local a, b: T1, T2 = e1, e2</c></summary>
public sealed class LocalStat : Stat
{
    /// <summary>Generated operand capture; the checker requires a built-in Result.</summary>
    public bool CameoTryOperand;
    /// <summary>Generated recovery slot receives the captured Result's success type.</summary>
    public string? CameoTryPayloadOf;
    /// <summary>Declared error discriminators selected by this capture's handlers.</summary>
    public List<StringExpr>? CameoCatchCases;
    /// <summary>Set by the type checker when named catch handlers exhaustively cover every error kind.</summary>
    public bool CameoTryExhaustive;
    public List<AttribName> Names = new();
    public List<TypeNode>? Types;
    public List<Expr> Values = new();
}

/// <summary><c>global a, b: T1, T2 = e1, e2</c></summary>
public sealed class GlobalStat : Stat
{
    public List<AttribName> Names = new();
    public List<TypeNode>? Types;
    public List<Expr> Values = new();
}

public sealed class AssignStat : Stat
{
    public List<Expr> Targets = new();
    public List<Expr> Values = new();
}

public sealed class CallStat : Stat { public Expr Call = null!; }
public sealed class DoStat : Stat { public Block Body = null!; }
public sealed class WhileStat : Stat { public Expr Cond = null!; public Block Body = null!; }
public sealed class RepeatStat : Stat { public Block Body = null!; public Expr Cond = null!; }

public sealed class IfStat : Stat
{
    public List<(Expr Cond, Block Body)> Clauses = new();
    public Block? Else;
}

public sealed class ForNumStat : Stat
{
    public AttribName Var = null!;
    public Expr Start = null!, Limit = null!;
    public Expr? Step;
    public Block Body = null!;
}

public sealed class ForInStat : Stat
{
    public List<AttribName> Names = new();
    public List<Expr> Exprs = new();
    public Block Body = null!;
    /// <summary>True when parsed in Cameo mode with a single iterator expression, enabling direct collection iteration.</summary>
    public bool CameoForIn;
    /// <summary>Set by the type checker: -1 for standard Lua iterator, 1 for <c>__cameo_iter1</c>, 2 for <c>__cameo_iter2</c>, 0 if unchecked/dynamic.</summary>
    public int CameoIterCount;
}

/// <summary><c>function a.b.c()</c> / <c>function a.b:m()</c> / <c>function f()</c>.</summary>
public sealed class FunctionStat : Stat
{
    /// <summary>The variable being indexed (first name of the path).</summary>
    public NameExpr Root = null!;
    /// <summary>Field names after the root (empty for <c>function f()</c>).</summary>
    public List<string> Path = new();
    public bool IsMethod;
    public FunctionBody Body = null!;
}

public sealed class LocalFunctionStat : Stat
{
    public AttribName Name = null!;
    public FunctionBody Body = null!;
}

public sealed class GlobalFunctionStat : Stat
{
    public string Name = "";
    public FunctionBody Body = null!;
}

public sealed class ReturnStat : Stat
{
    public List<Expr> Values = new();
    /// <summary>The generated branch returns the original, known-error Result.</summary>
    public bool CameoErrorPropagation;
    /// <summary>The try-operand capture this propagation belongs to, if any.</summary>
    public LocalStat? CameoTrySource;
}
/// <summary><c>break</c>, or <c>break name</c> for the enclosing loop labelled <c>::name::</c> (an extension).</summary>
public sealed class BreakStat : Stat { public string? Label; }
/// <summary><c>continue</c>: skips to the next iteration of the innermost loop, or with a label
/// of the enclosing loop labelled <c>::name::</c> (an extension to Teal).</summary>
public sealed class ContinueStat : Stat { public string? Label; }
public sealed class GotoStat : Stat { public string Label = ""; }
public sealed class LabelStat : Stat { public string Name = ""; }

public enum TypeDeclKind { Record, Interface, Enum, Alias }

/// <summary>What a type declaration leaves behind at run time.</summary>
public enum TypeDeclValue
{
    /// <summary>Decide syntactically: records create a table, everything else nothing.</summary>
    Default,
    None,
    /// <summary>The declared name is also a variable holding the value of the aliased type (alias to a record).</summary>
    AliasValue,
}

/// <summary>
/// <c>local|global record/interface/enum Name ... end</c> and <c>local|global type Name = ...</c>.
/// </summary>
public sealed class TypeDeclStat : Stat
{
    public bool IsGlobal;
    public TypeDeclKind Kind;
    public AttribName Name = null!;
    /// <summary>The definition; null for a forward declaration (<c>global type Name</c>).</summary>
    public TypeNode? Def;
    /// <summary>Type parameters of a generic alias: <c>local type Name&lt;T&gt; = ...</c>.</summary>
    public List<TypeParam>? TypeParams;
    /// <summary>Set by the type checker for aliases.</summary>
    public TypeDeclValue RuntimeValue;
    /// <summary>Set by the resolver: expression producing the run-time value of an alias, if it has one.</summary>
    public Expr? AliasValueExpr;
    /// <summary>Set by the resolver: true when this declaration creates a variable.</summary>
    public bool DeclaresValue;
}

/// <summary><c>local macroexp name(...) return expr end</c></summary>
public sealed class MacroExpStat : Stat
{
    public string Name = "";
    public MacroExp Macro = null!;
}

/// <summary>Cameo <c>match</c> used in statement position.</summary>
public sealed class MatchStat : Stat
{
    public CameoMatchExpr Match = null!;
}

public sealed class CameoImportItem : Node
{
    public string ExportName = "";
    public AttribName LocalName = null!;
    /// <summary>True when this imported member has a runtime value (default true for unchecked mode; false for pure interface/enum types).</summary>
    public bool HasRuntimeValue = true;
}

/// <summary>Cameo <c>import name from "mod"</c> or <c>import { a, b as c } from "mod"</c>.</summary>
public sealed class CameoImportStat : Stat
{
    public string Module = "";
    public AttribName? DefaultName;
    public List<CameoImportItem>? Named;
}

/// <summary>Top-level <c>export</c> entry in a Cameo module.</summary>
public sealed class CameoExportItem : Node
{
    public string Name = "";
    public bool HasRuntimeValue;
}

public sealed class Chunk : Node
{
    public string FileName = "";
    public Block Body = null!;
    /// <summary>The chunk as an implicit vararg function.</summary>
    public FunctionBody Main = null!;
    /// <summary>The source text the chunk was parsed from (the type checker reads `--#pragma` comments from it).</summary>
    public string? Source;
    /// <summary>The class declarations of the chunk: where each is, and its identity.</summary>
    public List<((int StartLine, int StartCol, int EndLine, int EndCol) Range, Vm.ClassTag Tag)>? ClassTags;
    /// <summary>Cameo top-level <c>export</c> declarations collected during parsing.</summary>
    public List<CameoExportItem>? CameoExports;
    /// <summary>True when this chunk was parsed with the Cameo surface grammar.</summary>
    public bool IsCameo;
}

// =====================================================================
// Compile-time macros. These nodes only exist between parsing and macro
// expansion (see Macros/MacroExpander.cs); later phases never see them.
// =====================================================================

/// <summary><c>$name</c> in expression position inside a macro quote.</summary>
public sealed class MacroVarExpr : Expr { public string Name = ""; }
/// <summary><c>$name</c> in type position inside a macro quote.</summary>
public sealed class MacroVarTypeNode : TypeNode { public string Name = ""; }
/// <summary><c>$name</c> on its own as a statement inside a macro quote.</summary>
public sealed class MacroVarStat : Stat { public string Name = ""; }

/// <summary>An argument of a macro invocation: an expression or a block of statements.</summary>
public sealed class MacroArg
{
    public Expr? Expr;
    public Block? Stats;
}

/// <summary><c>name!(args)</c>, <c>name!"str"</c>, <c>name!{...}</c></summary>
public sealed class MacroCallExpr : Expr
{
    public string Name = "";
    public List<MacroArg> Args = new();
}

public sealed class MacroCallStat : Stat { public MacroCallExpr Call = null!; }

/// <summary><c>local macro name!(...) ... end</c></summary>
public sealed class MacroDeclStat : Stat
{
    public string Name = "";
    public FunctionBody Body = null!;
    /// <summary>For each fixed parameter: true = Statement, false = Expression.</summary>
    public List<bool> ParamIsStatement = new();
    /// <summary>Kind of the vararg parameter, if the macro has one.</summary>
    public bool? VarargIsStatement;
}

/// <summary>The parsed content of a macro quote (a template instantiated each time the quote is evaluated).</summary>
public sealed class MacroQuote
{
    public bool IsStatements;
    public Expr? Expr;
    public Block? Stats;
    public int Line, Col;
}

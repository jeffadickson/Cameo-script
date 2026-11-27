using Cameo;
using CsCheck;
using Cameo.Runtime.Syntax;
using Xunit;

namespace Cameo.Tests;

/// <summary>Executable grammar contracts for the coherent pure-brace Cameo surface.</summary>
public class CameoSurfaceTests
{
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void Functions_records_and_mutable_bindings_preserve_lexical_scope(bool check)
    {
        using var engine = new CameoEngine(new CameoRuntimeOptions { TypeCheck = check });
        Assert.Equal(42, engine.RunString("record Box { value: integer } var outer = 40 fn add(n: integer) -> integer { if n == 0 { return outer } return add(n - 1) + 1 } do { var outer = 900 } var box: Box = {value = add(2)} box.value += 1 box.value -= 1 return box.value")[0].I);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void All_braced_control_blocks_compose_cleanly(bool check)
    {
        using var engine = new CameoEngine(new CameoRuntimeOptions { TypeCheck = check });
        Assert.Equal(42, engine.RunString("var total = 0 for i = 1, 3 { total += i } for _, value in ipairs({10, 20}) { total += value } while total < 40 { total += 1 } if false { total = 0 } else if total == 40 { do { total += 1 } } else { total = -1 } do { if total == 41 { total += 1 } } return total")[0].I);
    }

    [Theory]
    [InlineData("fn f() { return }")]
    [InlineData("fn f<T>(value: T) -> T { return value } return f(42)")]
    [InlineData("record R { ['end']: integer value: {integer} } var x: R = {['end'] = 42, value = {1}} return x['end']")]
    [InlineData("record R { {integer} record Nested { value: integer } item: Nested }")]
    [InlineData("if predicate(make{}) { return 42 }")]
    public void Existing_signature_and_record_types_remain_available(string source) => Parser.ParseCameo(source, "surface.cameo");

    [Fact]
    public void Header_calls_do_not_consume_bodies_but_nested_tables_remain_arguments()
    {
        using var engine = new CameoEngine(new CameoRuntimeOptions { TypeCheck = false });
        Assert.Equal(42, engine.RunString("fn yes(value) { return value.answer == 42 } var receiver = {yes = (self, value) => value.answer == 42} var answer = 0 if yes({answer = 42}) { answer += 20 } if receiver:yes({answer = 42}) { answer += 22 } return answer")[0].I);
    }

    [Theory]
    [InlineData("if true { let x = 1 end")]
    [InlineData("if true then let x = 1 }")]
    [InlineData("do { do let x = 1 } end")]
    [InlineData("do let x = 1 } end")]
    [InlineData("fn f() { return 1 end")]
    [InlineData("record R { value: integer end")]
    [InlineData("var x")]
    [InlineData("var x <const> = 1")]
    [InlineData("}")]
    [InlineData("if predicate {answer = 42} { return 1 }")]
    [InlineData("if receiver:predicate {answer = 42} { return 1 }")]
    public void Malformed_or_mixed_delimiters_have_source_diagnostics(string source)
    {
        var error = Assert.Throws<CameoSyntaxException>(() => Parser.ParseCameo(source, "surface.cameo"));
        Assert.Contains("surface.cameo", error.Message);
    }

    [Fact]
    public void Contextual_names_remain_identifiers_in_both_modes()
    {
        const string cameoSource = "let fn, var, record = 10, 20, 12 let box = {fn = fn, var = var, record = record} return box.fn + box.var + box.record";
        const string tealSource = "local fn, var, record = 10, 20, 12 local box = {fn = fn, var = var, record = record} return box.fn + box.var + box.record";
        using var cameo = new CameoEngine(); using var teal = new ScriptEngine();
        Assert.Equal(42, cameo.RunString(cameoSource)[0].I); Assert.Equal(42, teal.RunString(tealSource)[0].I);
        Assert.Throws<CameoSyntaxException>(() => Parser.Parse("fn answer() { return 42 }", "legacy.tl"));
    }

    [Fact]
    public void Generated_numeric_loops_match_teal_semantics()
    {
        Cameo.Tests.Properties.Prop.Cs(Gen.Int[0, 30], limit =>
        {
            using var cameo = new CameoEngine(); using var teal = new ScriptEngine();
            Assert.Equal(teal.RunString($"local total = 0 for i = 1, {limit} do total += i end return total")[0].I,
                cameo.RunString($"var total = 0 for i = 1, {limit} {{ total += i }} return total")[0].I);
        }, iter: 40);
    }

    [Fact]
    public void Bounded_surface_token_inputs_only_accept_or_report_syntax_errors()
    {
        string[] tokens = ["fn", "var", "record", "if", "else", "do", "end", "return", "x", "42", "=", "{", "}", "(", ")", "->"];
        Cameo.Tests.Properties.Prop.Cs(Gen.String[Gen.Char['a', 'p'], 0, 40], input =>
        {
            try { Parser.ParseCameo(string.Join(' ', input.Select(c => tokens[c - 'a'])), "fuzz.cameo"); }
            catch (CameoSyntaxException) { }
        }, iter: 500);
    }

    [Theory]
    [InlineData("factory{} then")]
    [InlineData("factory{}.ready then")]
    [InlineData("factory{}:ready() then")]
    [InlineData("factory{}[1] then")]
    [InlineData("factory{}() then")]
    [InlineData("factory{} 'ok' then")]
    [InlineData("factory{}{} then")]
    [InlineData("factory{} and true then")]
    [InlineData("factory{} is boolean then")]
    [InlineData("receiver:ready{} then")]
    public void Legacy_header_shorthand_stays_in_teal_and_is_rejected_in_cameo(string header)
    {
        Parser.Parse("if " + header + " return 42 end", "legacy.tl");
        Assert.Throws<CameoSyntaxException>(() => Parser.ParseCameo("if " + header + " return 42 end", "legacy.cameo"));
    }

    [Theory]
    [InlineData("fn f() - > integer { return 1 }")]
    [InlineData("fn f() - // gap\n > integer { return 1 }")]
    [InlineData("fn f() -> integer { return 1")]
    [InlineData("if true { do return 1 }")]
    [InlineData("if true { return 1 } else return 2 end")]
    public void Split_arrows_and_unfinished_blocks_are_rejected(string source)
        => Assert.Throws<CameoSyntaxException>(() => Parser.ParseCameo(source, "surface.cameo"));

    [Theory]
    [InlineData(true, false)] [InlineData(false, false)]
    [InlineData(true, true)] [InlineData(false, true)]
    public void Functions_execute_named_varargs_generics_and_capture_on_both_targets(bool check, bool compiled)
    {
        using var engine = new CameoEngine(new CameoRuntimeOptions { TypeCheck = check });
        Cameo.Jit.JitLibrary.Enable(engine.Runtime);
        var values = engine.RunString("fn identity<T>(x: T) -> T { return x } var captured = 40 fn first(...values: integer) -> integer { return values[1] } fn answer(value: integer) -> integer { var captured = captured while captured < 41 { captured += 1 } if value == 1 { return identity(captured + first(value)) } else { return 0 } } return answer");
        var function = values[0];
        if (compiled)
        {
            Assert.True(Cameo.Jit.JitLibrary.TryCompile(function, out var lowered, out var reason), reason?.ToString());
            function = lowered;
        }
        Assert.Equal(42, engine.State.Call(function, Cameo.Runtime.Vm.Value.FromInt(1L))[0].I);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void Newlines_preserve_return_values_leading_dots_and_call_boundaries(bool check)
    {
        using var engine = new CameoEngine(new CameoRuntimeOptions { TypeCheck = check });
        Assert.Equal(42, engine.RunString("fn answer() -> integer { return\n Future.completedFuture(42)\n // chain comment\n .get() } return answer()")[0].I);
        const string cameoCalls = "var result = 0 fn sink(x: integer) { result = x } fn factory() -> fn(integer) { return sink } factory()\n(sink)(42) return result";
        const string tealCalls = "local result = 0 local function sink(x: integer) result = x end local function factory(): function(integer) return sink end factory()\n(sink)(42) return result";
        Assert.Equal(42, engine.RunString(cameoCalls)[0].I);
        using var teal = new ScriptEngine(new CameoRuntimeOptions { TypeCheck = check });
        Assert.Equal(42, teal.RunString(tealCalls)[0].I);
    }

    [Fact]
    public void Nested_arrow_headers_parse_cleanly()
    {
        string condition = "true";
        for (int depth = 0; depth < 18; depth++)
            condition = "(() => { if " + condition + " { } return true })()";
        Parser.ParseCameo("if " + condition + " { }", "nested.cameo");
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void Named_functions_are_local_and_do_not_forward_hoist(bool check)
    {
        using var engine = new CameoEngine(new CameoRuntimeOptions { TypeCheck = check });
        Assert.IsNotType<RunCompleted>(engine.TryRunString("do { fn hidden() -> integer { return 42 } } return hidden()").Value);
        Assert.IsNotType<RunCompleted>(engine.TryRunString("fn before() -> integer { return after() } fn after() -> integer { return 42 } return before()").Value);
    }

    [Fact]
    public void Record_predicates_preserve_callable_headers_and_existing_field_types()
    {
        Parser.ParseCameo("fn valid(x: any) -> boolean { return true } record Box where valid(self) { value: integer }", "where.cameo");
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void Named_function_capture_executes_through_parallel_future_entry(bool check)
    {
        using var engine = new CameoEngine(new CameoRuntimeOptions { TypeCheck = check });
        Assert.Equal(441, engine.RunString("fn square(value: integer) -> integer { return value * value } let job = Future.parallel(() => square(21)) return job.get()")[0].I);
    }

    [Theory]
    [InlineData("var x, y <const> = 1, 2")]
    [InlineData("var x <const>, y = 1, 2")]
    public void Mutable_declarations_reject_attributes_on_every_binding(string source)
    {
        var error = Assert.Throws<CameoSyntaxException>(() => Parser.ParseCameo(source, "attributes.cameo"));
        Assert.Contains("var bindings do not accept local attributes", error.Message);
    }

    [Theory]
    [InlineData("var x 42")] [InlineData("let x 42")]
    public void Missing_initializer_operator_offers_the_required_repair(string source)
    {
        var error = Assert.Throws<CameoSyntaxException>(() => Parser.ParseCameo(source, "initializer.cameo"));
        Assert.Contains("requires an initializer ('= expression')", error.Message);
    }

    [Theory]
    [InlineData("+>")] [InlineData("=>")] [InlineData("-<")]
    public void Named_functions_reject_other_adjacent_arrow_spellings(string arrow)
        => Assert.Throws<CameoSyntaxException>(() => Parser.ParseCameo("fn answer() " + arrow + " integer { return 42 }", "arrow.cameo"));

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void Braced_else_if_selects_the_matching_branch(bool check)
    {
        using var engine = new CameoEngine(new CameoRuntimeOptions { TypeCheck = check });
        Assert.Equal(42, engine.RunString("var answer = 0 if false { answer = -1 } else if false { answer = -2 } else if true { answer = 42 } else { answer = -3 } return answer")[0].I);
    }

    [Theory]
    [InlineData("if ready() { }", true)] [InlineData("if ready() { }", false)]
    [InlineData("var count = 0 while ready() && count < 1 { count += 1 }", true)]
    [InlineData("var count = 0 while ready() && count < 1 { count += 1 }", false)]
    [InlineData("for _, value in ipairs({1}) { }", true)]
    [InlineData("for _, value in ipairs({1}) { }", false)]
    [InlineData("for index = 1, 2 { }", true)] [InlineData("for index = 1, 2 { }", false)]
    [InlineData("record Box where ready() { value: integer }", true)]
    [InlineData("record Box where ready() { value: integer }", false)]
    public void Header_boundaries_preserve_later_ordinary_table_call_arguments(string statement, bool check)
    {
        using var engine = new CameoEngine(new CameoRuntimeOptions { TypeCheck = check });
        const string helpers = "var answer = 0 fn ready() -> boolean { return true } fn add(values: {integer}) { answer += values[1] } ";
        Assert.Equal(42, engine.RunString(helpers + statement + " add {42} return answer")[0].I);
    }

    [Theory]
    [InlineData("record R { value: integer }", false, TypeDeclKind.Record)]
    [InlineData("interface I { value: integer }", true, TypeDeclKind.Interface)]
    public void Record_and_interface_declarations_retain_distinct_type_contracts(string source, bool isInterface, TypeDeclKind kind)
    {
        var declaration = Assert.IsType<TypeDeclStat>(Assert.Single(Parser.ParseCameo(source, "types.cameo").Body.Stats));
        Assert.Equal(kind, declaration.Kind);
        Assert.Equal(isInterface, Assert.IsType<RecordTypeNode>(declaration.Def).IsInterface);
    }

    [Fact]
    public void Unclosed_record_bodies_name_the_required_delimiter()
    {
        var error = Assert.Throws<CameoSyntaxException>(() => Parser.ParseCameo("record R { value: integer", "unclosed.cameo"));
        Assert.Contains("'}' expected", error.Message);
        Assert.Contains("unclosed.cameo:1:", error.Message);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void Optional_named_function_parameters_reuse_existing_defaultless_nil_behavior(bool check)
    {
        using var engine = new CameoEngine(new CameoRuntimeOptions { TypeCheck = check });
        Assert.Equal(42, engine.RunString("fn answer(value?: integer) -> integer { return value ?? 42 } return answer()")[0].I);
        Assert.Equal(7, engine.RunString("fn answer(value?: integer) -> integer { return value ?? 42 } return answer(7)")[0].I);
    }

    [Fact]
    public void Truncated_braced_record_reports_the_actual_brace_opener_line()
    {
        var error = Assert.Throws<CameoSyntaxException>(() => Parser.ParseCameo("record R\n{\n    value: integer\n", "multiline-record.cameo"));
        Assert.Contains("'}' expected (to close '{' at line 2)", error.Message);
        Assert.Equal(4, error.Diagnostic.Line);
    }

    [Theory]
    [InlineData("if ready() { }", true)] [InlineData("if ready() { }", false)]
    [InlineData("var count = 0 while ready() && count < 1 { count += 1 }", true)]
    [InlineData("var count = 0 while ready() && count < 1 { count += 1 }", false)]
    [InlineData("for _, value in ipairs({1}) { }", true)]
    [InlineData("for _, value in ipairs({1}) { }", false)]
    [InlineData("for index = 1, 2 { }", true)] [InlineData("for index = 1, 2 { }", false)]
    [InlineData("record Box where ready() { value: integer }", true)]
    [InlineData("record Box where ready() { value: integer }", false)]
    public void Header_boundaries_preserve_table_calls_in_later_expressions(string statement, bool check)
    {
        using var engine = new CameoEngine(new CameoRuntimeOptions { TypeCheck = check });
        const string helpers = "fn ready() -> boolean { return true } fn add(values: {integer}) -> integer { return values[1] } ";
        Assert.Equal(42, engine.RunString(helpers + statement + " return add {42}")[0].I);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void Header_boundaries_preserve_colon_method_table_arguments(bool check)
    {
        using var engine = new CameoEngine(new CameoRuntimeOptions { TypeCheck = check });
        Assert.Equal(42, engine.RunString("let receiver = {add = (self: any, values: {integer}) -> integer => { return values[1] }} if true { } return receiver:add {42}")[0].I);
    }

    [Fact]
    public void Readonly_binding_attribute_diagnostic_identifies_the_unsupported_attribute()
    {
        var error = Assert.Throws<CameoSyntaxException>(() => Parser.ParseCameo("let x <const> = 1", "let-attributes.cameo"));
        Assert.Contains("let bindings do not accept local attributes", error.Message);
    }
}

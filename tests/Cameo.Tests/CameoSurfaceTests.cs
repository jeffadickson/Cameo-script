using Cameo;
using CsCheck;
using Cameo.Runtime.Syntax;
using Xunit;

namespace Cameo.Tests;

/// <summary>Executable grammar contracts for the coherent Cameo surface and legacy parity.</summary>
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
    public void All_control_blocks_can_nest_with_legacy_blocks(bool check)
    {
        using var engine = new CameoEngine(new CameoRuntimeOptions { TypeCheck = check });
        Assert.Equal(42, engine.RunString("var total = 0 for i = 1, 3 { total += i } for _, value in ipairs({10, 20}) { total += value } while total < 40 { total += 1 } if false { total = 0 } else if total == 40 { do total += 1 end } else { total = -1 } do { if total == 41 then total += 1 end } repeat total -= 1 until total == 41 return total + 1")[0].I);
    }

    [Theory]
    [InlineData("fn f() { return }")] [InlineData("fn f<T>(value: T) -> T { return value } return f(42)")]
    [InlineData("record R { ['end']: integer value: {integer} } var x: R = {['end'] = 42, value = {1}} return x['end']")]
    public void Existing_signature_and_record_types_remain_available(string source) => Parser.ParseCameo(source, "surface.cameo");

    [Fact]
    public void Header_calls_do_not_consume_bodies_but_nested_tables_remain_arguments()
    {
        using var engine = new CameoEngine(new CameoRuntimeOptions { TypeCheck = false });
        Assert.Equal(42, engine.RunString("fn yes(value) { return value.answer == 42 } var receiver = {yes = function(self, value) return value.answer == 42 end} var answer = 0 if yes({answer = 42}) { answer += 20 } if receiver:yes({answer = 42}) { answer += 22 } return answer")[0].I);
    }

    [Theory]
    [InlineData("if true { local x = 1 end")]
    [InlineData("if true then local x = 1 }")]
    [InlineData("do { do local x = 1 } end")]
    [InlineData("do local x = 1 } end")]
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
        const string source = "local fn, var, record = 10, 20, 12 local box = {fn = fn, var = var, record = record} return box.fn + box.var + box.record";
        using var cameo = new CameoEngine(); using var teal = new ScriptEngine();
        Assert.Equal(42, cameo.RunString(source)[0].I); Assert.Equal(42, teal.RunString(source)[0].I);
        Assert.Throws<CameoSyntaxException>(() => Parser.Parse("fn answer() { return 42 }", "legacy.tl"));
    }

    [Fact]
    public void Generated_numeric_loops_match_legacy_spelling()
    {
        Cameo.Tests.Properties.Prop.Cs(Gen.Int[0, 30], limit =>
        {
            using var cameo = new CameoEngine(); using var teal = new ScriptEngine();
            string body = $"local total = 0 for i = 1, {limit}";
            Assert.Equal(teal.RunString(body + " do total += i end return total")[0].I,
                cameo.RunString(body + " { total += i } return total")[0].I);
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
    public void Legacy_header_shorthand_and_every_suffix_remain_accepted(string header)
    {
        Parser.ParseCameo("if " + header + " return 42 end", "legacy.cameo");
        Parser.Parse("if " + header + " return 42 end", "legacy.tl");
    }

    [Theory]
    [InlineData("for i = factory{}, factory{} do end")]
    [InlineData("for i = 1, factory{}, factory{} do end")]
    [InlineData("for key, value in factory{}, other{} do end")]
    [InlineData("while receiver:ready{} do break end")]
    [InlineData("if predicate(make{}) { return 42 }")]
    [InlineData("record R { {integer} record Nested value: integer end item: Nested }")]
    public void Legacy_header_lists_nested_arguments_and_record_bodies_remain_accepted(string source)
        => Parser.ParseCameo(source, "surface.cameo");

    [Theory]
    [InlineData("fn f() - > integer { return 1 }")]
    [InlineData("fn f() - -- gap\n > integer { return 1 }")]
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
    public void Newlines_preserve_return_values_leading_dots_and_legacy_call_boundaries(bool check)
    {
        using var engine = new CameoEngine(new CameoRuntimeOptions { TypeCheck = check });
        Assert.Equal(42, engine.RunString("fn answer() -> integer { return\n Future.completedFuture(42)\n -- chain comment\n .get() } return answer()")[0].I);
        const string calls = "local result = 0 local function sink(x: integer) result = x end local function factory(): function(integer) return sink end factory()\n(sink)(42) return result";
        Assert.Equal(42, engine.RunString(calls)[0].I);
        using var teal = new ScriptEngine(new CameoRuntimeOptions { TypeCheck = check });
        Assert.Equal(42, teal.RunString(calls)[0].I);
    }

    [Fact]
    public void Nested_function_headers_and_unused_macro_do_not_change_parse_acceptance()
    {
        string condition = "true";
        for (int depth = 0; depth < 18; depth++)
            condition = "(function() if " + condition + " { } return true end)()";
        Parser.ParseCameo("if " + condition + " { }", "nested.cameo");
        Parser.ParseCameo("local macro unused!(x: Expression) return `1` end if " + condition + " { }", "nested-macro.cameo");
    }

    [Theory]
    [InlineData("function(): Box<Box<integer>> ")]
    [InlineData("function() local class Empty end ")]
    [InlineData("function() local macro inner!(x: Expression) return `1` end ")]
    public void Nested_header_probes_preserve_syntax_context_across_normalization(string prefix)
    {
        string condition = "true";
        for (int depth = 0; depth < 16; depth++)
            condition = "(" + prefix + "if " + condition + " { } return nil end)()";
        string source = "if " + condition + " { }";
        if (prefix.Contains("local macro", StringComparison.Ordinal))
            Assert.Contains("top level", Assert.Throws<CameoSyntaxException>(() => Parser.ParseCameo(source, "normalized.cameo")).Message);
        else Parser.ParseCameo(source, "normalized.cameo");
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void Named_functions_are_local_and_do_not_forward_hoist(bool check)
    {
        using var engine = new CameoEngine(new CameoRuntimeOptions { TypeCheck = check });
        Assert.IsNotType<RunCompleted>(engine.TryRunString("do { fn hidden() -> integer { return 42 } } return hidden()").Value);
        Assert.IsNotType<RunCompleted>(engine.TryRunString("fn before() -> integer { return after() } fn after() -> integer { return 42 } return before()").Value);
    }
    [Theory]
    [InlineData(64)] [InlineData(256)] [InlineData(4096)] [InlineData(8192)]
    public void Sequential_macro_signatures_allow_valid_headers_and_diagnose_unclosed_headers(int count)
    {
        var source = new System.Text.StringBuilder();
        for (int index = 0; index < count; index++)
            source.Append("local macro m").Append(index).Append("!(x: Expression) return `1` end\n");
        string prefix = source.ToString();
        Parser.ParseCameo(prefix + "if true { }", "many-macros.cameo");
        Assert.Throws<CameoSyntaxException>(() => Parser.ParseCameo(prefix + "if true {", "many-macros.cameo"));
    }

    [Theory]
    [InlineData("record Box where valid(self) { value: integer }")]
    [InlineData("local record Box where valid(self) value: integer end")]
    public void Record_predicates_preserve_callable_headers_and_existing_field_types(string declaration)
    {
        // Parse-only: the existing where reparse/type rules remain outside this grammar test.
        Parser.ParseCameo("local function valid(x: any): boolean return true end " + declaration, "where.cameo");
    }

    [Theory]
    [InlineData("record Box { value: integer }")]
    [InlineData("record Box value: integer end")]
    [InlineData("record Box {integer} value: integer end")]
    public void Statement_macro_arguments_accept_braced_and_legacy_records(string declaration)
    {
        Parser.ParseCameo("local macro emit!(body: Statement) return ```$body``` end emit!(" + declaration + ")", "record-macro.cameo");
    }

    [Fact]
    public void Interleaved_macro_declarations_and_headers_preserve_macro_signature_lookup()
    {
        var source = new System.Text.StringBuilder("local macro emit!(body: Statement) return ```$body``` end\n");
        for (int index = 0; index < 1024; index++)
            source.Append("local macro m").Append(index).Append("!(x: Expression) return `1` end\nif true { }\n");
        source.Append("emit!(var answer = 42) return answer");
        using var engine = new CameoEngine();
        Assert.Equal(42, engine.RunString(source.ToString())[0].I);
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
    public void Braced_elseif_selects_the_matching_branch(bool check)
    {
        using var engine = new CameoEngine(new CameoRuntimeOptions { TypeCheck = check });
        Assert.Equal(42, engine.RunString("var answer = 0 if false { answer = -1 } elseif false { answer = -2 } elseif true { answer = 42 } else { answer = -3 } return answer")[0].I);
    }

    [Theory]
    [InlineData("if ready() { }", true)] [InlineData("if ready() { }", false)]
    [InlineData("var count = 0 while ready() and count < 1 { count += 1 }", true)]
    [InlineData("var count = 0 while ready() and count < 1 { count += 1 }", false)]
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

    [Fact]
    public void Legacy_record_predicates_keep_bare_table_arguments()
        => Parser.ParseCameo("local record R where valid{} value: integer end", "legacy-where.cameo");

    [Theory]
    [InlineData("record R { value: integer }", false, TypeDeclKind.Record)]
    [InlineData("local record R value: integer end", false, TypeDeclKind.Record)]
    [InlineData("local interface R value: integer end", true, TypeDeclKind.Interface)]
    public void Record_and_interface_declarations_retain_distinct_type_contracts(string source, bool isInterface, TypeDeclKind kind)
    {
        var declaration = Assert.IsType<TypeDeclStat>(Assert.Single(Parser.ParseCameo(source, "types.cameo").Body.Stats));
        Assert.Equal(kind, declaration.Kind);
        Assert.Equal(isInterface, Assert.IsType<RecordTypeNode>(declaration.Def).IsInterface);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void Macro_quotes_preserve_calls_to_an_identifier_named_let(bool check)
    {
        using var engine = new CameoEngine(new CameoRuntimeOptions { TypeCheck = check });
        Assert.Equal(42, engine.RunString("local total = 0 local let = function(value: integer) total = value end local macro invoke!() return ```let(42)``` end invoke!() return total")[0].I);
    }

    [Theory]
    [InlineData("record R { value: integer", "'}' expected")]
    [InlineData("local record R value: integer", "'end' expected")]
    public void Unclosed_record_bodies_name_the_required_delimiter(string source, string expected)
    {
        var error = Assert.Throws<CameoSyntaxException>(() => Parser.ParseCameo(source, "unclosed.cameo"));
        Assert.Contains(expected, error.Message);
        Assert.Contains("unclosed.cameo:1:", error.Message);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void Optional_named_function_parameters_reuse_existing_defaultless_nil_behavior(bool check)
    {
        using var engine = new CameoEngine(new CameoRuntimeOptions { TypeCheck = check });
        Assert.Equal(42, engine.RunString("fn answer(value?: integer) -> integer { return value or 42 } return answer()")[0].I);
        Assert.Equal(7, engine.RunString("fn answer(value?: integer) -> integer { return value or 42 } return answer(7)")[0].I);
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
    [InlineData("var count = 0 while ready() and count < 1 { count += 1 }", true)]
    [InlineData("var count = 0 while ready() and count < 1 { count += 1 }", false)]
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
        Assert.Equal(42, engine.RunString("let receiver = {add = function(self: any, values: {integer}): integer return values[1] end} if true { } return receiver:add {42}")[0].I);
    }

    [Fact]
    public void Readonly_binding_attribute_diagnostic_identifies_the_unsupported_attribute()
    {
        var error = Assert.Throws<CameoSyntaxException>(() => Parser.ParseCameo("let x <const> = 1", "let-attributes.cameo"));
        Assert.Contains("let bindings do not accept local attributes", error.Message);
    }

    [Theory]
    [InlineData("local record R\n", "record")]
    [InlineData("local interface I\n", "interface")]
    public void Truncated_legacy_types_name_the_construct_and_its_required_end(string source, string construct)
    {
        var error = Assert.Throws<CameoSyntaxException>(() => Parser.ParseCameo(source, "truncated-type.cameo"));
        Assert.Contains("'end' expected (to close '" + construct + "' at line 1)", error.Message);
        Assert.Equal(2, error.Diagnostic.Line);
    }
}

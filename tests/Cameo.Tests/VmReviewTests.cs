using System.Diagnostics;
using System.Text;
using CsCheck;
using Cameo.Runtime.Vm;
using Xunit;

namespace Cameo.Tests;

/// <summary>Behavioral qualification of the earlier VM review, not a claim of full Lua conformance.</summary>
public class VmReviewTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Parse_failure_replaces_previous_check_warnings(bool cameo)
    {
        using var engine=new ScriptEngine(null,cameo?Cameo.CameoLanguage.Instance:CameoSourceLanguage.Instance);
        Assert.Empty(engine.Check("local unused = 1","old.tl"));
        Assert.Contains(engine.Warnings,d=>d.File=="old.tl");
        Assert.NotEmpty(engine.Check("local =","new.tl"));
        Assert.Empty(engine.Warnings);
        Assert.Empty(engine.Check("return 42","valid.tl"));
        Assert.Empty(engine.Warnings);
    }

    [Theory]
    [InlineData("table.sort({2,1}, function(a: integer,b: integer): boolean coroutine.yield() return a < b end)")]
    [InlineData("string.gsub('x','x',function(): string coroutine.yield() return 'y' end)")]
    [InlineData("local t = setmetatable({}, {__concat=function(): string coroutine.yield() return '' end}) local s = t .. t")]
    [InlineData("local v <close> = setmetatable({}, {__close=function() coroutine.yield() end})")]
    public void Native_callback_yield_boundaries_report_errors_and_leave_engine_usable(string body)
    {
        using var engine=new ScriptEngine();
        var values=engine.RunString($"local co = coroutine.create(function() {body} end) local ok, message = coroutine.resume(co) return ok, message, coroutine.status(co)");
        Assert.False(values[0].IsTruthy);
        Assert.Contains("attempt to yield across a native-call boundary",values[1].ToText());
        Assert.Equal("dead",values[2].ToText());
        Assert.Equal(7,engine.RunString("return 7")[0].I);
    }

    [Fact]
    public void Explicit_host_reentry_cannot_hide_a_coroutine_yield()
    {
        using var engine=new ScriptEngine();
        engine.State.SetGlobal("host",Value.FromFunction((ref CallContext c)=>c.Return(c.L.Call1(c.CheckFunction(0))),"host"));
        var values=engine.RunString("global host: function(function(): integer): integer local co = coroutine.create(function() host(function(): integer coroutine.yield() return 1 end) end) return coroutine.resume(co)");
        Assert.False(values[0].IsTruthy);
        Assert.Contains("native-call boundary",values[1].ToText());
        Assert.Equal(7,engine.RunString("return 7")[0].I);
    }

    [Fact]
    public void Ordinary_coroutines_yield_values_and_resume_inside_protected_calls()
    {
        // Exercise dynamic coroutine result packs independently of static arity typing.
        using var engine=new ScriptEngine(new CameoRuntimeOptions{TypeCheck=false});
        // The native-boundary guard must permit the normal coroutine path as well as
        // reject host reentry. Protected Lua frames preserve the suspended continuation.
        var values=engine.RunString("""
            local co = coroutine.create(function()
                local ok, value = pcall(function(): integer
                    local resumed = coroutine.yield(12, 'paused')
                    return resumed + 1
                end)
                return ok, value
            end)
            local first, number, text = coroutine.resume(co)
            local suspended = coroutine.status(co)
            local second, protected, result = coroutine.resume(co, 41)
            return first, number, text, suspended, second, protected, result, coroutine.status(co)
            """);
        Assert.True(values[0].IsTruthy);Assert.Equal(12,values[1].I);
        Assert.Equal("paused",values[2].ToText());Assert.Equal("suspended",values[3].ToText());
        Assert.True(values[4].IsTruthy);Assert.True(values[5].IsTruthy);
        Assert.Equal(42,values[6].I);Assert.Equal("dead",values[7].ToText());
    }

    [Fact]
    public void Text_loader_rejects_binary_headers_from_strings_and_readers()
    {
        using var engine=new ScriptEngine();
        var values=engine.RunString("""
            local binary = string.char(27) .. 'Lua' .. string.char(0,255,0)
            local a, ea = load(binary)
            local sent = false
            local b, eb = load(function(): string if sent then return nil end sent=true return binary end)
            local text = assert(load('return 42'))
            return a == nil, type(ea), b == nil, type(eb), text()
            """);
        Assert.True(values[0].IsTruthy);Assert.Equal("string",values[1].ToText());
        Assert.True(values[2].IsTruthy);Assert.Equal("string",values[3].ToText());
        Assert.Equal(42,values[4].I);
        // The existing language deliberately ignores mode/env; this is not binary-mode support.
        Assert.Equal(42,engine.RunString("return assert(load('return 42', 'text', 'b'))()")[0].I);
    }

    [Fact]
    public void Binary_prefixed_malformed_loader_inputs_never_execute()
    {
        using var engine=new ScriptEngine();
        var loader=engine.RunString("return function(s: string): boolean local f = load(s) return f == nil end")[0];
        Gen.String[Gen.Char[(char)0,(char)255],0,100].Sample(tail=>
            Assert.True(engine.State.Call(loader,Value.FromString("\u001bLua"+tail))[0].IsTruthy),
            iter:200,threads:1,seed:Properties.Prop.Seeds.CsCheckSeed);
    }

    [Theory]
    [InlineData("int", "return host(12)", "12")]
    [InlineData("int", "return host('12')", "12")]
    [InlineData("string", "return host(12)", "12")]
    [InlineData("text", "return host('héllo')", "héllo")]
    public void Host_argument_helpers_preserve_lua_coercion_and_text(string kind,string call,string expected)
    {
        using var engine=new ScriptEngine();
        engine.State.SetGlobal("host",Value.FromFunction((ref CallContext c)=>kind switch
        {
            "int"=>c.Return(Value.FromInt(c.CheckInt(0))),
            "string"=>c.Return(Value.FromString(c.CheckString(0))),
            _=>c.ReturnText(c.CheckText(0))
        },"host"));
        Assert.Equal(expected,engine.RunString("global host: function(any): any "+call)[0].ToText());
    }

    [Theory]
    [InlineData("int", "true")]
    [InlineData("int", "1.5")]
    [InlineData("string", "{}")]
    [InlineData("text", "false")]
    [InlineData("text", "")]
    public void Host_argument_errors_are_catchable_and_actionable(string kind,string argument)
    {
        using var engine=new ScriptEngine();
        engine.State.SetGlobal("host",Value.FromFunction((ref CallContext c)=>
        {
            if(kind=="int")c.CheckInt(0);else if(kind=="string")c.CheckString(0);else c.CheckText(0);
            return c.Return();
        },"host"));
        var values=engine.RunString($"global host: function(...: any): any return pcall(host, {argument})".Replace("host, )","host)"));
        Assert.False(values[0].IsTruthy);
        Assert.Contains("bad argument #1 to 'host'",values[1].ToText());
        Assert.Equal(7,engine.RunString("return 7")[0].I);
    }

    [Theory]
    [InlineData("1.5", "number has no integer representation")]
    [InlineData("'1.5'", "number has no integer representation")]
    [InlineData("'not a number'", "number expected, got string")]
    [InlineData("true", "number expected, got boolean")]
    public void Integer_argument_errors_distinguish_fractional_numbers_from_wrong_types(string argument,string expected)
    {
        using var engine=new ScriptEngine();
        engine.State.SetGlobal("host",Value.FromFunction((ref CallContext c)=>c.Return(Value.FromInt(c.CheckInt(0))),"host"));
        var values=engine.RunString($"global host: function(any): integer return pcall(host, {argument})");
        Assert.False(values[0].IsTruthy);
        // A caller can fix a fractional value differently from a nonnumeric value;
        // merely asserting that both fail would miss loss of the useful diagnosis.
        Assert.Contains($"bad argument #1 to 'host' ({expected})",values[1].ToText());
    }

    [Theory]
    [InlineData("12", 12.0)]
    [InlineData("1.5", 1.5)]
    [InlineData("'12.5'", 12.5)]
    public void Number_arguments_accept_numeric_values_and_numeric_strings(string argument,double expected)
    {
        using var engine=new ScriptEngine();
        engine.State.SetGlobal("host",Value.FromFunction((ref CallContext c)=>c.Return(Value.FromFloat(c.CheckNumber(0))),"host"));
        Assert.Equal(expected,engine.RunString($"global host: function(any): number return host({argument})")[0].AsDouble);
    }

    [Theory]
    [InlineData("false", "boolean")]
    [InlineData("'not a number'", "string")]
    public void Number_arguments_reject_nonnumeric_values(string argument,string actualType)
    {
        using var engine=new ScriptEngine();
        engine.State.SetGlobal("host",Value.FromFunction((ref CallContext c)=>c.Return(Value.FromFloat(c.CheckNumber(0))),"host"));
        var values=engine.RunString($"global host: function(any): number return pcall(host, {argument})");
        Assert.False(values[0].IsTruthy);
        Assert.Contains($"number expected, got {actualType}",values[1].ToText());
    }

    [Fact]
    public void Optional_host_arguments_use_defaults_only_for_nil_or_missing_values()
    {
        using var engine=new ScriptEngine();
        engine.State.SetGlobal("host",Value.FromFunction((ref CallContext c)=>c.Return(
            Value.FromInt(c.OptInt(0,17)),Value.FromFloat(c.OptNumber(1,2.5)),Value.FromString(c.OptString(2,"default"))),"host"));
        const string declaration="global host: function(...: any): integer, number, string ";
        foreach(string arguments in new[]{"", "nil,nil,nil"})
        {
            var defaults=engine.RunString(declaration+$"return host({arguments})");
            Assert.Equal(17,defaults[0].I);Assert.Equal(2.5,defaults[1].AsDouble);Assert.Equal("default",defaults[2].ToText());
        }
        var supplied=engine.RunString(declaration+"return host('42','3.5',12)");
        Assert.Equal(42,supplied[0].I);Assert.Equal(3.5,supplied[1].AsDouble);Assert.Equal("12",supplied[2].ToText());
        // Optional does not mean invalid values are replaced by defaults.
        foreach(string arguments in new[]{"false", "nil, false", "nil, nil, {}"})
            Assert.False(engine.RunString(declaration+$"return pcall(host, {arguments})")[0].IsTruthy);
    }

    [Fact]
    public void Host_results_preserve_nil_arity_and_raw_bytes()
    {
        using var engine=new ScriptEngine();
        engine.State.SetGlobal("nil_text",Value.FromFunction((ref CallContext c)=>c.ReturnText(null),"nil_text"));
        engine.State.SetGlobal("raw",Value.FromFunction((ref CallContext c)=>c.Return(Value.FromString("\0\u00ff"),Value.Nil,Value.FromInt(42)),"raw"));
        var values=engine.RunString("global nil_text: function(): string global raw: function(): string, any, integer local a,b,c = raw() return select('#',nil_text()), nil_text()==nil, a,b,c");
        Assert.Equal(1,values[0].I);Assert.True(values[1].IsTruthy);
        Assert.Equal(new byte[]{0,255},Encoding.Latin1.GetBytes(values[2].AsString));
        Assert.True(values[3].IsNil);Assert.Equal(42,values[4].I);
    }

    [Fact]
    public void Cancellation_escapes_nested_protected_calls_and_allows_reuse()
    {
        using var engine=new ScriptEngine(new CameoRuntimeOptions{Timeout=TimeSpan.FromSeconds(10)});
        using var entered=new ManualResetEventSlim();
        using var lifetime=new CancellationTokenSource();
        Exception? workerError=null;
        engine.State.SetGlobal("cancel_gate",Value.FromFunction((ref CallContext c)=>
        {
            entered.Set(); // Execution is inside both protected calls, after watchdog startup.
            return c.Return();
        },"cancel_gate"));
        var worker=new Thread(()=>
        {
            try{entered.Wait(lifetime.Token);engine.Cancel();}
            catch(OperationCanceledException) when(lifetime.IsCancellationRequested) { }
            catch(Exception error){workerError=error;}
        }){IsBackground=true,Name="VM review canceller"};
        worker.Start();
        try
        {
            var result=engine.TryRunString("global cancel_gate: function() while true do pcall(function() xpcall(function() cancel_gate() while true do end end, function(e: any): any return e end) end) end");
            Assert.True(worker.Join(TimeSpan.FromSeconds(5)),"Cancellation worker did not settle");
            Assert.Null(workerError);
            Assert.Equal(AbortReason.Cancelled,Assert.IsType<RunAborted>(result.Value).Reason);
            Assert.Equal(42,engine.RunString("return 42")[0].I);
        }
        finally
        {
            lifetime.Cancel();
            Assert.True(worker.Join(TimeSpan.FromSeconds(5)),"Cancellation worker survived cleanup");
        }
    }

    [Fact]
    public void Compiler_line_tables_cover_instructions_and_lookup_is_total()
    {
        using var engine=new ScriptEngine();
        Gen.Int[1,100].Sample(n=>
        {
            var proto=engine.Compile($"local function f(x: integer): integer\n if x > {n} then return x + 1 end\n return x - 1\nend\nreturn f({n})","lines.tl");
            Visit(proto);
        },iter:100,threads:1,seed:Properties.Prop.Seeds.CsCheckSeed);
        static void Visit(Proto proto)
        {
            Assert.Equal(proto.Code.Length,proto.Lines.Length);
            Assert.All(proto.Lines,line=>Assert.InRange(line,1,5));
            Assert.All(proto.Code,i=>Assert.True(Enum.IsDefined(i.Op)));
            for(int pc=0;pc<proto.Code.Length;pc++)Assert.Equal(proto.Lines[pc],proto.LineOf(pc));
            foreach(int pc in new[]{int.MinValue,-1,proto.Code.Length,int.MaxValue})Assert.Equal(proto.LineDefined,proto.LineOf(pc));
            foreach(var child in proto.Protos)Visit(child);
        }
    }
}

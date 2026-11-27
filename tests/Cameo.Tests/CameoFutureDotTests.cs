using Cameo;
using CsCheck;
using Cameo.Jit;
using Cameo.Runtime.Vm;
using Cameo.Tests.Properties;
using Xunit;

namespace Cameo.Tests;

/// <summary>Direct Cameo dot syntax changes argument layout, never lookup or callable identity.</summary>
public class CameoFutureDotTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Direct_future_calls_keep_string_and_table_argument_forms(bool check)
    {
        using var engine = new CameoEngine(new CameoRuntimeOptions { TypeCheck = check });
        var values = engine.RunString("""
            let text = Future.completedFuture('ready')
            let current = text.getNow 'fallback'
            let record = Future.completedFuture({answer = 42})
            let completedAgain = record.complete {answer = 42}
            return current, completedAgain, record.get().answer
            """);
        Assert.Equal("ready", values[0].AsString);
        Assert.False(values[1].IsTruthy);
        Assert.Equal(42, values[2].I);
        using var teal = new ScriptEngine(new CameoRuntimeOptions { TypeCheck = check });
        foreach (string legacy in new[] {
            "local text = Future.completedFuture('ready') return text.getNow 'fallback'",
            "local record = Future.completedFuture({answer = 42}) return record.complete {answer = 42}"
        })
            Assert.IsNotType<RunCompleted>(teal.TryRunString(legacy).Value);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Direct_future_call_compiles_with_jit(bool check, bool compiled)
    {
        using var engine = new CameoEngine(new CameoRuntimeOptions { TypeCheck = check });
        JitLibrary.Enable(engine.Runtime);
        var values = engine.RunString("""
            fn wait(task: Future<integer>) -> integer { return task.get() }
            return wait, Future.completedFuture(42)
            """);
        Value function = values[0];
        if (compiled)
        {
            Assert.True(JitLibrary.TryCompile(function, out var lowered, out var reason), reason?.ToString());
            function = lowered;
        }
        Assert.Equal(42, engine.State.Call(function, values[1])[0].I);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Direct_future_dot_checked_and_unchecked(bool check)
    {
        using var engine = new CameoEngine(new CameoRuntimeOptions { TypeCheck = check });
        var values = engine.RunString("let f = Future.completedFuture(42) return f.get(), f.isDone()");
        Assert.Equal(42, values[0].I);
        Assert.True(values[1].IsTruthy);
    }

    [Theory]
    [InlineData("let f = Future.completedFuture(42) return f.get()", "42")]
    [InlineData("let f = Future.parallel(() => 42) return f.get()", "42")]
    [InlineData("return Future.parallel(() => Future.completedFuture(42).get()).get()", "42")]
    [InlineData("let a = Future.completedFuture(12) let b = Future.completedFuture(30) return a.thenCombine(b, (x, y) => x + y).get()", "42")]
    [InlineData("let f = Future.completedFuture(42) let g = f.get return g(f), (f.get)(f), f['get'](f), f.get == Future.get", "42|42|42|true")]
    [InlineData("let F = Future let f = F.completedFuture(42) return F.get(f), F.isDone(f)", "42|true")]
    [InlineData("let f = Future.completedFuture(42) let other = Future.completedFuture(7) let ok = pcall(() => f.get(other)) return ok", "false")]
    [InlineData("fn pair(a, b) { return a, b } let t = { get = pair } return t.get(12, 30)", "12|30")]
    [InlineData("let fake = {get = Future.get} let ok = pcall(() => fake.get()) return ok", "false")]
    [InlineData("let f = Future.completedFuture(42) Future.get = (x) => x return f.get(17)", "17")]
    [InlineData("let f = Future.completedFuture(42) Future.alias = Future.get return f.alias()", "42")]
    [InlineData("let f = Future.completedFuture(0) Future.make = Future.completedFuture return f.make(42).get()", "42")]
    [InlineData("fn countArgs(...) { return select('#', ...), ... } let t = {f = countArgs} fn many() { return 12, 30 } return t.f(many())", "2|12|30")]
    [InlineData("fn countArgs(...) { return select('#', ...), ... } let t = {f = countArgs} fn none() { } return t.f(17, none())", "1|17")]
    [InlineData("let f = Future.completedFuture(42) fn none() { } return f.get(none())", "42")]
    [InlineData("var n = 0 let f = Future.completedFuture(42) fn receiver() { n = n + 1 return f } fn arg() { n = n * 10 + 2 return 0 } let x = receiver().get(arg()) return x, n", "42|12")]
    [InlineData("let f = Future.delay(0, 42) return f.get()", "42")]
    [InlineData("let f = Future.failedFuture('oops') let value, err = f.await() return value, err.kind", "nil|failed")]
    [InlineData("let f = Future.new() f.cancel() let value, err = f.await() return value, err.kind, f.isCancelled()", "nil|cancelled|true")]
    [InlineData("let f = Future.new().orTimeout(0) let value, err = f.await() return value, err.kind", "nil|timeout")]
    [InlineData("let f = Future.new().completeOnTimeout(42, 0) return f.get()", "42")]
    [InlineData("let f = Future.completedFuture(40).thenApply((x) => x + 2) return f.get()", "42")]
    [InlineData("let f = Future.delay(0, 42) return pcall(() => f.get())", "true|42")]
    [InlineData("let f = Future.supplyAsync(() => Future.delay(0, 42).get()) return f.get()", "42")]
    [InlineData("let f = Future.failedFuture('bad').exceptionally((e) => 42) return f.get()", "42")]
    [InlineData("let f = Future.completedFuture(40).thenCompose((x) => Future.completedFuture(x + 2)) return f.get()", "42")]
    [InlineData("let f = Future.failedFuture('bad') let ok, err = pcall(() => f.get()) return ok, err.kind", "false|failed")]
    [InlineData("let q = Query.from({12, 30}) return q:sum(), json.decode('42')", "42|42|nil")]
    public void Source_contracts(string source, string expected)
    {
        using var engine = new CameoEngine(new CameoRuntimeOptions { TypeCheck = false });
        var values = engine.RunString(source);
        Assert.Equal(expected, string.Join('|', values.Select(v => v.ToString())));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Explicit_Teal_dot_remains_unbound(bool check)
    {
        using var engine = new ScriptEngine(new CameoRuntimeOptions { TypeCheck = check });
        var values = engine.RunString("local a=Future.completedFuture(12) local b=Future.completedFuture(30) return a.get(b),a:get(),Future.get(a)");
        Assert.Equal(new long[] {30,12,12}, values.Select(v=>v.I));
    }

    [Fact]
    public void Native_name_does_not_mark_a_replacement_function()
    {
        using var engine=new CameoEngine(new CameoRuntimeOptions {TypeCheck=false});
        engine.State.Register(engine.State.GetGlobal("Future").AsTable,"get",static (ref CallContext c)=>c.Return(c.Count,c[0]));
        var values=engine.RunString("let f = Future.completedFuture(42) return f.get(17)");
        Assert.Equal(1,values[0].I);
        Assert.Equal(17,values[1].I);
    }

    [Fact]
    public void Ordinary_lookup_runs_once_and_keeps_registered_identity()
    {
        using var engine=new CameoEngine(new CameoRuntimeOptions {TypeCheck=false});
        var values=engine.RunString("var n = 0 let get = Future.get let f = Future.completedFuture(42) debug.getmetatable(f).__index = (obj, key) => { n = n + 1 return get } let x = f.get() return x, n, f.get == get");
        Assert.Equal(42,values[0].I);
        Assert.Equal(1,values[1].I);
        Assert.True(values[2].IsTruthy);
    }

    [Theory]
    [InlineData("fn f(x) { return x.get() }", "f(Future.completedFuture(42))", "42")]
    [InlineData("fn f(x) { let n = x.get() return n + 1 }", "f(Future.completedFuture(41))", "42")]
    [InlineData("fn f(t) { return t.f(12, 30) }", "f({f = (a, b) => a + b})", "42")]
    [InlineData("fn many() { return 12, 30 } fn f(t) { return t.f(many()) }", "f({f = (a, b) => a + b})", "42")]
    [InlineData("fn none() { } fn countArgs(...) { return select('#', ...), ... } fn f(t) { return t.f(42, none()) }", "f({f = countArgs})", "1|42")]
    [InlineData("fn none() { } fn f(x) { return x.get(none()) }", "f(Future.completedFuture(42))", "42")]
    [InlineData("fn f(t) { let x = 1 let y = 2 t.f(x, y) return x + y }", "f({f = (a, b) => { assert(a == 1 && b == 2) }})", "3")]
    [InlineData("fn f(x, y, cb) { return x.thenCombine(y, cb).get() }", "f(Future.completedFuture(12), Future.completedFuture(30), (a, b) => a + b)", "42")]
    [InlineData("fn f(n, x) { if n == 0 { return x.get() } return f(n - 1, x) }", "f(200, Future.completedFuture(42))", "42")]
    public void Interpreted_and_real_IL_agree(string definition, string invocation, string expected)
    {
        foreach (bool compiled in new[] {false,true})
        {
            using var engine = new CameoEngine(new CameoRuntimeOptions { TypeCheck = false });
            JitLibrary.Enable(engine.Runtime);
            var values = engine.RunString(definition + (compiled ? " f = assert(jit.compile(f)) " : " ") + "return " + invocation);
            Assert.Equal(expected,string.Join('|',values.Select(v=>v.ToString())));
        }
    }

    [Fact]
    public void Register_preparation_matches_argument_model()
    {
        Prop.Cs(Gen.Select(Gen.Int[0,8], Gen.Int[0,30], Gen.Bool), input =>
        {
            var (offset,count,bind)=input;
            using var engine = new CameoEngine(new CameoRuntimeOptions { TypeCheck=false });
            Value function = engine.State.GetGlobal("Future").AsTable.GetStr(bind ? "get" : "new");
            Value receiver = engine.RunString("return Future.completedFuture(42)")[0];
            Value[] slots=Enumerable.Range(0,offset+count+6).Select(i=>Value.FromInt(1000+i)).ToArray();
            slots[offset]=function; slots[offset+1]=receiver;
            Value[] before=slots.ToArray();
            int end=offset+count+2;
            int top=DotCallPreparation.PrepareRegisters(slots,offset,end);
            Assert.Equal(end-(bind ? 0:1),top);
            Assert.Equal(before.Take(offset+1),slots.Take(offset+1));
            var expected=before.Skip(offset+(bind ? 1:2)).Take(count+(bind ? 1:0));
            Assert.Equal(expected,slots.Skip(offset+1).Take(top-offset-1));
            Assert.Equal(before.Skip(end),slots.Skip(end));
            if(!bind) Assert.True(slots[end-1].IsNil);
            Assert.Equal(slots.Skip(offset+1).Take(top-offset-1),DotCallPreparation.PrepareArguments(function,before.Skip(offset+1).Take(count+1).ToArray()));
        });
    }

    [Fact]
    public void Preparation_rejects_missing_receiver_and_invalid_bounds()
    {
        Assert.Throws<ArgumentException>(()=>DotCallPreparation.PrepareArguments(default, []));
        Assert.Throws<ArgumentOutOfRangeException>(()=>DotCallPreparation.PrepareRegisters(new Value[2],-1,2));
        Assert.Throws<ArgumentOutOfRangeException>(()=>DotCallPreparation.PrepareRegisters(new Value[2],0,3));
        Assert.Throws<ArgumentOutOfRangeException>(()=>DotCallPreparation.PrepareRegisters(new Value[2],0,1));
        Assert.Throws<ArgumentOutOfRangeException>(()=>DotCallPreparation.PrepareRegisters(new Value[2],2,1));
        Assert.Throws<ArgumentOutOfRangeException>(()=>DotCallPreparation.PrepareRegisters(new Value[2],int.MaxValue,int.MinValue));
        Assert.Throws<ArgumentOutOfRangeException>(()=>DotCallPreparation.PrepareRegisters(new Value[2],2,int.MinValue));
        Assert.Throws<ArgumentNullException>(()=>DotCallPreparation.PrepareRegisters(null!,0,2));
        Assert.Throws<ArgumentNullException>(()=>DotCallPreparation.PrepareArguments(default,null!));
    }

    [Fact]
    public void Exact_storage_end_and_nonzero_short_ranges_obey_the_public_contract()
    {
        using var engine = new CameoEngine(new CameoRuntimeOptions { TypeCheck = false });
        Value receiver = engine.RunString("return Future.completedFuture(42)")[0];
        Value method = engine.State.GetGlobal("Future").AsTable.GetStr("get");
        Value ordinary = engine.State.GetGlobal("math").AsTable.GetStr("abs");
        Value prefix = Value.FromInt(17);
        Value argument = Value.FromInt(42);

        Value[] kept = [prefix, method, receiver];
        Assert.Equal(kept.Length, DotCallPreparation.PrepareRegisters(kept, 1, kept.Length));
        Assert.Equal(new[] { prefix, method, receiver }, kept);
        Value[] dropped = [prefix, ordinary, receiver, argument];
        Assert.Equal(3, DotCallPreparation.PrepareRegisters(dropped, 1, dropped.Length));
        Assert.Equal(new[] { prefix, ordinary, argument, Value.Nil }, dropped);
        Value[] empty = [prefix, ordinary, receiver];
        Assert.Equal(2, DotCallPreparation.PrepareRegisters(empty, 1, empty.Length));
        Assert.Equal(new[] { prefix, ordinary, Value.Nil }, empty);

        // A real bound method must not bypass admission by observing a receiver outside
        // the supplied occupied span, even when that cell exists in the backing array.
        foreach (int offset in new[] { 1, 2, 7 })
        foreach (int count in new[] { 0, 1 })
        {
            Value[] invalid = Enumerable.Repeat(prefix, offset + 4).ToArray();
            invalid[offset] = method;
            invalid[offset + 1] = receiver;
            Value[] before = invalid.ToArray();
            var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
                DotCallPreparation.PrepareRegisters(invalid, offset, offset + count));
            Assert.Equal("end", error.ParamName);
            Assert.Equal(before, invalid);
        }
    }

    [Fact]
    public void Counterfeit_userdata_and_nonfunction_values_do_not_bind()
    {
        using var engine=new CameoEngine(new CameoRuntimeOptions {TypeCheck=false});
        Value method=engine.State.GetGlobal("Future").AsTable.GetStr("get");
        Value receiver=engine.RunString("return Future.completedFuture(42)")[0];
        Value argument=Value.FromInt(17);
        Assert.Equal(new[]{argument},DotCallPreparation.PrepareArguments(method,[Value.FromUserdata(new object()),argument]));
        Assert.Equal(new[]{argument},DotCallPreparation.PrepareArguments(Value.FromUserdata(method.O!),[receiver,argument]));
        Assert.Equal(new[]{argument},DotCallPreparation.PrepareArguments(default,[receiver,argument]));
    }
}

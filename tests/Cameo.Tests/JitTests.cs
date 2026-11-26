using System.Globalization;
using CsCheck;
using Cameo.Jit;
using Cameo.Runtime.Vm;
using Xunit;

namespace Cameo.Tests;

/// <summary>The IL target: a compiled function must do exactly what the interpreted one does.</summary>
public class JitTests
{
    private static ScriptEngine Engine(CameoRuntimeOptions? options = null)
    {
        var e = new ScriptEngine(options ?? new CameoRuntimeOptions { TypeCheck = false });
        JitLibrary.Enable(e);
        return e;
    }

    // Runs `body` (which defines a global function f and returns arguments for it) and gives the
    // outcome of calling f interpreted and compiled, as text.
    private static (string Interpreted, string Compiled) Both(string definition, string call)
    {
        string Run(bool compiled)
        {
            using ScriptEngine e = Engine();
            var output = new StringWriter { NewLine = "\n" };
            e.State.Stdout = output;
            string source = definition + "\n" + (compiled ? "f = assert(jit.compile(f))\n" : "") + $"print(pcall(function() return {call} end))";
            RunResult r = e.TryRunString(source, "t");
            return r is RunCompleted ? output.ToString() : r is CompileFailed c ? "compile: " + c.Diagnostics[0] : r is RuntimeFailed f ? "error: " + f.Message : r.ToString()!;
        }
        return (Run(false), Run(true));
    }

    private static void Same(string definition, string call)
    {
        var (interpreted, compiled) = Both(definition, call);
        // the interpreter also names the operand ("(local 'b')"); compiled code does not
        interpreted = System.Text.RegularExpressions.Regex.Replace(interpreted, @" \((local|field|global|upvalue|constant|method) '[^']*'\)", "");
        Assert.Equal(interpreted, compiled);
        Assert.DoesNotContain("compile:", interpreted);
        Assert.DoesNotContain("was not compiled", compiled);
    }

    [Theory]
    // arithmetic of every kind, integer and float and mixed, with wrap-around and division edge cases
    [InlineData("function f(a, b) return a + b, a - b, a * b, a / b, a % b, a // b, a ^ b, -a end", "f(7, 2), f(-7, 2), f(7.5, -2), f(7, 2.5), f(math.maxinteger, 2), f(math.mininteger, -1), f(1, 0.0)")]
    [InlineData("function f(a, b) return a & b, a | b, a ~ b, a << b, a >> b, ~a end", "f(0xF0F0, 12), f(-1, 63), f(1, 64), f(3.0, 1)")]
    [InlineData("function f(a, b) return a == b, a ~= b, a < b, a <= b, a > b, a >= b end", "f(1, 2), f(2, 2), f(2.0, 2), f(1.5, 1), f('a', 'b'), f('b', 'a'), f(math.maxinteger, math.maxinteger + 0.0)")]
    [InlineData("function f(a, b) return not a, (a and b) ~= nil, type(a or b), #b end", "f(nil, 'xy'), f(false, {1, 2, 3}), f(0, ''), f(1, 'abc')")]
    [InlineData("function f(a, b) return a .. b, a .. 1 .. b .. 2.5 end", "f('x', 'y'), f(1, 2), f('', '')")]
    // control flow
    [InlineData("function f(n) local s = 0 for i = 1, n do s = s + i end return s end", "f(0), f(1), f(100), f(-5)")]
    [InlineData("function f(a, b, c) local s = 0 for i = a, b, c do s = s + i end return s end", "f(1, 10, 3), f(10, 1, -3), f(1, 0, 1), f(1.5, 4, 0.5), f(4, 1, -0.5), f(math.maxinteger - 2, math.maxinteger, 1), f(1, 3, 1.0)")]
    [InlineData("function f(n) local s, i = 0, 0 while i < n do i = i + 1 if i % 3 == 0 then goto continue end s = s + i ::continue:: end return s, i end", "f(0), f(10), f(11)")]
    [InlineData("function f(n) local i = 0 repeat i = i + 2 until i >= n return i end", "f(0), f(7)")]
    [InlineData("function f(a, b) if a < b then return 'lt' elseif a == b then return 'eq' else return 'gt' end end", "f(1, 2), f(2, 2), f(3, 2), f('a', 'b')")]
    [InlineData("function f(n) local s = 0 for i = 1, n do for j = i, n do if (i + j) % 2 == 0 then s = s + i * j end end end return s end", "f(0), f(1), f(12)")]
    // tables, fields, methods, globals, upvalues
    [InlineData("function f(n) local t = {} for i = 1, n do t[i] = i * i end local s = 0 for i = 1, #t do s = s + t[i] end return s, #t end", "f(0), f(10)")]
    [InlineData("function f(p) p.x = p.x + 1 p.name = p.name .. '!' return p.x, p.name, p.missing end", "f({x = 1, name = 'a'}), f({x = 2.5, name = ''})")]
    [InlineData("function f(a, b) local t = {a, b, 3, k = a} return t[1], t[2], t[3], t.k, #t end", "f(1, 2), f('x', nil)")]
    [InlineData("function f(s) return s:upper(), s:len(), s:sub(2, 3) end", "f('hello'), f('')")]
    [InlineData("counter = 0 function f(n) for i = 1, n do counter = counter + i end return counter end", "f(3), f(4), counter")]
    [InlineData("local up = 10 function f(n) up = up + n return up end", "f(1), f(2)")]
    [InlineData("function f(a) return math.sin(a), math.sqrt(a), math.floor(a), math.max(a, 2), math.abs(-a) end", "f(1), f(0.25), f(9)")]
    [InlineData("function f(a) local x = FloatArray.new(5) for i = 1, 5 do x[i] = a * i end return x[1], x[5], x[6], x:sum() end", "f(1.5), f(2)")]
    // calls: none, one and several results, several arguments, recursion through the global
    [InlineData("function g(a, b, c, d) return a + b + c + d, a * d end function f(x) local s, p = g(x, 1, 2, 3) local q = g(1, 1, 1, x) return s, p, q end", "f(1), f(2.5)")]
    [InlineData("function f(n) if n < 2 then return n end return f(n - 1) + f(n - 2) end", "f(0), f(1), f(15)")]
    [InlineData("function f(n, acc) if n == 0 then return acc end return f(n - 1, acc + n) end", "f(0, 0), f(100, 0)")]
    [InlineData("function f(t) table.sort(t) table.insert(t, 99) return table.concat(t, ','), select('#', table.unpack(t)) end", "f({3, 1, 2}), f({})")]
    // a variable number of values: passed on, returned, collected
    [InlineData("function g(n) if n == 0 then return end if n == 1 then return 'a' end return 'a', 'b', 'c' end function f(n) local t = {0, g(n)} return #t, select('#', g(n)), select('#', 1, g(n)), g(n) end", "f(0), f(1), f(3)")]
    [InlineData("function g(...) return select('#', ...), ... end function f(a, b) return g(a, g(b, g())) end", "f(1, 2), f(nil, nil)")]
    [InlineData("function f(s) return s:rep(2), s:byte(1, -1) end", "f('ab'), f('')")]
    // metamethods take the same paths as in the interpreter
    [InlineData("local mt = {__add = function(a, b) return 'add' end, __index = function(t, k) return k .. '?' end, __lt = function() return true end, __len = function() return 7 end, __eq = function() return true end, __call = function(self, x) return x * 2 end, __concat = function() return 'cat' end, __unm = function() return 'neg' end}\nfunction f(a, b) return a + b, a.zz, a < b, #a, a == b, a(21), a .. b, -a, b + a end", "f(setmetatable({}, mt), setmetatable({}, mt))")]
    // errors are errors in both, with the line of the operation
    [InlineData("function f(a, b)\n  local x = 1\n  return a + b\nend", "f(1, {}), f(nil, 1), f('x', 1)")]
    [InlineData("function f(a)\n  return a.x.y\nend", "f({}), f(1), f(nil)")]
    [InlineData("function f(a)\n  for i = 1, a do end\n  return 1 // a, 1 % a\nend", "f('x'), f(0), f({})")]
    [InlineData("function f(a) return a < 1, a() end", "f({}), f('s'), f(nil)")]
    [InlineData("function f(a, b, c) for i = a, b, c do end return 1 end", "f(1, 2, 0), f(1, 'x', 1), f({}, 2, 1)")]
    public void Compiled_code_does_what_the_interpreter_does(string definition, string calls)
    {
        // each call on its own, so that an error in one does not hide the others
        foreach (string call in SplitCalls(calls)) Same(definition, call);
    }

    private static IEnumerable<string> SplitCalls(string calls)
    {
        int depth = 0, start = 0;
        for (int i = 0; i < calls.Length; i++)
        {
            char c = calls[i];
            if (c is '(' or '{') depth++;
            else if (c is ')' or '}') depth--;
            else if (c == ',' && depth == 0) { yield return calls[start..i].Trim(); start = i + 1; }
        }
        yield return calls[start..].Trim();
    }

    [Fact]
    public void Errors_name_the_line_they_happened_on()
    {
        var (interpreted, compiled) = Both("function f(a)\n  local x = 1\n  local y = x + a\n  return y\nend", "f({})");
        Assert.Contains("t:3:", interpreted);
        Assert.Contains("t:3:", compiled);
        Assert.Contains("attempt to perform arithmetic on a table value", compiled);
    }

    [Theory]
    [InlineData("function f(a, ...) return a, select('#', ...), ... end", "f(10, 20, 30)")]
    [InlineData("function f(...) local a, b = ... return a, b end", "f(7)")]
    [InlineData("function f(a) local x = a local g = function(d) x = x + d return x end return g(3), g(4), x end", "f(10)")]
    [InlineData("function f(t) local s = 0 for _, v in ipairs(t) do s = s + v end return s end", "f({10, 20, 30, 40})")]
    [InlineData("function f(t) local n = 0 for k, v in pairs(t) do n = n + v end return n end", "f({a = 2, b = 5, c = 8})")]
    [InlineData("function f(g) return g() end", "f(function() return 1, 2, 3 end)")]
    [InlineData("function f(g) return 1, g() end", "f(function() return 2, 3 end)")]
    [InlineData("function f(g) local t = {g()} return #t, t[2] end", "f(function() return 9, 8 end)")]
    [InlineData("function f() local log = {} do local x <close> = setmetatable({}, {__close = function(_, e) log[#log+1] = 'closed:' .. tostring(e) end}) log[#log+1] = 'body' end return table.concat(log, ',') end", "f()")]
    [InlineData("function f() local log = {} local ok, err = pcall(function() local x <close> = setmetatable({}, {__close = function(_, e) log[#log+1] = 'closed:' .. tostring(e) end}) error('boom', 0) end) return ok, err, table.concat(log, ',') end", "f()")]
    public void Varargs_closures_generic_for_and_close_compile_and_match_interpreter(string definition, string call)
    {
        var (interpreted, compiled) = Both(definition, call);
        Assert.Equal(interpreted, compiled);
    }

    [Fact]
    public void Compiled_close_unwinds_in_reverse_order_on_error()
    {
        string def = """
            local log = {}
            local function mk(name)
               return setmetatable({}, { __close = function(_, e) log[#log + 1] = name .. ':' .. tostring(e) end })
            end
            function f(fail)
               local a <close> = mk('a')
               local b <close> = mk('b')
               if fail then error('boom', 0) end
               return 'ok'
            end
            """;
        using ScriptEngine e = Engine();
        e.RunString(def + "\nf = assert(jit.compile(f))");
        Value[] r = e.RunString("local ok, err = pcall(f, true) return ok, err");
        Assert.False(r[0].IsTruthy);
        Assert.Equal("boom", r[1].ToText());
        using ScriptEngine e2 = Engine();
        Value[] r2 = e2.RunString("""
            local log = {}
            local function mk(name)
               return setmetatable({}, { __close = function(_, e) log[#log + 1] = name .. ':' .. tostring(e) end })
            end
            local function f(fail)
               local a <close> = mk('a')
               local b <close> = mk('b')
               if fail then error('boom', 0) end
               return 'ok'
            end
            f = assert(jit.compile(f))
            local ok, err = pcall(f, true)
            return ok, err, table.concat(log, ',')
            """);
        Assert.False(r2[0].IsTruthy);
        Assert.Equal("boom", r2[1].ToText());
        Assert.Equal("b:boom,a:boom", r2[2].ToText());
    }

    [Fact]
    public void Automatic_hot_function_tiering_compiles_and_matches_results()
    {
        using ScriptEngine e = new(new CameoRuntimeOptions { JitTierThreshold = 5 });
        Cameo.Jit.JitLibrary.Enable(e);
        Value[] r = e.RunString("""
            local function add(a: integer, b: integer): integer
               local s = 0
               for i = 1, b do s = s + a end
               return s
            end
            local total = 0
            for i = 1, 20 do total = total + add(i, 3) end
            return total, jit.auto()
            """);
        Assert.Equal(630, r[0].I);
        Assert.Equal(5, r[1].I);
    }

    [Fact]
    public void Cameo_engine_enables_jit_and_compiles_closures_and_using()
    {
        using var cameo = new Cameo.CameoEngine();
        Value[] r = cameo.RunString("""
            fn makeAdder(base: integer) -> function(integer): integer {
                return (x: integer) => base + x
            }
            let compiled = assert(jit.compile(makeAdder))
            let add10 = compiled(10)
            return add10(32)
            """);
        Assert.Equal(42, r[0].I);
    }

    [Fact]
    public void Invalid_bytecode_is_refused_by_compiler_and_validator()
    {
        var bad = new Proto
        {
            Name = "corrupt",
            MaxStack = 2,
            Code = [new Instruction(OpCode.Move, 99, 0)],
            Lines = [1],
        };
        Assert.NotNull(BytecodeValidator.Validate(bad));
        Assert.False(Cameo.Jit.IlCompiler.TryCompile(bad, out _, out var err));
        Assert.NotNull(err);
        Assert.Equal(Cameo.Jit.JitErrorKind.Unsupported, err!.Kind);
    }

    [Fact]
    public void Only_Teal_functions_can_be_compiled()
    {
        using ScriptEngine e = Engine();
        Value[] r = e.RunString("local a, e1 = jit.compile(print) local b, e2 = jit.compile(42) return a, e1.kind, b, e2.kind, e2.message, jit.available");
        Assert.True(r[0].IsNil);
        Assert.Equal("not_a_function", r[1].ToText());
        Assert.Equal("not_a_function", r[3].ToText());
        Assert.Contains("number", r[4].ToText());
        Assert.True(r[5].IsTruthy);
    }

    [Fact]
    public void A_compiled_loop_is_stopped_by_the_time_limit_and_by_cancel()
    {
        foreach (string source in new[]
        {
            "local function spin() while true do end end  assert(jit.compile(spin))()",
            "local function spin() for i = 1, math.maxinteger do end end  assert(jit.compile(spin))()",
            // an abort raised inside an interpreted function passes through the compiled caller
            "local function inner() while true do end end  local function outer() inner() return 1 end  assert(jit.compile(outer))()",
        })
        {
            using ScriptEngine e = Engine(new CameoRuntimeOptions { TypeCheck = false, Timeout = TimeSpan.FromMilliseconds(200) });
            var aborted = Assert.IsType<RunAborted>(e.TryRunString(source).Value);
            Assert.Equal(AbortReason.Timeout, aborted.Reason);
            Assert.IsType<RunCompleted>(e.TryRunString("return 1").Value);          // still usable
        }
    }

    [Fact]
    public void Compiled_recursion_uses_frames_instead_of_native_call_depth()
    {
        using ScriptEngine e = Engine();
        e.State.Limits.MaxNativeDepth = 10;
        Value[] r = e.RunString("""
            function depth(n) if n == 0 then return 0 end return 1 + depth(n - 1) end
            local interpreted = depth(5000)
            depth = assert(jit.compile(depth))
            local ok, err = pcall(depth, 5000)
            return interpreted, depth(3), ok, err
            """);
        Assert.Equal(5000, r[0].I);
        Assert.Equal(3, r[1].I);
        Assert.True(r[2].IsTruthy);
        Assert.Equal(5000, r[3].I);
    }

    [Fact]
    public void Compiled_mutual_and_tail_recursion_preserve_results()
    {
        using ScriptEngine e = Engine();
        e.State.Limits.MaxNativeDepth = 10;
        Value[] r = e.RunString("""
            function even(n) if n == 0 then return true, 'done' end return odd(n - 1) end
            function odd(n) if n == 0 then return false, 'done' end return even(n - 1) end
            even = assert(jit.compile(even)) odd = assert(jit.compile(odd))
            function sum(n) if n == 0 then return 0 end return n + sum(n - 1) end
            sum = assert(jit.compile(sum))
            return sum(6000), even(10001)
            """);
        Assert.Equal(18_003_000, r[0].I);
        Assert.False(r[1].IsTruthy);
        Assert.Equal("done", r[2].ToText());
    }

    [Fact]
    public void Compiled_frames_preserve_loops_open_results_and_rebinding()
    {
        using ScriptEngine e = Engine();
        Value[] r = e.RunString("""
            function pair(n) return n, n + 1 end
            pair = assert(jit.compile(pair))
            function work(n)
                local s = 0
                for i = 1, n do
                    for j = 1, 3 do local a, b = pair(i * j) s = s + a + b end
                end
                local t = {s, pair(7)}
                return t[1], t[2], t[3], pair(8)
            end
            work = assert(jit.compile(work))
            local a,b,c,d,e = work(10)
            pair = function(n) return 2 * n, 3 * n end
            local x = work(1)
            return a,b,c,d,e,x
            """);
        Assert.Equal(new long[] { 690, 7, 8, 8, 9, 30 }, r.Select(v => v.I));
    }

    [Fact]
    public void Compiled_frame_budget_and_errors_leave_engine_usable()
    {
        using ScriptEngine e = Engine();
        e.State.Limits.MaxStackSlots = 4096;
        Value[] r = e.RunString("""
            function depth(n) if n == 0 then return 0 end return 1 + depth(n - 1) end
            depth = assert(jit.compile(depth))
            local ok, err = pcall(depth, 10000)
            return ok, tostring(err), depth(12)
            """);
        Assert.False(r[0].IsTruthy);
        Assert.Contains("stack overflow", r[1].ToText());
        Assert.Equal(12, r[2].I);
        Assert.Equal(42, e.RunString("return 42")[0].I);
    }

    [Fact]
    public void Frame_budget_charges_generated_loop_snapshots()
    {
        using ScriptEngine engine = Engine();
        engine.State.Limits.MaxStackSlots = 512;
        string loops = string.Concat(Enumerable.Repeat("for i = 1, 1 do local x = i end\n", 40));
        engine.RunString("function depth(n)\n" + loops + "if n == 0 then return 0 end return 1 + depth(n - 1) end\ndepth = assert(jit.compile(depth))");
        Value[] values = engine.RunString("local ok,err = pcall(depth, 4) return ok,tostring(err)");
        Assert.False(values[0].IsTruthy);
        Assert.Contains("stack overflow", values[1].ToText());
        Assert.Equal(42, engine.RunString("return 42")[0].I);
    }

    [Fact]
    public void Tail_calls_do_not_accumulate_compiled_frames_under_a_small_budget()
    {
        using ScriptEngine engine = Engine();
        engine.State.Limits.MaxStackSlots = 256;
        engine.State.Limits.MaxNativeDepth = 10;
        Value[] values = engine.RunString("""
            function even(n) if n == 0 then return true end return odd(n - 1) end
            function odd(n) if n == 0 then return false end return even(n - 1) end
            even = assert(jit.compile(even)) odd = assert(jit.compile(odd))
            return even(100000)
            """);
        Assert.True(values[0].IsTruthy);
    }

    [Fact]
    public void Compiled_zero_and_short_results_are_padded_without_leaking_old_values()
    {
        using ScriptEngine engine = Engine();
        Value[] values = engine.RunString("""
            function none() return end
            function one() return 7 end
            none = assert(jit.compile(none)) one = assert(jit.compile(one))
            function outer()
                local a,b,c = one()
                local x,y = none()
                return a,b,c,x,y,select('#',none())
            end
            outer = assert(jit.compile(outer))
            return outer()
            """);
        Assert.Equal(6, values.Length);
        Assert.Equal(7, values[0].I);
        Assert.All(values[1..5], value => Assert.True(value.IsNil));
        Assert.Equal(0, values[5].I);
    }

    [Fact]
    public void Direct_compiled_body_obeys_exact_budget_boundary_and_recovers()
    {
        using ScriptEngine engine = Engine();
        Value original = engine.RunString("return function() return 42 end")[0];
        var closure = Assert.IsType<Closure>(original.O);
        Assert.True(IlCompiler.TryCompile(closure.Proto, out CompiledBody? body, out _));
        var program = Assert.IsType<CompiledProgram>(body!.Target);
        engine.State.Limits.MaxStackSlots = program.Slots;
        Assert.Equal(1, body(engine.State, engine.State.MainThread, 0, 0, closure, closure.Proto.K));
        engine.State.Limits.MaxStackSlots = program.Slots - 1;
        Assert.Throws<LuaException>(() => body(engine.State, engine.State.MainThread, 0, 0, closure, closure.Proto.K));
        engine.State.Limits.MaxStackSlots = program.Slots;
        Assert.Equal(1, body(engine.State, engine.State.MainThread, 0, 0, closure, closure.Proto.K));
    }

    [Fact]
    public void Compiled_functions_work_as_callbacks_methods_and_in_coroutines()
    {
        using ScriptEngine e = Engine();
        Value[] r = e.RunString("""
            local function less(a, b) return a > b end
            local t = {3, 1, 2}
            table.sort(t, assert(jit.compile(less)))
            local P = {}
            P.__index = P
            function P.norm2(self) return self.x * self.x + self.y * self.y end
            P.norm2 = assert(jit.compile(P.norm2))
            local p = setmetatable({x = 3, y = 4}, P)
            local function work(n) local s = 0 for i = 1, n do s = s + i end return s end
            work = assert(jit.compile(work))
            local co = coroutine.wrap(function() coroutine.yield(work(10)) coroutine.yield(work(100)) end)
            -- a compiled function cannot be yielded across: the yield is reported, not lost
            local function yielder() coroutine.yield(1) return 2 end
            local cy = assert(jit.compile(yielder))
            local ok, err = coroutine.resume(coroutine.create(function() return cy() end))
            return table.concat(t, ","), p:norm2(), co(), co(), ok, tostring(err)
            """);
        Assert.Equal("3,2,1", r[0].ToText());
        Assert.Equal(25, r[1].I);
        Assert.Equal(55, r[2].I);
        Assert.Equal(5050, r[3].I);
        Assert.False(r[4].IsTruthy);
        Assert.Contains("yield", r[5].ToText());
    }

    [Fact]
    public void The_typed_declarations_check()
    {
        using var e = new ScriptEngine();
        JitLibrary.Enable(e);
        Value[] r = e.RunString("""
            local function area(w: number, h: number): number return w * h end
            local fast, err = jit.compile(area)
            if not fast then error(err.message) end
            local ok, why = jit.supports(area)
            return fast(3, 4), ok, why
            """);
        Assert.True(r[0].TryToDouble(out double d) && d == 12);
        Assert.True(r[1].IsTruthy);
    }

    private static Gen<string> Pick(params string[] choices) => Gen.Int[0, choices.Length - 1].Select(i => choices[i]);

    // Property: random straight-line and looping arithmetic gives the same values, or the same
    // error, compiled and interpreted.
    [Fact]
    public void Random_arithmetic_programs_agree()
    {
        string[] binary = ["+", "-", "*", "/", "%", "//", "&", "|", "~", "<<", ">>", "^"];
        string[] compare = ["<", "<=", "==", "~=", ">", ">="];
        var genOperand = Gen.OneOf(
            Gen.Int[0, 5].Select(i => "v" + i),
            Gen.Long[-9, 9].Select(n => n.ToString(CultureInfo.InvariantCulture)),
            Pick("0.5", "-2.25", "3.0", "1e300", "math.maxinteger", "math.mininteger", "0", "0.0"));
        var genStatement = Gen.Select(Gen.Int[0, 5], genOperand, Pick(binary), genOperand, Gen.Int[0, 9], Pick(compare))
            .Select(t => t.Item5 switch
            {
                0 => $"if v{t.Item1} {t.Item6} {t.Item4} then v{t.Item1} = {t.Item2} end",
                1 => $"for i = 1, 3 do v{t.Item1} = v{t.Item1} {t.Item3} {t.Item4} end",
                2 => $"v{t.Item1} = -({t.Item2})",
                _ => $"v{t.Item1} = {t.Item2} {t.Item3} {t.Item4}",
            });
        var genArgs = Gen.OneOf(Gen.Long[-100, 100].Select(n => n.ToString(CultureInfo.InvariantCulture)),
            Gen.Double[-100, 100].Select(d => d.ToString("R", CultureInfo.InvariantCulture)),
            Pick("math.maxinteger", "math.mininteger", "0", "1")).Array[6];
        Gen.Select(genStatement.Array[1, 12], genArgs).Sample(t =>
        {
            string definition = "function f(v0, v1, v2, v3, v4, v5)\n  " + string.Join("\n  ", t.Item1) + "\n  return v0, v1, v2, v3, v4, v5\nend";
            var (interpreted, compiled) = Both(definition, $"f({string.Join(", ", t.Item2)})");
            return interpreted == compiled && !compiled.Contains("was not compiled");
        }, iter: 600, threads: 1,
            seed: Properties.Prop.Seeds.CsCheckSeed);
    }
}

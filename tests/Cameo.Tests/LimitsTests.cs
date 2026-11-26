using System.Text;
using Cameo.Runtime.Vm;
using Cameo.Runtime.Syntax;
using Xunit;

namespace Cameo.Tests;

/// <summary>
/// Every limit of the implementation, exercised at and beyond its bound. The rule under test:
/// exceeding a limit is a diagnostic or a Lua error (or, for the watchdog, a typed abort) —
/// never a crash of the host process, a hang, or a wrong answer — and the VM stays usable.
/// </summary>
public class LimitsTests
{
    private static ScriptEngine Engine(Action<CameoRuntimeOptions>? configure = null, bool typeCheck = false)
    {
        var o = new CameoRuntimeOptions { TypeCheck = typeCheck };
        configure?.Invoke(o);
        var e = new ScriptEngine(o);
        e.State.Stdout = new StringWriter();
        return e;
    }

    private static string Show(RunResult r) => r switch
    {
        RunCompleted c => "ok:" + string.Join("|", c.Values.Select(v => v.ToString())),
        CompileFailed f => "compile:" + f.Diagnostics[0].Message,
        RuntimeFailed f => "error:" + f.Message,
        RunAborted a => "aborted:" + a.Reason,
        RunExited x => "exit:" + x.Code,
    };

    private static RunCompleted Ok(RunResult r) =>
        r is RunCompleted c ? c : throw new Xunit.Sdk.XunitException("expected the run to complete, got " + Show(r));

    private static string Run(string src, Action<CameoRuntimeOptions>? configure = null, bool typeCheck = false) =>
        Show(Engine(configure, typeCheck).TryRunString(src, "limits"));

    /// <summary>Runs on a thread with a deliberately small native stack, as an embedding host might.</summary>
    private static string OnSmallStack(Func<string> f, int stackBytes = 512 * 1024)
    {
        string result = "";
        Exception? error = null;
        var t = new Thread(() => { try { result = f(); } catch (Exception e) { error = e; } }, stackBytes);
        t.Start();
        t.Join();
        if (error != null) throw new Xunit.Sdk.XunitException("unexpected exception: " + error);
        return result;
    }

    private static string Repeat(string s, int n) => new StringBuilder(s.Length * n).Insert(0, s, n).ToString();

    // ------------------------------------------------------------------ syntax nesting

    public static IEnumerable<object[]> NestingShapes() => new[]
    {
        new object[] { "parentheses", "return {0}1{1}", "(", ")" },
        new object[] { "table constructors", "return {0}1{1}", "{", "}" },
        new object[] { "unary operators", "return {0}1{1}", "- ", "" },
        new object[] { "not", "return {0}true{1}", "not ", "" },
        new object[] { "right-associative concat", "return {0}'x'{1}", "'a' .. ", "" },
        new object[] { "right-associative power", "return {0}2{1}", "1 ^ ", "" },
        new object[] { "blocks", "{0}local x = 1{1}", "do ", " end" },
        new object[] { "if statements", "{0}local x = 1{1}", "if true then ", " end" },
        new object[] { "loops", "{0}local x = 1{1}", "while false do ", " end" },
        new object[] { "function literals", "return {0}1{1}", "(function() return ", " end)()" },
        new object[] { "call arguments", "return {0}1{1}", "tostring(", ")" },
        new object[] { "index brackets", "local t = {{}} return {0}1{1}", "t[", "] or 1" },
    };

    [Theory]
    [MemberData(nameof(NestingShapes))]
    public void SyntaxNestingIsBoundedLikeLua(string what, string template, string open, string close)
    {
        _ = what;
        string At(int depth) => string.Format(template, Repeat(open, depth), Repeat(close, depth));
        // comfortably inside the bound: compiles and runs
        Assert.StartsWith("ok:", OnSmallStack(() => Run(At(60))));
        // beyond it: a diagnostic naming the limit, on a small stack too
        Assert.Equal("compile:chunk has too many syntax levels", OnSmallStack(() => Run(At(Parser.MaxSyntaxLevels + 50))));
        Assert.Equal("compile:chunk has too many syntax levels", OnSmallStack(() => Run(At(20000))));
    }

    [Fact]
    public void TypeNestingIsBounded()
    {
        string At(int depth) => $"local x: {Repeat("{", depth)}integer{Repeat("}", depth)} = nil\nreturn 1";
        Assert.Equal("ok:1", OnSmallStack(() => Run(At(50), typeCheck: true)));
        Assert.Equal("compile:chunk has too many syntax levels", OnSmallStack(() => Run(At(5000), typeCheck: true)));
        string Fn(int depth) => $"local f: {Repeat("function(): ", depth)}integer = nil\nreturn 1";
        Assert.Equal("compile:chunk has too many syntax levels", OnSmallStack(() => Run(Fn(5000), typeCheck: true)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LongOperatorChainsCompileOrAreRejectedButNeverCrash(bool typeCheck)
    {
        // left-associative chains are parsed iteratively, so they are not bounded by syntax
        // levels; the tree walkers are bounded by the available native stack instead
        string Chain(int n) => "return 0" + Repeat(" + 1", n);
        Assert.Equal("ok:2000", Run(Chain(2000), typeCheck: typeCheck));
        string deep = OnSmallStack(() => Run(Chain(300_000), typeCheck: typeCheck), 256 * 1024);
        Assert.True(deep == "ok:300000" || deep.StartsWith("compile:") && deep.Contains("too deeply nested"), deep);
        string calls = OnSmallStack(() => Run("local t = {} t.f = function() return t end\nreturn t" + Repeat(".f()", 100_000) + " == t"), 256 * 1024);
        Assert.True(calls == "ok:true" || calls.Contains("too deeply nested"), calls);
    }

    [Fact]
    public void LoadReportsNestingAsAnErrorValue()
    {
        string src = $"local f, err = load('return ' .. string.rep('(', 5000) .. '1' .. string.rep(')', 5000))\nreturn f == nil, err";
        string r = OnSmallStack(() => Run(src));
        Assert.StartsWith("ok:true|", r);
        Assert.Contains("too many syntax levels", r);
    }

    // ------------------------------------------------------------------ compiler sizes

    [Fact]
    public void LargeButLegalProgramsCompile()
    {
        var sb = new StringBuilder("local t = {");
        for (int i = 0; i < 100_000; i++) sb.Append(i).Append(',');
        sb.Append("}\nlocal s = 0 for i = 1, #t do s = s + t[i] end\nreturn #t, s");
        Assert.Equal("ok:100000|4999950000", Run(sb.ToString()));

        sb.Clear();
        for (int i = 0; i < 20_000; i++) sb.Append("local v").Append(i).Append(" = ").Append(i).Append('\n');
        sb.Append("return v0 + v19999");
        Assert.Equal("ok:19999", Run(sb.ToString()));

        sb.Clear();
        sb.Append("local s = 0\n");
        for (int i = 0; i < 30_000; i++) sb.Append("s = s + ").Append(i).Append(".5\n"); // 30,000 distinct constants
        sb.Append("return s");
        Assert.Equal("ok:450000000.0", Run(sb.ToString()));

        string lit = Repeat("x", 2_000_000);
        Assert.Equal("ok:2000000", Run($"return #'{lit}'"));

        sb.Clear();
        sb.Append("local function f(");
        for (int i = 0; i < 250; i++) sb.Append(i > 0 ? "," : "").Append('a').Append(i);
        sb.Append(") return a0 + a249 end\nreturn f(");
        for (int i = 0; i < 250; i++) sb.Append(i > 0 ? "," : "").Append(i);
        sb.Append(')');
        Assert.Equal("ok:249", Run(sb.ToString()));
    }

    [Fact]
    public void TooManyRegistersIsADiagnostic()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 70_000; i++) sb.Append("local v").Append(i).Append(" = 0\n");
        Assert.StartsWith("compile:function or expression too complex", Run(sb.ToString()));
    }

    // ------------------------------------------------------------------ recursion depth at run time

    [Fact]
    public void RecursionDepthIsBoundedByStackSlotsAndRecoverable()
    {
        const string probe = @"
            local depth = 0
            local function down(n) depth = n return 1 + down(n + 1) end
            local ok, err = pcall(down, 1)
            -- the VM must be fully usable after the overflow
            local function fib(n) if n < 2 then return n end return fib(n - 1) + fib(n - 2) end
            return ok, (tostring(err):gsub('^.-:%d+: ', '')), depth, fib(15)";
        var def = Ok(Engine().TryRunString(probe));
        Assert.Equal("false|stack overflow", $"{def.Values[0]}|{def.Values[1]}");
        long depth = def.Values[2].I;
        Assert.InRange(depth, 100_000, 400_000);   // default 1,000,000 slots; this frame uses 5
        Assert.Equal(610, def.Values[3].I);

        var small = Engine();
        small.State.Limits.MaxStackSlots = 10_000;
        var lim = Ok(small.TryRunString(probe));
        Assert.Equal("stack overflow", lim.Values[1].AsString);
        Assert.InRange(lim.Values[2].I, 1_000, 8_000);
        Assert.True(lim.Values[2].I < depth / 20);
    }

    [Fact]
    public void RecursionVariantsAllEndInStackOverflow()
    {
        string Probe(string body) => $@"
            local ok, err = pcall(function() {body} end)
            return ok, (tostring(err):gsub('^.-:%d+: ', ''))";
        Assert.Equal("ok:false|stack overflow", Run(Probe("local function a() return 1 + a() end return a()")));
        Assert.Equal("ok:false|stack overflow", Run(Probe("local a, b function a() return 1 + b() end function b() return 1 + a() end return a()")));
        Assert.Equal("ok:false|stack overflow", Run(Probe("local function m(self) return 1 + self:m() end return m({m = m})")));
        Assert.Equal("ok:false|stack overflow", Run(Probe("local function v(...) return 1 + v(1, ...) end return v()")));
        Assert.Equal("ok:false|stack overflow", Run(Probe("local t t = setmetatable({}, {__index = function(_, k) return 1 + t[k] end}) return t.x")));
        Assert.Equal("ok:false|stack overflow", Run(Probe("local t t = setmetatable({}, {__add = function(a, b) return 1 + (a + b) end}) return t + t")));
        Assert.Equal("ok:false|stack overflow", Run(Probe("local t t = setmetatable({}, {__call = function(self) return 1 + self() end}) return t()")));
        // recursion through pcall: the overflow is caught at the bottom and the callers fail one by one
        Assert.StartsWith("ok:false|", Run(Probe("local function p() return 1 + select(2, pcall(p)) end return p()")));
        Assert.Equal("ok:false|stack overflow", Run(Probe("local function p() local ok, e = pcall(p) if not ok then error(e, 0) end return 1 end return p()")));
        // inside a coroutine: the coroutine dies with the error, the caller carries on
        Assert.Equal("ok:false|stack overflow|dead", Run(@"
            local co = coroutine.create(function() local function a() return 1 + a() end return a() end)
            local ok, err = coroutine.resume(co)
            return ok, (tostring(err):gsub('^.-:%d+: ', '')), coroutine.status(co)"));
        // tail calls do not consume stack: 5 million of them are fine
        Assert.Equal("ok:5000000", Run("local function t(n, acc) if n == 0 then return acc end return t(n - 1, acc + 1) end return t(5000000, 0)"));
    }

    [Fact]
    public void NativeNestingIsBounded()
    {
        // each of these re-enters the interpreter through a native function; the bound is MaxNativeDepth
        string Probe(string body) => $@"
            local depth = 0
            local ok, err = pcall(function() {body} end)
            return ok, (tostring(err):gsub('^.-:%d+: ', '')), depth";
        foreach (string body in new[]
        {
            "local function f() depth = depth + 1 return (string.gsub('a', 'a', f)) end return f()",
            "local function f(a, b) depth = depth + 1 table.sort({2, 1}, f) return a < b end return f(1, 2)",
            "local function f() depth = depth + 1 return coroutine.wrap(f)() end return f()",
            "local t t = setmetatable({}, {__tostring = function() depth = depth + 1 return tostring(t) end}) return tostring(t)",
            "local function f() depth = depth + 1 local ok, e = pcall(string.gsub, 'a', 'a', f) if not ok then error(e, 0) end return e end return f()",
        })
        {
            var r = Ok(Engine().TryRunString(Probe(body)));
            Assert.False(r.Values[0].IsTruthy, body);
            Assert.Contains("stack overflow", r.Values[1].AsString);
            Assert.InRange(r.Values[2].I, 30, 200_000);
        }
        var small = Engine();
        small.State.Limits.MaxNativeDepth = 20;
        var lim = Ok(small.TryRunString(Probe("local function f() depth = depth + 1 return (string.gsub('a', 'a', f)) end return f()")));
        Assert.InRange(lim.Values[2].I, 5, 20);
        Assert.Equal("ok:ok", Show(small.TryRunString("return 'ok'")));
    }

    [Fact]
    public void MetatableChainsAndCyclesAreBounded()
    {
        Assert.Equal("ok:false|'__index' chain too long; possible loop", Run(@"
            local a, b = {}, {}
            setmetatable(a, {__index = b}) setmetatable(b, {__index = a})
            local ok, err = pcall(function() return a.x end)
            return ok, (tostring(err):gsub('^.-:%d+: ', ''))"));
        Assert.Equal("ok:false|'__newindex' chain too long; possible loop", Run(@"
            local a, b = {}, {}
            setmetatable(a, {__newindex = b}) setmetatable(b, {__newindex = a})
            local ok, err = pcall(function() a.x = 1 end)
            return ok, (tostring(err):gsub('^.-:%d+: ', ''))"));
        // a legitimate chain of 90 prototypes still resolves
        Assert.Equal("ok:found", Run(@"
            local base = {v = 'found'}
            local obj = base
            for i = 1, 90 do obj = setmetatable({}, {__index = obj}) end
            return obj.v"));
        // a __call that is itself callable-through-__call forever
        foreach (string call in new[] { "pcall(t)", "pcall(function() return t() end)" })
        {
            Assert.Equal("ok:false|'__call' chain too long; possible loop", Run($@"
                local t = {{}}
                setmetatable(t, {{__call = t}})
                local ok, err = {call}
                return ok, (tostring(err):gsub('^.-:%d+: ', ''))"));
        }
        // a chain of 50 callable tables ending in a function is fine
        Assert.Equal("ok:reached 1", Run(@"
            local f = function(...) return 'reached ' .. select('#', ...) - 50 end
            for i = 1, 50 do f = setmetatable({}, {__call = f}) end
            return f(1)"));
    }

    // ------------------------------------------------------------------ sizes at run time

    [Fact]
    public void StringSizeLimitAppliesToEveryProducer()
    {
        static void Small(CameoRuntimeOptions o) { }
        string Lim(string expr)
        {
            var e = Engine(Small);
            e.State.Limits.MaxStringLength = 1000;
            return Show(e.TryRunString($"local ok, err = pcall(function() return {expr} end)\nreturn ok, (tostring(err):gsub('^.-:%d+: ', ''))"));
        }
        Assert.Equal("ok:false|resulting string too large", Lim("('x'):rep(1001)"));
        Assert.Equal("ok:false|resulting string too large", Lim("('x'):rep(400, 'yy')"));
        Assert.Equal("ok:false|resulting string too large", Lim("('x'):rep(600) .. ('y'):rep(600)"));
        Assert.Equal("ok:false|resulting string too large", Lim("('x'):rep(400) .. ('y'):rep(400) .. ('z'):rep(400)"));
        Assert.Equal("ok:false|resulting string too large", Lim("table.concat({('x'):rep(600), ('y'):rep(600)})"));
        Assert.Equal("ok:false|resulting string too large", Lim("(function() local s = 'x' for i = 1, 20 do s = s .. s end return s end)()"));
        Assert.StartsWith("ok:true|", Lim("('x'):rep(1000)"));
        // repeating nothing a trillion times is instant (real Lua loops)
        Assert.Equal("ok:0|0", Run("return #string.rep('', 1 << 40), #string.rep('', 1 << 40, '')", o => o.Timeout = TimeSpan.FromSeconds(5)));
        // default limit: far above anything reasonable, and still an error rather than a crash
        Assert.Equal("ok:false|resulting string too large", Run("local ok, err = pcall(string.rep, 'x', 1 << 40)\nreturn ok, err"));
        Assert.Equal("ok:false|resulting string too large", Run("local ok, err = pcall(string.rep, 'abc', 1 << 62, ',')\nreturn ok, err"));
    }

    [Fact]
    public void TablesStringsAndArgumentListsAtScale()
    {
        Assert.Equal("ok:3000000|3000000|nil", Run(@"
            local t = {}
            for i = 1, 3000000 do t[i] = i end
            local n = 0
            for _ in pairs(t) do n = n + 1 end
            for i = 1, 3000000 do t[i] = nil end
            return n, n, next(t)"));
        Assert.Equal("ok:1|1|1|0", Run(@"
            local t = {}
            t[1 << 40] = 1 t[-(1 << 40)] = 1 t[2^53] = 1
            return t[1 << 40], t[-(1 << 40)], t[1 << 53], #t"));
        Assert.Equal("ok:200000|200000", Run(@"
            local t = {}
            for i = 1, 200000 do t[i] = i end
            return select('#', table.unpack(t)), #{table.unpack(t)}"));
        Assert.Equal("ok:false|too many results to unpack", Run("local ok, err = pcall(table.unpack, {}, 1, 1e8)\nreturn ok, err"));
        Assert.Equal("ok:500000", Run("local function count(...) return select('#', ...) end\nlocal t = {} for i = 1, 500000 do t[i] = true end\nreturn count(table.unpack(t))"));
        Assert.Equal("ok:1048576", Run("local s = 'x' for i = 1, 20 do s = s .. s end return #s"));
    }

    [Fact]
    public void PatternMatcherLimits()
    {
        Assert.Equal("ok:false|pattern too complex", Run("local ok, err = pcall(string.find, string.rep('a', 300), string.rep('(a', 0) .. string.rep('a?', 250) .. string.rep('a', 250))\nreturn ok, err"));
        Assert.Equal("ok:false|too many captures", Run("local ok, err = pcall(string.find, 'x', string.rep('()', 33))\nreturn ok, err"));
        Assert.Equal("ok:false|malformed pattern (ends with '%')", Run("local ok, err = pcall(string.find, 'x', '%')\nreturn ok, err"));
        Assert.Equal("ok:false|malformed pattern (missing ']')", Run("local ok, err = pcall(string.find, 'x', '[a')\nreturn ok, err"));
        Assert.Equal("ok:100000", Run("return #string.rep('a', 100000):match('a*')"));
    }

    // ------------------------------------------------------------------ watchdog

    public static IEnumerable<object[]> RunawayPrograms() => new[]
    {
        "while true do end",
        "repeat until false",
        "for i = 1, math.huge do end",
        "local i = 0 ::top:: i = i + 1 goto top",
        "local function f() return f() end f()",
        "local function f() f() end local function g() while true do pcall(f) end end g()",
        "print(pcall(function() while true do end end))",
        "print(xpcall(function() while true do end end, print))",
        "local co = coroutine.wrap(function() while true do end end) co()",
        "local t = setmetatable({}, {__index = function() while true do end end}) return t.x",
        "table.sort({3, 2, 1}, function() while true do end end)",
        "return string.find(string.rep('a', 40), string.rep('a*', 40) .. 'b')",
        "return (string.gsub(string.rep('a', 40), string.rep('a*', 40) .. 'b', ''))",
        "Future.runAsync(function() while true do coroutine.yield() end end):get()",
        "Future.delay(60):get()",
        "local t = {} while true do t[#t + 1] = #t end",
    }.Select(s => new object[] { s });

    [Theory]
    [MemberData(nameof(RunawayPrograms))]
    public void WatchdogStopsRunawayPrograms(string program)
    {
        var engine = Engine(o => o.Timeout = TimeSpan.FromMilliseconds(150));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        RunResult r = engine.TryRunString(program);
        Assert.Equal("aborted:Timeout", Show(r));
        Assert.InRange(sw.Elapsed.TotalSeconds, 0.1, 5);
        // the engine is usable again afterwards, with a fresh time budget
        Assert.Equal("ok:42", Show(engine.TryRunString("local s = 0 for i = 1, 6 do s = s + 7 end return s")));
    }

    [Fact]
    public void CancelFromAnotherThreadAndMemoryLimit()
    {
        var engine = Engine();
        var canceller = new Timer(_ => engine.Cancel(), null, 100, Timeout.Infinite);
        Assert.Equal("aborted:Cancelled", Show(engine.TryRunString("while true do end")));
        canceller.Dispose();
        Assert.Equal("ok:1", Show(engine.TryRunString("return 1")));

        var capped = Engine(o => o.MaxMemoryBytes = GC.GetTotalMemory(true) + 150L * 1024 * 1024);
        Assert.Equal("aborted:MemoryLimit", Show(capped.TryRunString("local t = {} while true do t[#t + 1] = string.rep('x', 4096) .. #t end")));
        GC.Collect();
    }

    [Fact]
    public void RunResultCoversEveryOutcome()
    {
        Assert.Equal("ok:3", Run("return 1 + 2"));
        Assert.StartsWith("compile:", Run("return +"));
        Assert.StartsWith("compile:", Run("local x: integer = 'a'", typeCheck: true));
        Assert.Equal("error:limits:1: boom", Run("error('boom')"));
        Assert.Equal("exit:7", Run("os.exit(7)"));
        Assert.Equal("exit:0", Run("pcall(os.exit, true)"));
    }

    // ------------------------------------------------------------------ scheduler

    [Fact]
    public void ManyTasksAndDeepFutureChains()
    {
        Assert.Equal("ok:50000|1250025000", Run(@"
            local fs = {}
            for i = 1, 50000 do fs[i] = Future.supplyAsync(function() return i end) end
            local s = 0
            for _, v in ipairs(Future.allOf(fs):get()) do s = s + v end
            return #fs, s"));
        Assert.Equal("ok:20000", Run(@"
            local f = Future.completedFuture(0)
            for i = 1, 20000 do f = f:thenApply(function(x) return x + 1 end) end
            return f:get()"));
        // a task awaiting inside 150 nested pcalls still suspends and resumes
        Assert.Equal("ok:done", Run(@"
            local function nest(n) if n == 0 then return Future.delay(0.001, 'done'):get() end return select(2, pcall(nest, n - 1)) end
            return Future.supplyAsync(function() return nest(150) end):get()"));
    }
}

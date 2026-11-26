using System.Globalization;
using System.Text;
using Hedgehog;
using Hedgehog.Linq;
using Cameo.Runtime.Vm;
using Xunit;
using Gen = Hedgehog.Linq.Gen;
using Range = Hedgehog.Linq.Range;

namespace Cameo.Tests.Properties;

/// <summary>
/// Runtime behaviour against simple models (Hedgehog): coroutine yield/resume protocols,
/// to-be-closed variable ordering, error values through pcall, and weak-table survivors.
/// </summary>
public class RuntimeProperties
{
    private readonly LuaHost _lua = new();

    private static readonly Gen<List<long>> GenInts = Gen.Int64(Range.LinearFromInt64(0, -99, 99)).List(Range.LinearInt32(0, 4));

    private static string Args(IEnumerable<long> xs) => string.Join(", ", xs.Select(x => x.ToString(CultureInfo.InvariantCulture)));

    // ------------------------------------------------------------------ coroutines

    /// <summary>How a yield is reached inside the coroutine body.</summary>
    private enum Via { Direct, Helper, Pcall, Loop, Metamethod }

    private sealed record Step(List<long> Yielded, List<long> Resumed, Via Via);

    private static readonly Gen<Step> GenStep =
        from y in GenInts from r in GenInts from v in Gen.Item(Via.Direct, Via.Direct, Via.Helper, Via.Pcall, Via.Loop, Via.Metamethod)
        select new Step(y, r, v);

    [Fact]
    public void Coroutines_follow_the_yield_resume_protocol()
    {
        var gen = from first in GenInts from steps in GenStep.List(Range.LinearInt32(0, 6)) from ret in GenInts select (first, steps, ret);
        Prop.Hh(gen, x =>
        {
            var (first, steps, ret) = x;
            // The body logs how many values each resume delivered, and the values themselves.
            var sb = new StringBuilder();
            sb.Append("local function helper(...) return coroutine.yield(...) end\n");
            sb.Append("local mt = setmetatable({}, { __index = function(_, k) return coroutine.yield(k) end })\n");
            sb.Append("return coroutine.create(function(...)\n");
            sb.Append("  local log = { select('#', ...), ... }\n");
            sb.Append("  local function note(...) log[#log + 1] = select('#', ...) for i = 1, select('#', ...) do log[#log + 1] = (select(i, ...)) end end\n");
            foreach (Step s in steps)
            {
                string a = Args(s.Yielded);
                switch (s.Via)
                {
                    case Via.Direct: sb.Append($"  note(coroutine.yield({a}))\n"); break;
                    case Via.Helper: sb.Append($"  note(helper({a}))\n"); break;
                    case Via.Pcall: sb.Append($"  note(select(2, pcall(function() return coroutine.yield({a}) end)))\n"); break;
                    case Via.Loop: sb.Append($"  for _ = 1, 1 do note(coroutine.yield({a})) end\n"); break;
                    case Via.Metamethod:
                        // a metamethod yields exactly one value and receives exactly one back
                        sb.Append($"  note(mt[{(s.Yielded.Count > 0 ? s.Yielded[0] : 0)}])\n");
                        break;
                }
            }
            // the result is the given values followed by the log of everything the body received
            sb.Append($"  return {ret.Count}{(ret.Count > 0 ? ", " + Args(ret) : "")}, table.unpack(log)\n");
            sb.Append("end)\n");
            Value co = _lua.Run(sb.ToString())[0];
            Assert.Equal("suspended", _lua.Call1("coroutine.status", co).AsString);

            var log = new List<long> { first.Count };
            log.AddRange(first);
            List<long> toSend = first;
            foreach (Step s in steps)
            {
                Value[] r = _lua.Call("coroutine.resume", new[] { co }.Concat(toSend.Select(v => Value.FromInt(v))).ToArray());
                Assert.True(r[0].IsTruthy, r.Length > 1 ? r[1].ToString() : "");
                List<long> expectYield = s.Via == Via.Metamethod ? new List<long> { s.Yielded.Count > 0 ? s.Yielded[0] : 0 } : s.Yielded;
                Assert.Equal(expectYield, r.Skip(1).Select(v => v.I));
                Assert.Equal("suspended", _lua.Call1("coroutine.status", co).AsString);
                toSend = s.Resumed;
                // what the body will observe for this resume
                if (s.Via == Via.Metamethod)
                {
                    log.Add(1);                                 // an index expression has exactly one value
                    log.Add(s.Resumed.Count > 0 ? s.Resumed[0] : long.MinValue);
                }
                else
                {
                    log.Add(s.Resumed.Count);
                    log.AddRange(s.Resumed);
                }
            }
            // a metamethod step that is resumed with no value yields nil in the log: avoid that case
            for (int i = 0; i < steps.Count; i++)
                if (steps[i].Via == Via.Metamethod && steps[i].Resumed.Count == 0) return;

            Value[] last = _lua.Call("coroutine.resume", new[] { co }.Concat(toSend.Select(v => Value.FromInt(v))).ToArray());
            Assert.True(last[0].IsTruthy, last.Length > 1 ? last[1].ToString() : "");
            Assert.Equal(ret.Count, last[1].I);
            Assert.Equal(ret, last.Skip(2).Take(ret.Count).Select(v => v.I));
            Assert.Equal(log, last.Skip(2 + ret.Count).Select(v => v.I));
            Assert.Equal("dead", _lua.Call1("coroutine.status", co).AsString);
            // the log the body built is exactly what was sent in
            // (returned separately so that `ret` stays the coroutine's own result)
            Value[] again = _lua.Call("coroutine.resume", co);
            Assert.False(again[0].IsTruthy);
            Assert.Equal("cannot resume dead coroutine", again[1].AsString);
            Assert.True(_lua.Call1("coroutine.close", co).IsTruthy);
        }, 300);
    }

    [Fact]
    public void Coroutine_bodies_see_exactly_the_values_passed_to_resume()
    {
        var gen = from first in GenInts from sends in GenInts.List(Range.LinearInt32(0, 6)) select (first, sends);
        Prop.Hh(gen, x =>
        {
            var (first, sends) = x;
            // an echo coroutine: yields back whatever it was resumed with, tagged with the count
            Value co = _lua.Run(@"
                return coroutine.wrap(function(...)
                    local args = table.pack(...)
                    while true do
                        args = table.pack(coroutine.yield(args.n, table.unpack(args, 1, args.n)))
                    end
                end)")[0];
            foreach (List<long> send in new[] { first }.Concat(sends))
            {
                Value[] r = _lua.L.Call(co, send.Select(v => Value.FromInt(v)).ToArray());
                Assert.Equal(send.Count, r[0].I);
                Assert.Equal(send, r.Skip(1).Select(v => v.I));
            }
        }, 200);
    }

    [Fact]
    public void Generators_produce_their_sequence_and_errors_kill_the_coroutine()
    {
        var gen = from n in Gen.Int32(Range.LinearInt32(0, 30)) from failAt in Gen.Int32(Range.LinearInt32(0, 40)) select (n, failAt);
        Prop.Hh(gen, x =>
        {
            var (n, failAt) = x;
            Value[] r = _lua.Run($@"
                local co = coroutine.create(function()
                    for i = 1, {n} do
                        if i == {failAt} then error({{ code = i }}) end
                        coroutine.yield(i * i)
                    end
                    return 'done'
                end)
                local got, last = {{}}, nil
                while true do
                    local ok, v = coroutine.resume(co)
                    if not ok then last = v.code break end
                    if coroutine.status(co) == 'dead' then last = v break end
                    got[#got + 1] = v
                end
                return #got, table.concat(got, ','), last, coroutine.status(co), (coroutine.resume(co))");
            bool fails = failAt >= 1 && failAt <= n;
            int produced = fails ? failAt - 1 : n;
            Assert.Equal(produced, r[0].I);
            Assert.Equal(string.Join(",", Enumerable.Range(1, produced).Select(i => i * i)), r[1].AsString);
            if (fails) Assert.Equal(failAt, r[2].I); else Assert.Equal("done", r[2].AsString);
            Assert.Equal("dead", r[3].AsString);
            Assert.False(r[4].IsTruthy);
        }, 200);
    }

    // ------------------------------------------------------------------ to-be-closed variables

    private enum Exit { FallThrough, Return, Error, Break, Goto }

    [Fact]
    public void Close_handlers_run_once_in_reverse_order_on_every_exit()
    {
        var gen = from outer in Gen.Int32(Range.LinearInt32(0, 3))
                  from inner in Gen.Int32(Range.LinearInt32(0, 3))
                  from exit in Gen.Item(Exit.FallThrough, Exit.Return, Exit.Error, Exit.Break, Exit.Goto)
                  from nils in Gen.Int32(Range.LinearInt32(0, 7))
                  select (outer, inner, exit, nils);
        Prop.Hh(gen, x =>
        {
            var (outer, inner, exit, nils) = x;
            var sb = new StringBuilder();
            sb.Append("local log = {}\n");
            sb.Append("local function res(name) return setmetatable({}, { __close = function(_, err) log[#log + 1] = name .. (err and '!' or '') end }) end\n");
            sb.Append("local function body()\n");
            var expected = new List<string>();
            for (int i = 0; i < outer; i++)
            {
                // some variables hold nil or false, which are skipped
                bool isNil = ((nils >> i) & 1) != 0;
                sb.Append($"  local o{i} <close> = {(isNil ? (i % 2 == 0 ? "nil" : "false") : $"res('o{i}')")}\n");
                if (!isNil) expected.Add("o" + i);
            }
            sb.Append("  for _ = 1, 1 do\n");
            var innerNames = new List<string>();
            for (int i = 0; i < inner; i++)
            {
                sb.Append($"    local i{i} <close> = res('i{i}')\n");
                innerNames.Add("i" + i);
            }
            sb.Append(exit switch
            {
                Exit.Return => "    do return 'ret' end\n",
                Exit.Error => "    error('boom', 0)\n",
                Exit.Break => "    do break end\n",
                Exit.Goto => "    goto out\n",
                _ => "",
            });
            sb.Append("  end\n  ::out::\n  log[#log + 1] = 'after'\n  return 'end'\nend\n");
            sb.Append("local ok, v = pcall(body)\nreturn ok, v, table.concat(log, ' ')\n");
            Value[] r = _lua.Run(sb.ToString());

            string bang = exit == Exit.Error ? "!" : "";
            var order = new List<string>();
            order.AddRange(Enumerable.Reverse(innerNames).Select(n => n + bang));
            if (exit is Exit.FallThrough or Exit.Break or Exit.Goto) order.Add("after");
            order.AddRange(Enumerable.Reverse(expected).Select(n => n + bang));
            Assert.Equal(string.Join(" ", order), r[2].AsString);
            Assert.Equal(exit != Exit.Error, r[0].IsTruthy);
            Assert.Equal(exit == Exit.Error ? "boom" : exit == Exit.Return ? "ret" : "end", r[1].AsString);
        }, 400);
    }

    // ------------------------------------------------------------------ errors through pcall

    private static readonly Gen<Value> GenErrorValue = Gen.Choice(
        Gen.Int64(Range.ConstantBoundedInt64()).Select(i => Value.FromInt(i)),
        Gen.Double(Range.LinearFromDouble(0, -1e9, 1e9)).Select(d => Value.FromFloat(d)),
        Gen.Char(' ', '~').String(Range.LinearInt32(0, 10)).Select(s => Value.FromString(s)),
        Gen.Item(Value.True, Value.False, Value.Nil),
        Gen.Constant(0).Select(_ => Value.FromTable(new Table())));

    [Fact]
    public void Error_values_pass_through_pcall_unchanged()
    {
        var gen = from v in GenErrorValue from depth in Gen.Int32(Range.LinearInt32(1, 6)) select (v, depth);
        Prop.Hh(gen, x =>
        {
            var (v, depth) = x;
            // error(v) called directly by pcall: no position is added, any value comes back as is
            Value[] direct = _lua.Call("pcall", _lua.Fn("error"), v);
            Assert.False(direct[0].IsTruthy);
            Assert.True(direct[1].RawEquals(v) || (v.IsFloat && double.IsNaN(v.N)));
            // thrown `depth` Lua frames down with level 0, re-thrown at every level
            Value thrower = _lua.Run(@"
                local function rethrow(v, n)
                    if n == 0 then error(v, 0) end
                    local ok, e = pcall(rethrow, v, n - 1)
                    assert(not ok)
                    error(e, 0)
                end
                return rethrow")[0];
            Value[] nested = _lua.Call("pcall", thrower, v, depth);
            Assert.False(nested[0].IsTruthy);
            Assert.True(nested[1].RawEquals(v));
            // xpcall hands the same value to the message handler
            Value seen = default;
            Value handler = Value.FromFunction((ref CallContext c) => { seen = c[0]; return c.Return(Value.FromInt(7)); }, "handler");
            Value[] viaX = _lua.Call("xpcall", thrower, handler, v, depth);
            Assert.False(viaX[0].IsTruthy);
            Assert.Equal(7, viaX[1].I);
            Assert.True(seen.RawEquals(v));
            // with the default level, string messages (only) get a position prefix
            Value[] located = _lua.L.Call(_lua.Run("return function(v) return pcall(function() error(v) end) end", "chunk")[0], v);
            Assert.False(located[0].IsTruthy);
            if (v.IsString) Assert.Equal("chunk:1: " + v.AsString, located[1].AsString);
            else Assert.True(located[1].RawEquals(v));
            // success passes every result through
            Value[] ok = _lua.Call("pcall", _lua.Fn("select"), 1, v, v);
            Assert.True(ok[0].IsTruthy);
            Assert.Equal(3, ok.Length);
            Assert.True(ok[2].RawEquals(v));
        }, 300);
    }

    [Fact]
    public void Runtime_errors_report_the_line_they_happen_on()
    {
        var gen = from line in Gen.Int32(Range.LinearInt32(1, 30)) from kind in Gen.Int32(Range.LinearInt32(0, 4)) select (line, kind);
        Prop.Hh(gen, x =>
        {
            var (line, kind) = x;
            string bad = kind switch
            {
                0 => "local t = nil; return t.x",
                1 => "local n = nil; return n + 1",
                2 => "undefined_function()",
                3 => "return #5",
                _ => "return {} < {}",
            };
            string src = new string('\n', line - 1) + bad;
            var e = Assert.Throws<LuaException>(() => _lua.Run(src, "where"));
            Assert.StartsWith($"where:{line}: ", e.Message);
            Assert.Contains("where:" + line, e.Traceback);
        }, 100);
    }

    // ------------------------------------------------------------------ weak tables

    [Fact]
    public void Weak_tables_keep_values_that_are_not_collectable_or_still_referenced()
    {
        var gen = from n in Gen.Int32(Range.LinearInt32(0, 20)) from mode in Gen.Item("k", "v", "kv") select (n, mode);
        Prop.Hh(gen, x =>
        {
            var (n, mode) = x;
            Value[] r = _lua.Run($@"
                local keep = {{}}
                local w = setmetatable({{}}, {{ __mode = '{mode}' }})
                for i = 1, {n} do
                    w[i] = 'value' .. i            -- strings and numbers are never removed
                    w['k' .. i] = i
                    local obj = {{ i }}
                    keep[i] = obj                  -- strongly referenced elsewhere
                    w[obj] = obj
                end
                collectgarbage()
                collectgarbage()
                local strings, numbers, objects = 0, 0, 0
                for k, v in pairs(w) do
                    if type(v) == 'string' then strings = strings + 1
                    elseif type(v) == 'number' then numbers = numbers + 1
                    elseif keep[v[1]] == v and k == v then objects = objects + 1 end
                end
                return strings, numbers, objects, getmetatable(w).__mode");
            Assert.Equal(n, r[0].I);
            Assert.Equal(n, r[1].I);
            Assert.Equal(n, r[2].I);
            Assert.Equal(mode, r[3].AsString);
        }, 40);
    }
}

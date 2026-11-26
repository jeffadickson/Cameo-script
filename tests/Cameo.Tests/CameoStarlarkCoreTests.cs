using System.Text;
using Cameo;
using CsCheck;
using Cameo.Runtime;
using Cameo.Runtime.Vm;
using Cameo.Runtime.Stdlib;
using Cameo.Runtime.Syntax;
using Xunit;

namespace Cameo.Tests;

public sealed class CameoStarlarkCoreTests
{
    private static Value[] Run(string source, bool typeCheck = true, bool hermetic = false, long? maxSteps = null)
    {
        using var engine = hermetic
            ? CameoEngine.CreateHermetic(maxSteps: maxSteps, typeCheck: typeCheck)
            : new CameoEngine(new CameoRuntimeOptions { TypeCheck = typeCheck, MaxSteps = maxSteps });
        return engine.RunString(source);
    }

    private static IReadOnlyList<Diagnostic> Check(string source)
    {
        using var engine = new CameoEngine();
        return engine.Check(source);
    }

    // ------------------------------------------------------------------ List<T>

    [Fact]
    public void List_literals_methods_nil_safety_and_json()
    {
        Value[] res = Run("""
            let empty: List<string> = []
            let nums = [3, 1, 4, 1, 5]
            nums.push(9)
            let popped = nums.pop()
            let sorted = nums.clone().sort()
            let doubled = [1, 2, 3].map((x) => x * 2)
            let evens = [1, 2, 3, 4, 5, 6].filter((x) => x % 2 == 0)
            let total = [10, 20, 30].reduce(0, (acc, x) => acc + x)
            let withNil: List<any> = [1, nil, 3]
            return #empty, json.encode(empty), popped, json.encode(sorted),
                   json.encode(doubled), json.encode(evens), total, #withNil, withNil[2] == nil,
                   [1, 2] == [1, 2], [1, 2] == [1, 3]
            """);

        Assert.Equal(0L, res[0].I);
        Assert.Equal("[]", res[1].AsString);
        Assert.Equal(9L, res[2].I);
        Assert.Equal("[1,1,3,4,5]", res[3].AsString);
        Assert.Equal("[2,4,6]", res[4].AsString);
        Assert.Equal("[2,4,6]", res[5].AsString);
        Assert.Equal(60L, res[6].I);
        Assert.Equal(3L, res[7].I);
        Assert.True(res[8].AsBool);
        Assert.True(res[9].AsBool);
        Assert.False(res[10].AsBool);
    }

    [Fact]
    public void List_contextual_typing_with_records_and_interfaces()
    {
        Value[] res = Run("""
            interface Greeter {
                fn greet(name: string) -> string
            }
            let gs: List<Greeter> = [
                { greet = (n) => "hi " .. n },
                { greet = (n) => "yo " .. n },
            ]
            let out = [g.greet("dev") for g in gs]
            return out[1], out[2], #out
            """);

        Assert.Equal("hi dev", res[0].AsString);
        Assert.Equal("yo dev", res[1].AsString);
        Assert.Equal(2L, res[2].I);
    }

    [Fact]
    public void List_and_Dict_checked_dot_calls_emit_zero_PrepareDotCall()
    {
        using var engine = new CameoEngine();
        Proto proto = engine.Compile("""
            let xs = [1, 2, 3]
            xs.push(4)
            let ys = xs.map((x) => x + 1)
            let d: Dict<string, integer> = Dict.new()
            d.set("a", ys.len())
            return d.get("a")
            """);

        static bool HasPrepareDotCall(Proto p)
        {
            for (int i = 0; i < p.Code.Length; i++)
                if (p.At(i).Op == OpCode.PrepareDotCall) return true;
            foreach (Proto child in p.Protos)
                if (HasPrepareDotCall(child)) return true;
            return false;
        }

        Assert.False(HasPrepareDotCall(proto));
    }

    [Fact]
    public void List_large_literal_over_fifty_items()
    {
        string items = string.Join(", ", Enumerable.Range(1, 80));
        Value[] res = Run($"let xs = [{items}]\nreturn #xs, xs[1], xs[80]");
        Assert.Equal(80L, res[0].I);
        Assert.Equal(1L, res[1].I);
        Assert.Equal(80L, res[2].I);
    }

    // ------------------------------------------------------------------ Dict<K, V>

    [Fact]
    public void Dict_insertion_order_method_collision_safety_and_json()
    {
        Value[] res = Run("""
            let d: Dict<string, integer> = Dict.new()
            let emptyJson = json.encode(d)
            d.set("zeta", 1)
            d.set("alpha", 2)
            d.set("keys", 3)
            d.set("get", 4)
            d.set("alpha", 20)
            let ks = d.keys()
            let vs = d.values()
            let deleted = d.delete("zeta")
            return emptyJson, #ks, json.encode(ks), json.encode(vs),
                   d.get("keys"), d.get("get"), d.getOr("missing", 99),
                   deleted, json.encode(d)
            """);

        Assert.Equal("{}", res[0].AsString);
        Assert.Equal(4L, res[1].I);
        Assert.Equal("[\"zeta\",\"alpha\",\"keys\",\"get\"]", res[2].AsString);
        Assert.Equal("[1,20,3,4]", res[3].AsString);
        Assert.Equal(3L, res[4].I);
        Assert.Equal(4L, res[5].I);
        Assert.Equal(99L, res[6].I);
        Assert.True(res[7].AsBool);
        Assert.Equal("{\"alpha\":20,\"keys\":3,\"get\":4}", res[8].AsString);
    }

    // ------------------------------------------------------------------ List comprehensions & for-in

    [Fact]
    public void List_comprehensions_filter_nested_dict_and_closures()
    {
        Value[] res = Run("""
            let nums = [1, 2, 3, 4, 5, 6]
            let sq = [x * x for x in nums if x % 2 == 0]
            let indexed = [i * 10 + v for i, v in [5, 6, 7]]
            let d: Dict<string, integer> = Dict.new()
            d.set("a", 1)
            d.set("b", 2)
            let pairs = [$"{k}={v}" for k, v in d]
            let matrix = [ [x * y for y in [1, 2]] for x in [3, 4] ]
            let thunks = [() => x * 10 for x in [1, 2, 3]]
            return json.encode(sq), json.encode(indexed), json.encode(pairs),
                   json.encode(matrix), thunks[1](), thunks[2](), thunks[3]()
            """);

        Assert.Equal("[4,16,36]", res[0].AsString);
        Assert.Equal("[15,26,37]", res[1].AsString);
        Assert.Equal("[\"a=1\",\"b=2\"]", res[2].AsString);
        Assert.Equal("[[3,6],[4,8]]", res[3].AsString);
        Assert.Equal(10L, res[4].I);
        Assert.Equal(20L, res[5].I);
        Assert.Equal(30L, res[6].I);
    }

    [Fact]
    public void For_in_direct_collection_iteration_and_legacy_iterators()
    {
        Value[] res = Run("""
            var sum = 0
            for x in [10, 20, 30] { sum += x }

            var idxSum = 0
            for i, x in [5, 6, 7] { idxSum += i * x }

            let d: Dict<string, integer> = Dict.new()
            d.set("x", 2)
            d.set("y", 3)
            var keys = ""
            var vals = 0
            for k, v in d {
                keys = keys .. k
                vals += v
            }

            let arr: {integer} = { 1, 2, 3, 4 }
            var arrSum = 0
            for v in arr { arrSum += v }

            let fa = FloatArray.from({ 1.5, 2.5, 3.0 })
            var faSum = 0.0
            for v in fa { faSum += v }

            let q = Query.from([2, 4, 6])
            var qSum = 0
            for v in q { qSum += v }

            var words = ""
            for w in ("foo bar baz").gmatch("%w+") { words = words .. w }

            return sum, idxSum, keys, vals, arrSum, faSum, qSum, words
            """);

        Assert.Equal(60L, res[0].I);
        Assert.Equal(5 + 12 + 21L, res[1].I);
        Assert.Equal("xy", res[2].AsString);
        Assert.Equal(5L, res[3].I);
        Assert.Equal(10L, res[4].I);
        Assert.Equal(7.0, res[5].N);
        Assert.Equal(12L, res[6].I);
        Assert.Equal("foobarbaz", res[7].AsString);
    }

    [Fact]
    public void For_in_unchecked_mode_supports_collections_and_pairs()
    {
        Value[] res = Run("""
            fn makeList() { return [2, 3, 5] }
            var s = 0
            for x in makeList() { s += x }
            let t = { a = 10, b = 20 }
            var ts = 0
            for _, v in pairs(t) { ts += v }
            return s, ts
            """, typeCheck: false);

        Assert.Equal(10L, res[0].I);
        Assert.Equal(30L, res[1].I);
    }

    [Fact]
    public void Type_checker_rejects_bad_list_element_and_non_iterable_comprehension()
    {
        IReadOnlyList<Diagnostic> errs1 = Check("let xs: List<integer> = [1, \"bad\"]");
        Assert.NotEmpty(errs1);

        IReadOnlyList<Diagnostic> errs2 = Check("let ys = [x for x in 42]");
        Assert.NotEmpty(errs2);
    }

    // ------------------------------------------------------------------ Deep freeze

    [Fact]
    public void Freeze_deeply_freezes_tables_lists_dicts_and_cycles()
    {
        Value[] res = Run("""
            let list = [1, 2]
            let dict: Dict<string, any> = Dict.new()
            dict.set("nums", list)
            let tbl: {string: any} = { d = dict }
            tbl["self"] = tbl

            let beforeTable = isFrozen(tbl)
            let beforeList = isFrozen(list)
            let beforeDict = isFrozen(dict)
            let same = freeze(tbl) == tbl

            let ok1, e1 = pcall(() => { tbl["x"] = 1 })
            let ok2, e2 = pcall(() => { list.push(3) })
            let ok3, e3 = pcall(() => { dict.set("k", 9) })
            let ok4, e4 = pcall(() => { setmetatable(tbl, {}) })
            let ok5, e5 = pcall(() => { table.sort({ 3, 1 } |> freeze) })

            return beforeTable, beforeList, beforeDict, same,
                   isFrozen(tbl), isFrozen(list), isFrozen(dict), isFrozen(42),
                   ok1, ok2, ok3, ok4, ok5,
                   tostring(e1), tostring(e2), tostring(e3)
            """);

        Assert.False(res[0].AsBool);
        Assert.False(res[1].AsBool);
        Assert.False(res[2].AsBool);
        Assert.True(res[3].AsBool);
        Assert.True(res[4].AsBool);
        Assert.True(res[5].AsBool);
        Assert.True(res[6].AsBool);
        Assert.True(res[7].AsBool);
        Assert.False(res[8].AsBool);
        Assert.False(res[9].AsBool);
        Assert.False(res[10].AsBool);
        Assert.False(res[11].AsBool);
        Assert.False(res[12].AsBool);
        Assert.Contains("frozen", res[13].AsString, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("frozen", res[14].AsString, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("frozen", res[15].AsString, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ Hermetic mode

    [Fact]
    public void Hermetic_mode_strips_all_nondeterministic_and_io_globals()
    {
        using var engine = CameoEngine.CreateHermetic(typeCheck: false);
        Value[] res = engine.RunString("""
            return os == nil, time == nil, io == nil, fs == nil, aio == nil,
                   db == nil, cli == nil, debug == nil, dofile == nil,
                   Future == nil, jit == nil,
                   math.random == nil, math.randomseed == nil,
                   crypto.randomBytes == nil, crypto.randomInt == nil, crypto.uuid == nil,
                   crypto.sha256("cameo") ~= nil,
                   json.encode([1, 2, 3])
            """);

        for (int i = 0; i < 17; i++)
            Assert.True(res[i].AsBool, $"hermetic check #{i} failed");
        Assert.Equal("[1,2,3]", res[17].AsString);
    }

    [Fact]
    public void Hermetic_mode_auto_freezes_required_modules()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cameo-hermetic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "cfg.cameo"), """
                let M = {
                    version = 1,
                    tags = ["a", "b"],
                }
                return M
                """);
            using var engine = CameoEngine.CreateHermetic(modulePaths: [dir], typeCheck: false);
            Value[] res = engine.RunString("""
                let cfg = require("cfg")
                let frozenRoot = isFrozen(cfg)
                let frozenTags = isFrozen(cfg.tags)
                let okMut, err = pcall(() => { cfg.version = 2 })
                return frozenRoot, frozenTags, okMut, tostring(err)
                """);

            Assert.True(res[0].AsBool);
            Assert.True(res[1].AsBool);
            Assert.False(res[2].AsBool);
            Assert.Contains("frozen", res[3].AsString, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Hermetic_cli_flag_parses_and_enforces_determinism()
    {
        CameoCommandParse parsed = CameoCommands.Parse(["--hermetic", "--no-check", "-e",
            "print(tostring(os == nil) .. ',' .. tostring(math.random == nil) .. ',' .. json.encode([x * 2 for x in [1, 2, 3]]))"]);
        Assert.Null(parsed.Error);
        var eval = Assert.IsType<CameoEvalCommand>(parsed.Command);
        Assert.True(eval.Hermetic);

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        CameoCommandResult result = CameoCommands.Execute(eval, stdout, stderr);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("true,true,[2,4,6]", stdout.ToString().Trim());
    }

    // ------------------------------------------------------------------ parallel.map

    [Fact]
    public void Parallel_map_works_in_hermetic_mode_and_preserves_order_and_frozen_state()
    {
        Value[] res = Run("""
            let xs = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10]
            let ys = xs.parallelMap((x: integer) -> integer => x * x + 1)
            let zs = parallel.map(ys, (v: integer) -> List<integer> => freeze([v, v + 1]))
            return #ys, json.encode(ys), isFrozen(zs[1]), json.encode(zs[10])
            """, hermetic: true);

        Assert.Equal(10L, res[0].I);
        Assert.Equal("[2,5,10,17,26,37,50,65,82,101]", res[1].AsString);
        Assert.True(res[2].AsBool);
        Assert.Equal("[101,102]", res[3].AsString);
    }

    [Fact]
    public void Parallel_map_propagates_worker_errors_and_step_limits()
    {
        Value[] errRes = Run("""
            let ok, msg = pcall(() => parallel.map([1, 2, 3, 4], (x: integer) -> integer => {
                if x == 3 { error("boom at 3") }
                return x * 2
            }))
            return ok, tostring(msg)
            """, hermetic: true);
        Assert.False(errRes[0].AsBool);
        Assert.Contains("boom at 3", errRes[1].AsString);

        using var engine = CameoEngine.CreateHermetic(maxSteps: 5_000);
        RunResult aborted = engine.TryRunString("""
            let xs = [1, 2, 3, 4]
            let _ = xs.parallelMap((x: integer) -> integer => {
                var s = 0
                for i = 1, 100000 { s += i }
                return s + x
            })
            """);
        Assert.True(aborted is RunAborted { Reason: AbortReason.StepLimit }, $"Expected StepLimit abort, got {aborted}");
    }

    // ------------------------------------------------------------------ CsCheck property tests

    [Fact]
    public void Property_parallel_map_matches_sequential_map_and_reference()
    {
        Gen.Int[-50, 50].Array[0, 24].Sample(arr =>
        {
            string literal = "[" + string.Join(", ", arr) + "]";
            string src = $"""
                let xs: List<integer> = {literal}
                let seq = xs.map((x: integer) -> integer => x * x - 3 * x + 7)
                let par = xs.parallelMap((x: integer) -> integer => x * x - 3 * x + 7)
                return json.encode(seq), json.encode(par), seq == par
                """;
            Value[] res = Run(src, hermetic: true);
            string expected = "[" + string.Join(",", arr.Select(x => (long)x * x - 3L * x + 7L)) + "]";
            Assert.Equal(expected, res[0].AsString);
            Assert.Equal(expected, res[1].AsString);
            Assert.True(res[2].AsBool);
        }, iter: 15);
    }

    [Fact]
    public void Property_dict_matches_csharp_ordered_dictionary_model()
    {
        string[] keyPool = ["a", "b", "c", "keys", "get", "set"];
        Gen.Select(Gen.Int[0, keyPool.Length - 1], Gen.Int[-10, 10], Gen.Bool)
            .Array[0, 20]
            .Sample(ops =>
            {
                var model = new OrderedDictionary<string, long>();
                var sb = new StringBuilder();
                sb.AppendLine("let d: Dict<string, integer> = Dict.new()");
                foreach (var (ki, v, isDelete) in ops)
                {
                    string k = keyPool[ki];
                    if (isDelete)
                    {
                        model.Remove(k);
                        sb.AppendLine($"d.delete(\"{k}\")");
                    }
                    else
                    {
                        model[k] = v;
                        sb.AppendLine($"d.set(\"{k}\", {v})");
                    }
                }
                sb.AppendLine("return #d, json.encode(d.keys()), json.encode(d.values())");
                Value[] res = Run(sb.ToString(), hermetic: true);

                Assert.Equal(model.Count, res[0].I);
                string expKeys = "[" + string.Join(",", model.Keys.Select(k => $"\"{k}\"")) + "]";
                string expVals = "[" + string.Join(",", model.Values) + "]";
                Assert.Equal(expKeys, res[1].AsString);
                Assert.Equal(expVals, res[2].AsString);
            }, iter: 25);
    }
}

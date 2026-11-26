using Hedgehog;
using Hedgehog.Linq;
using Cameo.Runtime.Vm;
using Xunit;
using Range = Hedgehog.Linq.Range;

namespace Cameo.Tests.Properties;

/// <summary>
/// Hedgehog properties for the features added on top of the core language: the library
/// extensions, FloatArray, Query, the language extensions, workers and sandboxes, databases
/// and the scripting tools. Each states a law (a round trip, an inverse, an invariant, or
/// agreement with a reference) rather than an example.
/// </summary>
public class ExtrasHedgehogProperties
{
    private static readonly LuaHost Host = new();
    private static readonly object Gate = new();

    private static Value[] Call(string body, params Value[] args)
    {
        lock (Gate)
        {
            Value fn = Host.Run("return function(a, b, c) " + body + " end")[0];
            Value[] r = Host.L.Call(fn, args);
            Host.L.Scheduler.RunPendingTasks();
            return r;
        }
    }

    private static readonly Gen<string> GenText = Gen.Frequency(
        (6, Gen.Char('a', 'e')), (2, Gen.Item(' ', ',', ';', '"', '\n', '-', '?', '%', '{', '}')), (1, Gen.Char((char)0, (char)255))).String(Range.LinearInt32(0, 12));

    private static readonly Gen<string> GenWord = Gen.Char('a', 'f').String(Range.LinearInt32(1, 6));

    private static readonly Gen<List<long>> GenInts = Gen.Int64(Range.LinearFromInt64(0, -40, 40)).List(Range.LinearInt32(0, 14));

    private static readonly Gen<List<double>> GenFloats = Gen.Double(Range.LinearFromDouble(0, -1000, 1000)).List(Range.LinearInt32(0, 20));

    private static Value List(IEnumerable<long> xs)
    {
        var t = new Table();
        foreach (long x in xs) t.Append(x);
        return Value.FromTable(t);
    }

    private static Value List(IEnumerable<double> xs)
    {
        var t = new Table();
        foreach (double x in xs) t.Append(x);
        return Value.FromTable(t);
    }

    private static Value Str(string bytes) => Value.FromString(bytes);

    // ------------------------------------------------------------------ strings and codecs

    [Fact]
    public void Splitting_then_joining_gives_the_string_back()
    {
        var gen = from s in GenText from sep in Gen.Item(",", ";", ", ", "ab") select (s, sep);
        Prop.Hh(gen, t =>
        {
            Value[] r = Call("local parts = a:split(b) return b:join(parts) == a, #parts, select(2, a:replace(b, b))", Str(t.s), Str(t.sep));
            Assert.True(r[0].AsBool);
            Assert.Equal(r[1].I - 1, r[2].I); // one more piece than separators
        });
    }

    [Fact]
    public void Encodings_are_inverses_and_trimming_is_idempotent()
    {
        Prop.Hh(GenText, s =>
        {
            Value[] r = Call("""
                return base64.decode(base64.encode(a)) == a, base64.decode(base64.encode(a, true)) == a, hex.decode(hex.encode(a)) == a,
                       a:trim():trim() == a:trim(), a:ltrim():rtrim() == a:trim(), #hex.encode(a) == 2 * #a,
                       a:tr('a-e', 'a-e') == a, a:find('[A-E]') ~= nil or (a:tr('a-e', 'A-E')):tr('A-E', 'a-e') == a, crypto.sha256(a) == crypto.sha256(a .. '')
                """, Str(s));
            Assert.All(r, v => Assert.True(v.AsBool));
        });
    }

    [Fact]
    public void Json_and_csv_round_trip_what_they_encode()
    {
        var gen = from words in GenWord.List(Range.LinearInt32(1, 5))
                  from nums in GenInts
                  from text in GenText
                  select (words, nums, text);
        Prop.Hh(gen, t =>
        {
            var record = new Table();
            record.SetStr("words", Value.FromTable(Seq(t.words.Select(Str))));
            record.SetStr("nums", List(t.nums));
            record.SetStr("flag", t.nums.Count % 2 == 0);
            // JSON text must be valid UTF-8: keep the free-form text to ASCII for this law
            string ascii = new(t.text.Where(ch => ch < 0x80).ToArray());
            record.SetStr("text", Str(ascii));
            var row = Seq(t.words.Select(Str).Append(Str(ascii)));
            Value[] r = Call("""
                local back = json.decode(json.encode(a))
                local pretty = json.decode(json.encode(a, {indent = 2, sortKeys = true}))
                local rows = csv.decode(csv.encode({b}), {header = false})
                return table.equals(back.words, a.words) and table.equals(back.nums, a.nums) and back.flag == a.flag and back.text == a.text,
                       table.equals(pretty.nums, a.nums) and pretty.text == a.text, #rows == 1 and table.equals(rows[1], b)
                """, Value.FromTable(record), Value.FromTable(row));
            Assert.True(r[0].AsBool);
            Assert.True(r[1].AsBool);
            Assert.True(r[2].AsBool);
        }, tests: 150);
    }

    private static Table Seq(IEnumerable<Value> items)
    {
        var t = new Table();
        foreach (Value v in items) t.Append(v);
        return t;
    }

    [Fact]
    public void A_regex_built_from_escaped_text_finds_that_text()
    {
        var gen = from before in GenText from needle in GenText.Where(s => s.Length > 0 && s.All(ch => ch < 0x80)) from after in GenText
                  select (before: new string(before.Where(ch => ch < 0x80).ToArray()), needle, after: new string(after.Where(ch => ch < 0x80).ToArray()));
        Prop.Hh(gen, t =>
        {
            Value[] r = Call("""
                local s = a .. b .. c
                local m = regex.match(s, regex.escape(b))
                local plain = s:find(b, 1, true)
                return m ~= nil and m.text == b and m.start == plain and s:sub(m.start, m.finish) == b, s:retest(regex.escape(b)),
                       (s:resub(regex.escape(b), '')) == (s:replace(b, ''))
                """, Str(t.before), Str(t.needle), Str(t.after));
            Assert.All(r, v => Assert.True(v.AsBool));
        }, tests: 150);
    }

    // ------------------------------------------------------------------ tables, queries, arrays

    [Fact]
    public void Table_helpers_obey_their_laws()
    {
        Prop.Hh(GenInts, xs =>
        {
            Value[] r = Call("""
                local even = function(x) return x % 2 == 0 end
                local yes, no = table.partition(a, even)
                local sorted = table.sortby(a, function(x) return x end)
                local ok = true
                for i = 2, #sorted do ok = ok and sorted[i - 1] <= sorted[i] end
                return #yes + #no == #a, table.all(yes, even), not table.any(no, even),
                       table.equals(table.reverse(table.reverse(a)), a), ok and #sorted == #a,
                       table.sumby(sorted, function(x) return x end) == table.reduce(a, function(s, x) return s + x end, 0),
                       #table.distinct(a) <= #a, table.count(a, even) == #table.filter(a, even),
                       table.equals(table.deepcopy(a), a), #table.take(a, 3) + #table.skip(a, 3) == #a
                """, List(xs));
            Assert.All(r, v => Assert.True(v.AsBool));
        });
    }

    [Fact]
    public void Query_chains_agree_with_linq()
    {
        var gen = from xs in GenInts from k in Gen.Int32(Range.LinearInt32(0, 6)) select (xs, k);
        Prop.Hh(gen, t =>
        {
            Value[] r = Call("""
                local q = Query.from(a)
                return table.concat(q:where(function(x) return x > 0 end):select(function(x) return x * x end):orderByDescending(function(x) return x end):take(b):toList(), ' '),
                       q:skip(b):count(), q:distinct():count(), q:sum(), #q:groupBy(function(x) return x % 3 end):toList()
                """, List(t.xs), t.k);
            Assert.Equal(string.Join(" ", t.xs.Where(x => x > 0).Select(x => x * x).OrderByDescending(x => x).Take(t.k)), r[0].AsString);
            Assert.Equal(t.xs.Skip(t.k).Count(), r[1].I);
            Assert.Equal(t.xs.Distinct().Count(), r[2].I);
            Assert.Equal(t.xs.Sum(), r[3].I);
            Assert.Equal(t.xs.Select(x => ((x % 3) + 3) % 3).Distinct().Count(), r[4].I);
        });
    }

    [Fact]
    public void FloatArray_arithmetic_and_bytes_obey_their_laws()
    {
        Prop.Hh(GenFloats, xs =>
        {
            Value[] r = Call("""
                local f = FloatArray.from(a)
                local doubled = f + f
                local ok = true
                for i = 1, #f do ok = ok and doubled[i] == 2 * a[i] and (-f)[i] == -a[i] end
                local left = f:slice(1, #f // 2)
                local right = f:slice(#f // 2 + 1)
                return ok, doubled == f * 2, FloatArray.frombytes(f:tobytes()) == f, FloatArray.from(f:totable()) == f,
                       #left + #right == #f, left:sum() + right:sum() == f:slice(1, #f // 2):sum() + f:slice(#f // 2 + 1):sum(),
                       f:copy():mulInPlace(0):sum() == 0, #f == 0 or (f:max() >= f:mean() and f:mean() >= f:min()),
                       (f - f):abs():sum() == 0
                """, List(xs));
            Assert.All(r, v => Assert.True(v.AsBool));
        });
    }

    // ------------------------------------------------------------------ language extensions

    [Fact]
    public void Language_extensions_mean_what_their_expansions_mean()
    {
        var gen = from x in Gen.Int64(Range.LinearFromInt64(0, -500, 500))
                  from y in Gen.Int64(Range.LinearFromInt64(1, -500, 500))
                  from word in GenWord
                  select (x, y, word);
        Prop.Hh(gen, t =>
        {
            string program = $$$"""
                local x, y, w = {{{t.x}}}, {{{t.y}}}, "{{{t.word}}}"
                local t = {v = x, list = {x}}
                x += y  t.v -= y  t.list[1] *= y
                local s = $"{w}:{x}|{t.v}" .. $[[<{w}>]]
                local skipped, kept = 0, 0
                ::outer:: for i = 1, 4 do
                   for j = 1, 4 do
                      if j > i then continue outer end
                      if i == 4 then break outer end
                      kept += 1
                   end
                   skipped += 1
                end
                return x, t.v, t.list[1], s, kept, skipped
                """;
            Value[] r;
            lock (Gate) r = Host.Run(program);
            Assert.Equal(t.x + t.y, r[0].I);
            Assert.Equal(t.x - t.y, r[1].I);
            Assert.Equal(unchecked(t.x * t.y), r[2].I);
            Assert.Equal($"{t.word}:{t.x + t.y}|{t.x - t.y}<{t.word}>", r[3].AsString);
            Assert.Equal(6, r[4].I);  // (1,1) (2,1) (2,2) (3,1) (3,2) (3,3)
            Assert.Equal(0, r[5].I);  // every row ends by `continue outer` or `break outer`
        }, tests: 100);
    }

    // ------------------------------------------------------------------ other VMs and the outside

    [Fact]
    public void A_sandbox_returns_what_it_is_given()
    {
        var gen = from xs in GenInts from s in GenText select (xs, s);
        Prop.Hh(gen, t =>
        {
            Value[] r = Call("""
                local list, text = sandbox.run('local l, s = ... return {l, s}', {args = {a, b}}):get()[1], nil
                local back = sandbox.run('local l, s = ... return {l, s}', {args = {a, b}}):get()
                return table.equals(back[1], a), back[2] == b, back[1] ~= a
                """, List(t.xs), Str(t.s));
            Assert.All(r, v => Assert.True(v.AsBool));
        }, tests: 40);
    }

    [Fact]
    public void Script_parameters_convert_numbers_exactly()
    {
        var gen = from n in Gen.Int64(Range.ConstantBoundedInt64()) from d in Gen.Double(Range.LinearFromDouble(0, -1e9, 1e9)) select (n, d);
        Prop.Hh(gen, t =>
        {
            Value[] r = Call("""
                local spec = {options = {{name = 'count', type = 'integer'}, {name = 'ratio', type = 'number'}}, positionals = {{name = 'n', type = 'integer'}}}
                local v = cli.tryParse(spec, {'--count=' .. a, '--ratio', string.format('%.17g', b), '--', tostring(a)})
                return v.count, v.ratio, v.n
                """, t.n, t.d);
            Assert.Equal(t.n, r[0].I);
            Assert.Equal(t.d, r[1].N);
            Assert.Equal(t.n, r[2].I);
        });
    }

    [Fact]
    public void Expect_on_a_file_stream_finds_the_first_occurrence_and_keeps_the_rest()
    {
        string path = Path.GetTempFileName();
        try
        {
            var gen = from before in GenWord.List(Range.LinearInt32(0, 6)) from after in GenWord.List(Range.LinearInt32(0, 6)) select (before, after);
            Prop.Hh(gen, t =>
            {
                // the marker cannot occur in the words (they are letters a-f)
                string head = string.Join(" ", t.before), tail = string.Join(" ", t.after);
                File.WriteAllText(path, head + "<<MARK>>" + tail + "<<MARK>>");
                Value[] r = Call("""
                    local s = aio.openRead(a):get()
                    local first = s:expect('<<MARK>>'):get()
                    local second = s:expect({'never', '<<(\\w+)>>'}):get()
                    local _, eof = s:expect('x'):await()
                    s:close()
                    return first.before, first.text, second.before, second.index, second.groups[1], eof.cause.kind
                    """, Value.FromText(path));
                Assert.Equal(head, r[0].AsString);
                Assert.Equal("<<MARK>>", r[1].AsString);
                Assert.Equal(tail, r[2].AsString);
                Assert.Equal(2, r[3].I);
                Assert.Equal("MARK", r[4].AsString);
                Assert.Equal("eof", r[5].AsString);
            }, tests: 60);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Streams_deliver_exactly_what_was_written_however_it_is_read()
    {
        string path = Path.GetTempFileName();
        try
        {
            var gen = from lines in GenWord.List(Range.LinearInt32(0, 12)) from chunk in Gen.Int32(Range.LinearInt32(1, 9)) select (lines, chunk);
            Prop.Hh(gen, t =>
            {
                Value[] r = Call("""
                    local sink = aio.openWrite(a):get()
                    for _, line in ipairs(b) do sink:write(line .. '\n') end       -- not awaited: order must still hold
                    sink:close():get()
                    local byLine, pieces = {}, {}
                    local s = aio.openRead(a):get()
                    for line in s:lines() do byLine[#byLine + 1] = line end
                    s:close()
                    local p = aio.openRead(a):get()
                    while true do local piece = p:read(c):get() if not piece then break end pieces[#pieces + 1] = piece end
                    p:close()
                    return table.equals(byLine, b), table.concat(pieces) == aio.readFile(a):get(), fs.stat(a).size
                    """, Value.FromText(path), Value.FromTable(Seq(t.lines.Select(Str))), t.chunk);
                Assert.True(r[0].AsBool);
                Assert.True(r[1].AsBool);
                Assert.Equal(t.lines.Sum(l => l.Length + 1), r[2].I);
            }, tests: 40);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

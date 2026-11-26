using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CsCheck;
using Cameo.Runtime.Vm;
using Cameo.Tests.Properties;
using Xunit;

namespace Cameo.Tests;

/// <summary>
/// The library extensions (string/table helpers, json, fs, time, regex, base64/hex/crypto) and
/// the language extensions (continue, compound assignment, string interpolation): properties
/// against .NET reference implementations and models, and the error cases.
/// </summary>
public class ExtrasTests
{
    private static readonly LuaHost Host = new();
    private static readonly object Gate = new();

    private static Value[] Run(string source)
    {
        lock (Gate) return Host.Run(source);
    }

    private static Gen<string> Pick(params string[] choices) => Gen.Int[0, choices.Length - 1].Select(i => choices[i]);

    private static string Quote(string bytes) => "\"" + string.Concat(bytes.Select(b => "\\" + ((int)b).ToString("D3"))) + "\"";
    private static string Quote(byte[] bytes) => Quote(LuaText.FromBytes(bytes));

    private static string Compile(string source, bool typeCheck = true)
    {
        var engine = new ScriptEngine(new CameoRuntimeOptions { TypeCheck = typeCheck });
        engine.State.Stdout = new StringWriter();
        return engine.TryRunString(source) switch
        {
            RunCompleted c => "ok:" + string.Join("|", c.Values.Select(v => v.ToString())),
            CompileFailed f => "compile:" + f.Diagnostics[0].Message,
            RuntimeFailed f => "error:" + f.Message,
            RunAborted a => "aborted:" + a.Reason,
            RunExited x => "exit:" + x.Code,
        };
    }

    // ------------------------------------------------------------------ strings

    private static readonly Gen<string> GenWord = Gen.Char["abc ,;"].Array[0, 12].Select(cs => new string(cs));

    [Fact]
    public void Split_agrees_with_dotnet_and_join_undoes_it()
    {
        Prop.Cs(Gen.Select(GenWord, Pick(",", ";", ", ", "ab")), t =>
        {
            var (text, sep) = t;
            Value[] r = Run($"local parts = ({Quote(text)}):split({Quote(sep)}) return #parts, table.concat(parts, '\\0'), ({Quote(sep)}):join(parts)");
            string[] expected = text.Split(sep);
            Assert.Equal(expected.Length, r[0].I);
            Assert.Equal(string.Join("\0", expected), r[1].AsString);
            Assert.Equal(text, r[2].AsString);
        });
    }

    [Fact]
    public void Whitespace_split_trim_and_predicates_agree_with_dotnet()
    {
        Prop.Cs(Gen.Char[" \t\nxy"].Array[0, 14].Select(cs => new string(cs)), text =>
        {
            Value[] r = Run($"local s = {Quote(text)} return table.concat(s:split(), '|'), s:trim(), s:ltrim(), s:rtrim(), s:startswith('x'), s:endswith('y '), s:contains('xy')");
            char[] ws = { ' ', '\t', '\n' };
            Assert.Equal(string.Join("|", text.Split(ws, StringSplitOptions.RemoveEmptyEntries)), r[0].AsString);
            Assert.Equal(text.Trim(ws), r[1].AsString);
            Assert.Equal(text.TrimStart(ws), r[2].AsString);
            Assert.Equal(text.TrimEnd(ws), r[3].AsString);
            Assert.Equal(text.StartsWith('x'), r[4].AsBool);
            Assert.Equal(text.EndsWith("y "), r[5].AsBool);
            Assert.Equal(text.Contains("xy"), r[6].AsBool);
        });
    }

    [Fact]
    public void Replace_agrees_with_dotnet()
    {
        Prop.Cs(Gen.Select(GenWord, Pick("a", "ab", ", "), Pick("", "X", "aa")), t =>
        {
            Value[] r = Run($"return ({Quote(t.Item1)}):replace({Quote(t.Item2)}, {Quote(t.Item3)})");
            Assert.Equal(t.Item1.Replace(t.Item2, t.Item3), r[0].AsString);
            Assert.Equal((t.Item1.Length - t.Item1.Replace(t.Item2, "").Length) / t.Item2.Length, r[1].I);
        });
    }

    [Fact]
    public void Utf8_case_and_sub_agree_with_dotnet_on_valid_text_and_pass_invalid_bytes_through()
    {
        Prop.Cs(Gen.Char["aZéÜßжЖ€😀 "].Array[0, 10].Select(cs => new string(cs)), text =>
        {
            string bytes = LuaText.Encode(text);
            int chars = text.EnumerateRunes().Count();
            Value[] r = Run($"local s = {Quote(bytes)} return utf8.upper(s), utf8.lower(s), utf8.sub(s, 2, -2), utf8.sub(s, 1), utf8.len(s)");
            Assert.Equal(string.Concat(text.EnumerateRunes().Select(x => Rune.ToUpperInvariant(x).ToString())), LuaText.Decode(r[0].AsString));
            Assert.Equal(string.Concat(text.EnumerateRunes().Select(x => Rune.ToLowerInvariant(x).ToString())), LuaText.Decode(r[1].AsString));
            Assert.Equal(string.Concat(text.EnumerateRunes().Skip(1).Take(Math.Max(chars - 2, 0)).Select(x => x.ToString())), LuaText.Decode(r[2].AsString));
            Assert.Equal(bytes, r[3].AsString);
            Assert.Equal(chars, r[4].I);
        });
        Value[] bad = Run("return utf8.upper('a\\255b\\195'), utf8.sub('\\255\\254x', 2, 3)");
        Assert.Equal("A\u00ffB\u00c3", bad[0].AsString);
        Assert.Equal("\u00fex", bad[1].AsString);
    }

    // ------------------------------------------------------------------ tables

    [Fact]
    public void Array_helpers_agree_with_linq()
    {
        Prop.Cs(Gen.Select(Gen.Int[-20, 20].Array[0, 12], Gen.Int[-14, 14], Gen.Int[-14, 14]), t =>
        {
            var (xs, i, j) = t;
            string list = "{" + string.Join(", ", xs) + "}";
            Value[] r = Run($"""
                local t = {list}
                local c = table.concat
                return c(table.map(t, function(x, i) return x * 2 + i end), ' '),
                       c(table.filter(t, function(x) return x % 3 == 0 end), ' '),
                       table.reduce(t, function(a, x) return a + x end, 100),
                       c(table.reverse(t), ' '), c(table.slice(t, {i}, {j}), ' '),
                       table.indexof(t, 7) or 0, table.contains(t, -3),
                       (table.find(t, function(x) return x > 10 end)) or 0, c(t, ' ')
                """);
            Assert.Equal(string.Join(" ", xs.Select((x, k) => x * 2 + k + 1)), r[0].AsString);
            Assert.Equal(string.Join(" ", xs.Where(x => x % 3 == 0)), r[1].AsString);
            Assert.Equal(100 + xs.Sum(), r[2].I);
            Assert.Equal(string.Join(" ", Enumerable.Reverse(xs)), r[3].AsString);
            // the same index rules as string.sub
            int n = xs.Length;
            int from = i < 0 ? Math.Max(n + i + 1, 1) : i == 0 ? 1 : i;
            int to = j < 0 ? n + j + 1 : Math.Min(j, n);
            Assert.Equal(string.Join(" ", xs.Skip(from - 1).Take(Math.Max(to - from + 1, 0))), r[4].AsString);
            Assert.Equal(Array.IndexOf(xs, 7) + 1, r[5].I);
            Assert.Equal(xs.Contains(-3), r[6].AsBool);
            Assert.Equal(xs.FirstOrDefault(x => x > 10), r[7].I);
            Assert.Equal(string.Join(" ", xs), r[8].AsString); // the argument is untouched
        });
    }

    [Fact]
    public void Deepcopy_is_equal_but_separate_and_equals_sees_any_difference()
    {
        Value[] r = Run("""
            local t = {1, {2, {3}}, k = {x = 1}, [true] = 'b'}
            t.self = t
            local d = table.deepcopy(t)
            local same = table.equals(d, t)
            d[2][2][1] = 99
            local s = table.copy(t)
            return same, table.equals(d, t), d.self == d, d ~= t, t[2][2][1], s[2] == t[2], s ~= t,
                   table.equals({1, 2}, {1, 2, 3}), table.equals({a = 1}, {a = 1, b = 2}), table.equals(1, 1.0), table.equals('a', 'b'),
                   getmetatable(table.deepcopy(setmetatable({}, {__index = string}))).__index == string
            """);
        Assert.Equal(new[] { true, false, true, true }, r[..4].Select(v => v.AsBool));
        Assert.Equal(3, r[4].I);
        Assert.Equal(new[] { true, true, false, false, true, false, true }, r[5..].Select(v => v.AsBool));
        Assert.StartsWith("error:", Compile("local t = {} local h = t for i = 1, 300000 do t.n = {} t = t.n end return table.deepcopy(h)", false));
    }

    [Fact]
    public void Keys_and_values_follow_pairs_order()
    {
        Value[] r = Run("local t = {10, 20, z = 1, a = 2} local ks, vs = {}, {} for k, v in pairs(t) do ks[#ks + 1] = tostring(k) vs[#vs + 1] = v end " +
                        "return table.concat(table.map(table.keys(t), tostring), ','), table.concat(ks, ','), table.concat(table.values(t), ','), table.concat(vs, ',')");
        Assert.Equal(r[1].AsString, r[0].AsString);
        Assert.Equal(r[3].AsString, r[2].AsString);
    }

    // ------------------------------------------------------------------ json

    private static readonly Gen<string> GenJsonString = Gen.OneOf(
        Gen.Char["ab \"\\/\n\t\u0001é€😀"].Array[0, 8].Select(cs => new string(cs)),
        Gen.Const(""));

    private static readonly Gen<JsonElement> GenJson = Gen.Recursive<string>((depth, self) =>
        depth >= 3
            ? Gen.OneOf(
                Gen.Long.Select(l => l.ToString()),
                Gen.Double.Where(double.IsFinite).Select(d => JsonSerializer.Serialize(d)),
                Gen.Bool.Select(b => b ? "true" : "false"),
                Gen.Const("null"),
                GenJsonString.Select(s => JsonSerializer.Serialize(s)))
            : Gen.Frequency(
                (2, Gen.Long.Select(l => l.ToString())),
                (2, GenJsonString.Select(s => JsonSerializer.Serialize(s))),
                (1, Gen.Const("null")),
                (2, self.Array[0, 4].Select(items => "[" + string.Join(",", items) + "]")),
                (2, Gen.Select(GenJsonString, self).Array[0, 4].Select(fs =>
                    "{" + string.Join(",", fs.DistinctBy(f => JsonSerializer.Serialize(f.Item1)) /* distinct as written: two lone surrogates both become \uFFFD */.Select(f => JsonSerializer.Serialize(f.Item1) + ":" + f.Item2)) + "}"))))
        .Select(text => JsonDocument.Parse(text).RootElement);

    [Fact]
    public void Json_decode_then_encode_gives_the_same_document()
    {
        Value fn;
        lock (Gate) fn = Host.Run("return function(text) local v, e = json.decode(text) if e then return nil, tostring(e) end return json.encode(v) end")[0];
        Prop.Cs(GenJson, doc =>
        {
            // System.Text.Json writes the document; our decoder reads it and our encoder writes
            // it again; System.Text.Json reads that back and must see the same document.
            string text = doc.GetRawText();
            Value[] r;
            lock (Gate) r = Host.L.Call(fn, Value.FromText(text));
            Assert.True(r[0].IsString, r.Length > 1 ? r[1].ToString() : "no result");
            using JsonDocument back = JsonDocument.Parse(LuaText.ToBytes(r[0].AsString));
            Assert.True(JsonElement.DeepEquals(doc, back.RootElement), $"{text}\n  became\n{LuaText.Decode(r[0].AsString)}");
        }, iter: 300, print: d => d.GetRawText());
    }

    [Theory]
    [InlineData("", "syntax")]
    [InlineData("{", "syntax")]
    [InlineData("[1,]", "syntax")]
    [InlineData("{\"a\":1,}", "syntax")]
    [InlineData("{a:1}", "syntax")]
    [InlineData("'x'", "syntax")]
    [InlineData("01", "syntax")]
    [InlineData("1.", "syntax")]
    [InlineData("-", "syntax")]
    [InlineData("\"abc", "syntax")]
    [InlineData("\"\\x\"", "syntax")]
    [InlineData("\"\\u12\"", "syntax")]
    [InlineData("\"a\nb\"", "syntax")]
    [InlineData("tru", "syntax")]
    [InlineData("nul", "syntax")]
    [InlineData("[1] 2", "syntax")]
    [InlineData("// c\n1", "syntax")]
    public void Json_rejects_what_the_reference_parser_rejects(string text, string kind)
    {
        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(text));
        Value[] r = Run($"local v, e = json.decode({Quote(LuaText.Encode(text))}) return v, e.kind, e.position, tostring(e)");
        Assert.True(r[0].IsNil);
        Assert.Equal(kind, r[1].AsString);
        Assert.InRange(r[2].I, 1, text.Length + 1);
        Assert.StartsWith("JsonError(syntax): ", r[3].AsString);
    }

    [Fact]
    public void Json_numbers_strings_and_structure_edge_cases()
    {
        Value[] r = Run("""
            local d = json.decode
            return math.type(d('9223372036854775807')), d('9223372036854775808'), math.type(d('1e2')), 1 / d('-0'), d('1E400'),
                   d('"\\ud83d\\ude00"') == '\xF0\x9F\x98\x80', d('"\\ud83d x"'), d('"\\u0000"') == '\0',
                   json.encode('\0\31"\\/'), json.encode(1e100), json.encode(-0.0), json.encode(2^53),
                   json.encode({[1] = 'a', [3] = 'c'}, {sortKeys = true}), json.encode(json.array({})), json.encode({10, nil, 30}),
                   json.encode(setmetatable({}, getmetatable(d('[]')))), (select(2, json.encode({[{}] = 1}))).kind
            """);
        Assert.Equal("integer", r[0].AsString);
        Assert.Equal(9223372036854775808.0, r[1].N);
        Assert.Equal("float", r[2].AsString);
        Assert.Equal(double.NegativeInfinity, r[3].N);
        Assert.Equal(double.PositiveInfinity, r[4].N);
        Assert.True(r[5].AsBool);
        Assert.Equal(LuaText.Encode("\uFFFD x"), r[6].AsString);
        Assert.True(r[7].AsBool);
        Assert.Equal("\"\\u0000\\u001f\\\"\\\\/\"", r[8].AsString);
        Assert.Equal("1E+100", r[9].AsString);
        Assert.Equal("-0.0", r[10].AsString);
        Assert.Equal("9007199254740992.0", r[11].AsString);
        Assert.Equal("{\"1\":\"a\",\"3\":\"c\"}", r[12].AsString);
        Assert.Equal("[]", r[13].AsString);
        Assert.Contains(r[14].AsString, new[] { "[10]", "[10,null,30]", "{\"1\":10,\"3\":30}" });
        Assert.Equal("[]", r[15].AsString);
        Assert.Equal("unsupported", r[16].AsString);
    }

    [Fact]
    public void Json_depth_is_bounded_both_ways()
    {
        Value[] r = Run("""
            local deep = string.rep('[', 100000) .. string.rep(']', 100000)
            local _, e1 = json.decode(deep)
            local t = {} local h = t
            for i = 1, 100000 do t[1] = {} t = t[1] end
            local _, e2 = json.encode(h)
            local ok = json.decode(string.rep('[', 400) .. string.rep(']', 400))
            return e1.kind, e2.kind, ok ~= nil
            """);
        Assert.Equal("too_deep", r[0].AsString);
        Assert.Equal("too_deep", r[1].AsString);
        Assert.True(r[2].AsBool);
    }

    // Cases chosen from surviving mutants of a Stryker run: each pins a boundary or a branch
    // that the properties above did not reach.
    [Theory]
    // escapes, both ways (JSON text is written in Lua long brackets, so backslashes are literal)
    [InlineData(@"json.decode([[""\b\f\r\n\t\/\\\""""]]) == '\b\f\r\n\t/\\""'", @"true")]
    [InlineData(@"json.encode('\b\f\r\n\t\31 ')", @"""\\b\\f\\r\\n\\t\\u001f """)]
    [InlineData(@"json.encode(nil)", @"null")]
    // surrogates: the edges of each range, pairs, and halves on their own
    [InlineData(@"json.decode([[""𐀀""]]) == utf8.char(0x10000)", @"true")]
    [InlineData(@"json.decode([[""􏿿""]]) == utf8.char(0x10FFFF)", @"true")]
    [InlineData(@"json.decode([[""퟿""]]) == utf8.char(0xD7FF, 0xE000)", @"true")]
    [InlineData(@"json.decode([[""\udc00""]]) == utf8.char(0xFFFD)", @"true")]
    [InlineData(@"json.decode([[""\udfff""]]) == utf8.char(0xFFFD)", @"true")]
    [InlineData(@"json.decode([[""\ud800""]]) == utf8.char(0xFFFD)", @"true")]
    [InlineData(@"json.decode([[""\ud800A""]]) == utf8.char(0xFFFD) .. 'A'", @"true")]
    [InlineData(@"json.decode([[""\ud800\udbff""]]) == utf8.char(0xFFFD, 0xFFFD)", @"true")]
    [InlineData(@"json.decode([[""\ud800""]]) == utf8.char(0xFFFD, 0xE000)", @"true")]
    [InlineData(@"json.decode([[""\ud800\n""]]) == utf8.char(0xFFFD) .. '\n'", @"true")]
    [InlineData(@"json.decode([[""é""]]) == utf8.char(0xE9)", @"true")]
    [InlineData(@"(select(2, json.decode([[""\u00e]]))).message", @"incomplete \\u escape at byte 4")]
    [InlineData(@"(select(2, json.decode([[""\u00zz""]]))).message", @"invalid \\u escape at byte 4")]
    [InlineData(@"(select(2, json.decode([[""\q""]]))).message", @"invalid escape '\\q' at byte 3")]
    [InlineData(@"(select(2, json.decode('""\31""'))).position", @"2")]
    [InlineData(@"json.decode('"" ""')", @" ")]
    // whitespace is allowed around every token
    [InlineData(@"json.encode(json.decode(' \t\r\n{ ""a"" : [ 1 , 2 ] , ""b"" : { } , ""c"" : [ ] } \n'), {sortKeys = true})", @"{""a"":[1,2],""b"":{},""c"":[]}")]
    // errors inside containers, with their positions
    [InlineData(@"(select(2, json.decode('{""a"" 1}'))).message", @"expected ':' after the key at byte 6")]
    [InlineData(@"(select(2, json.decode('{""a""'))).message", @"expected ':' after the key at byte 5")]
    [InlineData(@"(select(2, json.decode('{""a"":1'))).message", @"unexpected end of input in an object at byte 7")]
    [InlineData(@"(select(2, json.decode('{""a"":1 2}'))).message", @"expected ',' or '}' at byte 8")]
    [InlineData(@"(select(2, json.decode('[1'))).message", @"unexpected end of input in an array at byte 3")]
    [InlineData(@"(select(2, json.decode('[1 2]'))).message", @"expected ',' or ']' at byte 4")]
    [InlineData(@"(select(2, json.decode('[1,'))).message", @"unexpected end of input at byte 4")]
    [InlineData(@"(select(2, json.decode('""ab'))).message", @"unterminated string at byte 4")]
    [InlineData(@"(select(2, json.decode('""a\\'))).message", @"unterminated string at byte 4")]
    [InlineData(@"(select(2, json.decode('\1'))).message", @"unexpected character '\\x01' at byte 1")]
    [InlineData(@"(select(2, json.decode('\127'))).message", @"unexpected character '\\x7F' at byte 1")]
    [InlineData(@"(select(2, json.decode('~'))).message", @"unexpected character '~' at byte 1")]
    [InlineData(@"(select(2, json.encode(print))).message", @"cannot encode a function as JSON")]
    // numbers
    [InlineData(@"(select(2, json.decode('1e'))).position", @"3")]
    [InlineData(@"(select(2, json.decode('1e+'))).position", @"4")]
    [InlineData(@"(select(2, json.decode('1ex'))).position", @"3")]
    [InlineData(@"(select(2, json.decode('1e:'))).position", @"3")]
    [InlineData(@"(select(2, json.decode('1./'))).position", @"3")]
    [InlineData(@"math.type(json.decode('10')) .. math.type(json.decode('-0')) .. math.type(json.decode('-7')) .. math.type(json.decode('1e0')) .. math.type(json.decode('0.5'))", @"integerfloatintegerfloatfloat")]
    [InlineData(@"json.decode('1e-2') + json.decode('1E+2') + json.decode('90') + json.decode('0')", @"190.01")]
    [InlineData(@"json.encode(0.5) .. json.encode(3.0) .. json.encode(1e-7)", @"0.53.01E-07")]
    // nesting depth: 512 levels are accepted, 513 are not, in both directions
    [InlineData(@"json.decode(string.rep('[', 512) .. string.rep(']', 512)) ~= nil", @"true")]
    [InlineData(@"(select(2, json.decode(string.rep('[', 513) .. string.rep(']', 513)))).kind", @"too_deep")]
    [InlineData(@"(select(2, json.decode(string.rep('{""a"":', 513) .. '1' .. string.rep('}', 513)))).kind", @"too_deep")]
    [InlineData(@"(function() local t = {} local h = t for i = 1, 511 do t[1] = {} t = t[1] end return #json.encode(h) end)()", @"1024")]
    [InlineData(@"(function() local t = {} local h = t for i = 1, 512 do t[1] = {} t = t[1] end return (select(2, json.encode(h))).kind end)()", @"too_deep")]
    // a table used twice is not a cycle; siblings do not add up to depth
    [InlineData(@"(function() local s = {1} return json.encode({s, s, {s}}) end)()", @"[[1],[1],[[1]]]")]
    [InlineData(@"(function() local t = {} for i = 1, 600 do t[i] = {{}} end return #json.encode(t) end)()", @"3001")]
    // layout
    [InlineData(@"json.encode({}, {indent = 2}) .. json.encode(json.array(), {indent = 2}) .. json.encode({{}}, {indent = 1})", @"{}[][\n {}\n]")]
    [InlineData(@"json.encode({a = {b = 1}}, {indent = 2})", @"{\n  ""a"": {\n    ""b"": 1\n  }\n}")]
    [InlineData(@"json.encode({z = 1, a = 2, m = 3})", @"{""z"":1,""a"":2,""m"":3}")]
    [InlineData(@"(function() local t = {5} return tostring(json.array(t) == t) .. json.encode(json.array(t)) .. tostring(json.isArray(t)) .. tostring(json.isArray({})) .. tostring(json.isArray(1)) end)()", @"true[5]truefalsefalse")]
    [InlineData(@"tostring(json.null) .. tostring(pcall(json.encode)) .. tostring(pcall(json.array, 5))", @"nullfalsefalse")]
    // string helpers
    [InlineData(@"table.concat(('a\r\nb\n\r\n\rc\r'):lines(), '|')", @"a|b||\rc")]
    [InlineData(@"#(''):lines() .. #('\n'):lines() .. #('x'):lines() .. #('x\n'):lines() .. #('x\ny'):lines()", @"01112")]
    [InlineData(@"table.concat(('  a  b  c d '):split(nil, 2), '|') .. '/' .. table.concat(('a b'):split(nil, 0), '|') .. '/' .. table.concat(('a,b,c'):split(',', 0), '|')", @"a|b|c d/a b/a,b,c")]
    [InlineData(@"tostring(pcall(string.split, 'a', '')) .. tostring(pcall(string.join, ',', {1, {}})) .. tostring(pcall(string.replace, 'a', '', 'b'))", @"falsefalsefalse")]
    [InlineData(@"('\v\f\r\n\t x \8'):trim() .. '|' .. ('abc'):trim() .. '|' .. (''):trim()", @"x \8|abc|")]
    [InlineData(@"utf8.sub('h\xC3\xA4llo', -4, -3) .. utf8.sub('abc', 0) .. utf8.sub('abc', -9, 2) .. utf8.sub('abc', 2, 9) .. utf8.sub('abc', 3, 2) .. utf8.sub('abc', -1)", @"\xC3\xA4labcabbcc")]
    [InlineData(@"utf8.upper('a\x7Fz') .. utf8.lower('A\xC3\x89\x80Z')", @"A\x7FZa\xC3\xA9\x80z")]
    // table helpers
    [InlineData(@"#table.slice({1, 2, 3}, 2, 9) .. #table.slice({1, 2, 3}, 3, 3) .. #table.slice({1, 2, 3}, 3, 2) .. #table.slice({}, 1, 1)", @"2100")]
    [InlineData(@"(function() local mt = {} local c = table.copy(setmetatable({1}, mt)) return tostring(getmetatable(c) == mt) end)()", @"true")]
    [InlineData(@"(function() local k = {1} local d = table.deepcopy({[k] = 'v'}) local nk = next(d) return tostring(nk ~= k) .. nk[1] .. d[nk] end)()", @"true1v")]
    [InlineData(@"tostring(table.equals({}, 1)) .. tostring(table.equals(1, {})) .. tostring(table.equals({}, {})) .. tostring(pcall(table.equals, 1)) .. tostring(pcall(table.contains, {}))", @"falsefalsetruefalsefalse")]
    [InlineData(@"(function() local s = {1} return tostring(table.equals({s, s}, {{1}, {2}})) .. tostring(table.equals({s, s}, {{1}, {1}})) end)()", @"falsetrue")]
    [InlineData(@"(function() local eq = setmetatable({}, {__eq = function() return true end}) return tostring(table.equals(eq, {1})) .. tostring(table.equals({1}, eq)) end)()", @"truetrue")]
    public void Library_boundaries(string expression, string expected)
    {
        Value[] r = Run("return " + expression);
        Value[] want = Run("return \"" + expected.Replace("\"", "\\\"") + "\"");
        Assert.Equal(want[0].AsString, r[0].ToString());
    }

    // ------------------------------------------------------------------ codecs

    [Fact]
    public void Base64_hex_and_hashes_agree_with_dotnet()
    {
        Prop.Cs(Gen.Byte.Array[0, 200], bytes =>
        {
            Value[] r = Run($"""
                local s = {Quote(bytes)}
                return base64.encode(s), base64.decode(base64.encode(s)) == s, base64.decode(base64.encode(s, true)) == s, base64.encode(s, true),
                       hex.encode(s), hex.decode(hex.encode(s)) == s, hex.decode(hex.encode(s):upper()) == s,
                       crypto.sha256(s), crypto.sha1(s), crypto.md5(s), crypto.sha512(s), crypto.sha256(s, true),
                       crypto.hmac('sha256', 'key', s), crypto.hmac('sha1', s, 'msg')
                """);
            Assert.Equal(Convert.ToBase64String(bytes), r[0].AsString);
            Assert.True(r[1].AsBool && r[2].AsBool && r[5].AsBool && r[6].AsBool);
            Assert.Equal(Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_'), r[3].AsString);
            Assert.Equal(Convert.ToHexStringLower(bytes), r[4].AsString);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), r[7].AsString);
            Assert.Equal(Convert.ToHexStringLower(SHA1.HashData(bytes)), r[8].AsString);
            Assert.Equal(Convert.ToHexStringLower(MD5.HashData(bytes)), r[9].AsString);
            Assert.Equal(Convert.ToHexStringLower(SHA512.HashData(bytes)), r[10].AsString);
            Assert.Equal(SHA256.HashData(bytes), LuaText.ToBytes(r[11].AsString));
            Assert.Equal(Convert.ToHexStringLower(HMACSHA256.HashData("key"u8, bytes)), r[12].AsString);
            Assert.Equal(Convert.ToHexStringLower(HMACSHA1.HashData(bytes, "msg"u8)), r[13].AsString);
        }, iter: 150);
    }

    [Fact]
    public void Bad_encodings_are_typed_errors_and_random_values_are_in_range()
    {
        Value[] r = Run("""
            local function kind(v, e) return (v == nil) and e.kind or 'ok' end
            local lo, hi, seen = math.maxinteger, math.mininteger, {}
            for i = 1, 300 do local x = crypto.randomInt(-2, 2) lo = math.min(lo, x) hi = math.max(hi, x) seen[x] = true end
            local n = 0 for _ in pairs(seen) do n = n + 1 end
            return kind(base64.decode('a')), kind(base64.decode('ab!d')), kind(base64.decode('')), kind(hex.decode('abc')), kind(hex.decode('zz')),
                   lo, hi, n, crypto.randomInt(7, 7), math.type(crypto.randomInt(math.mininteger, math.maxinteger)),
                   #crypto.randomBytes(0), crypto.randomBytes(8) ~= crypto.randomBytes(8),
                   crypto.uuid():match('^%x%x%x%x%x%x%x%x%-%x%x%x%x%-4%x%x%x%-[89ab]%x%x%x%-%x%x%x%x%x%x%x%x%x%x%x%x$') ~= nil,
                   pcall(crypto.randomInt, 2, 1), pcall(crypto.hmac, 'sha3', 'k', 'm'), pcall(crypto.randomBytes, -1)
            """);
        Assert.Equal(new[] { "invalid_base64", "invalid_base64", "ok", "invalid_hex", "invalid_hex" }, r[..5].Select(v => v.AsString));
        Assert.Equal(-2, r[5].I);
        Assert.Equal(2, r[6].I);
        Assert.Equal(5, r[7].I);
        Assert.Equal(7, r[8].I);
        Assert.Equal("integer", r[9].AsString);
        Assert.Equal(0, r[10].I);
        Assert.True(r[11].AsBool && r[12].AsBool);
        Assert.False(r[13].AsBool);
    }

    // ------------------------------------------------------------------ regex

    [Fact]
    public void Regex_positions_are_byte_positions_in_utf8_text()
    {
        // (characters outside the BMP are two units to the .NET engine: checked separately below)
        Prop.Cs(Gen.Char["aé€ж 1"].Array[0, 10].Select(cs => new string(cs)), text =>
        {
            string bytes = LuaText.Encode(text);
            Value[] r = Run($"""
                local s = {Quote(bytes)}
                local out = {"{"}{"}"}
                for _, m in ipairs(regex.matches(s, '[^ ]')) do
                  assert(s:sub(m.start, m.finish) == m.text)
                  out[#out + 1] = m.text
                end
                return table.concat(out, '|'), #regex.split(s, ' '), (regex.replace(s, '.', '$0$0'))
                """);
            var runes = text.EnumerateRunes().Where(x => x.Value != ' ').Select(x => x.ToString());
            Assert.Equal(string.Join("|", runes), LuaText.Decode(r[0].AsString));
            Assert.Equal(text.Split(' ').Length, r[1].I);
            Assert.Equal(string.Concat(text.EnumerateRunes().Select(x => x.ToString() + x)), LuaText.Decode(r[2].AsString));
        });
    }

    [Fact]
    public void Regex_matches_never_split_a_character_outside_the_bmp()
    {
        // U+1F600 is four bytes in UTF-8 and two units in UTF-16
        Value[] r = Run("""
            local s = 'a\xF0\x9F\x98\x80b'
            local ok = true
            for _, pat in ipairs({'.', '[^a]', 'a.', '.b', '\\P{L}'}) do
              for _, m in ipairs(regex.matches(s, pat)) do
                ok = ok and s:sub(m.start, m.finish) == m.text and utf8.len(m.text) ~= nil
              end
            end
            return ok, regex.match(s, 'a..b').text == s, regex.match(s, '\xF0\x9F\x98\x80').start
            """);
        Assert.True(r[0].AsBool);
        Assert.True(r[1].AsBool);
        Assert.Equal(2, r[2].I);
    }

    [Fact]
    public void Regex_flags_groups_errors_and_timeouts()
    {
        Value[] r = Run("""
            local re = regex.new('(a)|(b)')
            local m = re:match('xb')
            local multi = regex.new('^b$', 'm'):test('a\nb')
            local dot = regex.new('a.b', 's'):test('a\nb')
            local ext = regex.new('a  b # comment', 'x'):test('ab')
            local lin, lerr = regex.new('(a)\\1', 'n')
            local _, ferr = regex.new('a', 'q')
            local init = regex.match('aXbXc', 'X', 3)
            local raw = regex.match('\255a\254', 'a')
            local limited = regex.replace('aaaa', 'a', 'b', 2)
            local kept = regex.replace('abc', '[ab]', function(m) if m.text == 'a' then return nil end return 'B' end)
            local ok, terr = pcall(regex.test, string.rep('a', 40) .. '!', '^(a+)+$')
            return m.groups[1], m.groups[2], #m.groups, m.named, multi, dot, ext, lin, lerr.kind, ferr.kind, init.start, raw.start, limited, kept,
                   tostring(re), ok, type(terr) == 'table' and terr.kind or 'none', regex.new('^(a+)+$', 'n'):test(string.rep('a', 40) .. '!')
            """);
        Assert.True(r[0].IsNil);
        Assert.Equal("b", r[1].AsString);
        Assert.True(r[3].IsNil);
        Assert.True(r[4].AsBool && r[5].AsBool && r[6].AsBool);
        Assert.True(r[7].IsNil);
        Assert.Equal("invalid_pattern", r[8].AsString);
        Assert.Equal("invalid_pattern", r[9].AsString);
        Assert.Equal(4, r[10].I);
        Assert.Equal(2, r[11].I);
        Assert.Equal("bbaa", r[12].AsString);
        Assert.Equal("aBc", r[13].AsString);
        Assert.Equal("Regex((a)|(b))", r[14].AsString);
        // catastrophic backtracking is cut off (or finishes); the linear engine answers at once
        Assert.True(r[15].AsBool || r[16].AsString == "timeout");
        Assert.False(r[17].AsBool);
    }

    // ------------------------------------------------------------------ time

    [Fact]
    public void Time_format_and_parse_round_trip_and_agree_with_dotnet()
    {
        Prop.Cs(Gen.Select(Gen.Long[-2_000_000_000, 4_000_000_000], Pick("utc", "Europe/Berlin", "America/New_York", "Asia/Kolkata")), t =>
        {
            var (secs, zone) = t;
            Value[] r = Run($"local s = time.format({secs}, nil, '{zone}') return s, time.parse(s), time.format({secs}, 'yyyy-MM-dd HH:mm:ss', '{zone}')");
            Assert.Equal((double)secs, r[1].N);
            var tz = zone == "utc" ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById(zone);
            DateTimeOffset at = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(secs), tz);
            Assert.Equal(at.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture), r[2].AsString);
        }, iter: 200);
    }

    [Fact]
    public void Sleep_lets_other_tasks_run_and_obeys_the_watchdog()
    {
        Value[] r = Run("""
            local order = {}
            local a = Future.runAsync(function() time.sleep(0.03) order[#order + 1] = 'slow' end)
            local b = Future.runAsync(function() order[#order + 1] = 'fast' end)
            a:get() b:get()
            return table.concat(order, ',')
            """);
        Assert.Equal("fast,slow", r[0].AsString);
        var engine = new ScriptEngine(new CameoRuntimeOptions { TypeCheck = false, Timeout = TimeSpan.FromMilliseconds(100) });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.True(engine.TryRunString("time.sleep(30)") is RunAborted { Reason: AbortReason.Timeout });
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
    }

    // ------------------------------------------------------------------ language

    [Fact]
    public void Compound_assignment_equals_its_expansion()
    {
        var ops = new[] { "+", "-", "*", "//", "%", "&", "|", "<<", ">>" };
        Prop.Cs(Gen.Select(Gen.Long[-1000, 1000], Gen.Long[-1000, 1000], Gen.Int[0, ops.Length - 1]), t =>
        {
            var (a, b, k) = t;
            string op = ops[k];
            if ((b == 0 || b + 1 == 0) && op is "//" or "%") return; // both `b` and `b + 1` are used as divisors below
            Value[] r = Run($"""
                local x, y = {a}, {a}
                local t, u = {"{"}v = {a}{"}"}, {"{"}{a}{"}"}
                x {op}= {b} + 1
                y = y {op} ({b} + 1)
                t.v {op}= {b}
                u[1] {op}= {b}
                return x, y, t.v, u[1], {a} {op} ({b})
                """);
            Assert.Equal(r[1].I, r[0].I);
            Assert.Equal(r[4].I, r[2].I);
            Assert.Equal(r[4].I, r[3].I);
        });
    }

    [Theory]
    [InlineData("continue", "compile:continue outside a loop")]
    [InlineData("local function f() continue end for i = 1, 2 do f() end", "compile:continue outside a loop")]
    [InlineData("repeat local a = 1 if a then continue end local b = 2 until b", "compile:'continue' skips the declaration of local 'b', which the 'until' condition uses")]
    [InlineData("local n = 0 repeat local a = n n = n + 1 if a < 2 then continue end until a >= 2 return n", "ok:3")]
    [InlineData("local x = 1 x += 1, 2", "compile:'+=' takes one value")]
    [InlineData("local function f() end f() += 1", "compile:cannot use '+=' on this expression (a variable or field is needed)")]
    [InlineData("local a, b = 1, 2 a, b += 1", "compile:'=' expected near '+='")]
    [InlineData("return $\"{\"", "compile:unfinished string in an interpolated expression")]
    [InlineData("return $\"abc", "compile:unfinished string")]
    [InlineData("return $\"}\"", "compile:'}' without '{' in an interpolated string (write '}}' for a literal brace)")]
    [InlineData("return $\"{}\"", "compile:empty '{}' in an interpolated string")]
    [InlineData("return $\"{1 +}\"", "compile:in interpolated '{1 +}': unexpected '<eof>'")]
    [InlineData("return $\"{1 2}\"", "compile:in interpolated '{1 2}': unexpected '2'")]
    [InlineData("return $\"a\\tb{1}\\65\"", "ok:a\tb1A")]
    [InlineData("local t = {k = 'v'} return $\"{t[\"k\"]}{t['k']}\"", "ok:vv")]
    [InlineData("local continue = 1 continue += 1 return continue", "ok:2")]
    public void Language_extension_diagnostics(string source, string expected) =>
        Assert.Equal(expected, Compile(source, typeCheck: false));

    [Theory]
    [InlineData("local s: string = 'a' s += 1", "compile:")]
    [InlineData("local n: integer = 1 n ..= 'x'", "compile:")]
    [InlineData("local record R x: integer end local r: R = {x = 1} r.y += 1", "compile:")]
    [InlineData("local n: integer = 1 n /= 2", "compile:")]
    [InlineData("local n: number = 1 n /= 2 return n", "ok:0.5")]
    [InlineData("local record R x: integer end local r: R = {x = 1} r.x += 1 return r.x", "ok:2")]
    [InlineData("local m: {string:integer} = {} local k = 'a' m[k] = 1 m[k .. ''] += 1 return m.a", "ok:2")]
    [InlineData("local s: string = $\"{1}{true}{nil}{ {} == nil }\" return s", "ok:1truenilfalse")]
    public void Language_extensions_are_type_checked(string source, string expectedPrefix) =>
        Assert.StartsWith(expectedPrefix, Compile(source));

    [Fact]
    public void Continue_closes_to_be_closed_variables_of_the_body()
    {
        Assert.Equal("ok:c1,c2,c3,b3", Compile("""
            local log = {}
            local function closer(name) return setmetatable({}, {__close = function() log[#log + 1] = name end}) end
            for i = 1, 3 do
              local c <close> = closer('c' .. i)
              if i < 3 then continue end
              log[#log + 1] = 'b' .. i
            end
            return table.concat(log, ','):gsub('c3,b3', 'b3,c3'):gsub('b3,c3', 'c3,b3')
            """, typeCheck: false).Split('|')[0]);
        Assert.Equal("ok:c1 c2 body3 c3", Compile("""
            local log = {}
            local function closer(name) return setmetatable({}, {__close = function() log[#log + 1] = name end}) end
            local i = 0
            while i < 3 do
              i += 1
              local c <close> = closer('c' .. i)
              if i < 3 then continue end
              log[#log + 1] = 'body' .. i
            end
            return table.concat(log, ' ')
            """, typeCheck: false));
    }
}

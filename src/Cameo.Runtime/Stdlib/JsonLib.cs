using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Cameo.Runtime.Vm;

namespace Cameo.Runtime.Stdlib;

/// <summary>Why JSON could not be read or written.</summary>
public enum JsonErrorKind { Syntax, TooDeep, Unsupported, Cycle, InvalidNumber }

/// <summary>
/// <c>json</c>: <c>json.decode(text)</c> and <c>json.encode(value [, options])</c>. Both are
/// railway-style: they return the result, or nil and a typed <c>JsonError</c> (kind, message,
/// position) — they do not raise.
///
/// Mapping: objects are tables with string keys, arrays are sequences, numbers without
/// fraction or exponent that fit 64 bits are integers, and JSON null is <c>json.null</c> (so
/// arrays keep their positions). Decoded arrays carry a marker metatable so that an empty
/// array stays an array when encoded again; <c>json.array(t)</c> marks a table by hand.
/// Strings are bytes on both sides (UTF-8 is expected, not checked).
/// </summary>
public static class JsonLib
{
    private const int MaxDepth = 512;

    private sealed class JsonException(JsonErrorKind kind, string message, int position = 0) : Exception(message)
    {
        public JsonErrorKind Kind => kind;
        public int Position => position;
    }

    /// <summary>Per-VM objects: the null sentinel and the array marker.</summary>
    private sealed class Shared
    {
        public readonly Table Null = new();
        public readonly Table ArrayMeta = new();
        public readonly Table ErrorMeta = new();
    }

    public static void Open(LuaState L)
    {
        var shared = new Shared();
        L.JsonShared = shared;
        var json = new Table();

        var nullMeta = new Table();
        nullMeta.SetStr("__name", "json.null");
        L.Register(nullMeta, "__tostring", static (ref CallContext c) => c.Return("null"));
        shared.Null.Metatable = nullMeta;
        shared.ArrayMeta.SetStr("__name", "json.array");
        shared.ErrorMeta.SetStr("__name", "JsonError");
        L.Register(shared.ErrorMeta, "__tostring", static (ref CallContext c) =>
        {
            Table t = c.CheckTable(0);
            return c.Return($"JsonError({t.GetStr("kind")}): {t.GetStr("message")}");
        });

        json.SetStr("null", shared.Null);
        L.Register(json, "decode", (ref CallContext c) =>
        {
            string text = c.CheckString(0);
            try
            {
                var p = new Parser(text, shared);
                Value v = p.ParseDocument();
                return c.Return(v, Value.Nil);
            }
            catch (JsonException e)
            {
                return c.Return(Value.Nil, Error(shared, e));
            }
            catch (InsufficientExecutionStackException)
            {
                return c.Return(Value.Nil, Error(shared, new JsonException(JsonErrorKind.TooDeep, "JSON is nested too deeply")));
            }
        });
        L.Register(json, "encode", (ref CallContext c) =>
        {
            c.CheckAny(0);
            int indent = 0;
            bool sort = false;
            if (!c.IsNoneOrNil(1))
            {
                Table o = c.CheckTable(1);
                if (o.GetStr("indent").TryToInteger(out long n)) indent = (int)Math.Clamp(n, 0, 16);
                sort = o.GetStr("sortKeys").IsTruthy;
            }
            try
            {
                var w = new Writer(c.L, shared, indent, sort);
                w.Write(c[0], 0);
                return c.Return(w.ToString(), Value.Nil);
            }
            catch (JsonException e)
            {
                return c.Return(Value.Nil, Error(shared, e));
            }
            catch (InsufficientExecutionStackException)
            {
                return c.Return(Value.Nil, Error(shared, new JsonException(JsonErrorKind.TooDeep, "value is nested too deeply")));
            }
        });
        L.Register(json, "array", (ref CallContext c) =>
        {
            Table t = c.IsNoneOrNil(0) ? new Table() : c.CheckTable(0);
            t.Metatable = shared.ArrayMeta;
            return c.Return(Value.FromTable(t));
        });
        L.Register(json, "isArray", (ref CallContext c) => c.Return(c[0].O is Table t && ReferenceEquals(t.Metatable, shared.ArrayMeta)));

        L.Globals.SetStr("json", json);
        L.LoadedModules.SetStr("json", json);
    }

    /// <summary>Decodes JSON for other libraries; on failure <paramref name="message"/> says why.</summary>
    internal static bool TryDecode(LuaState L, string text, out Value value, out string? message)
    {
        try
        {
            value = new Parser(text, (Shared)L.JsonShared!).ParseDocument();
            message = null;
            return true;
        }
        catch (JsonException e)
        {
            value = default;
            message = e.Position > 0 ? $"{e.Message} at byte {e.Position}" : e.Message;
            return false;
        }
        catch (InsufficientExecutionStackException)
        {
            value = default;
            message = "JSON is nested too deeply";
            return false;
        }
    }

    /// <summary>Encodes a value as JSON for other libraries; on failure <paramref name="message"/> says why.</summary>
    internal static bool TryEncode(LuaState L, in Value value, out string text, out string? message)
    {
        try
        {
            var w = new Writer(L, (Shared)L.JsonShared!, 0, false);
            w.Write(value, 0);
            text = w.ToString();
            message = null;
            return true;
        }
        catch (JsonException e)
        {
            text = "";
            message = e.Message;
            return false;
        }
        catch (InsufficientExecutionStackException)
        {
            text = "";
            message = "value is nested too deeply";
            return false;
        }
    }

    private static string KindName(JsonErrorKind kind) => kind switch
    {
        JsonErrorKind.Syntax => "syntax",
        JsonErrorKind.TooDeep => "too_deep",
        JsonErrorKind.Unsupported => "unsupported",
        JsonErrorKind.Cycle => "cycle",
        JsonErrorKind.InvalidNumber => "invalid_number",
        _ => "syntax",
    };

    private static Value Error(Shared shared, JsonException e)
    {
        var t = new Table(0, 3);
        t.SetStr("kind", KindName(e.Kind));
        t.SetStr("message", e.Position > 0 ? $"{e.Message} at byte {e.Position}" : e.Message);
        t.SetStr("position", e.Position);
        t.Metatable = shared.ErrorMeta;
        return Value.FromTable(t);
    }

    // ------------------------------------------------------------------ decoding

    private ref struct Parser
    {
        private readonly string _s;
        private readonly Shared _shared;
        private int _i;
        private int _depth;

        public Parser(string s, Shared shared) { _s = s; _shared = shared; _i = 0; _depth = 0; }

        private readonly JsonException Fail(string message) => new(JsonErrorKind.Syntax, message, _i + 1);

        private void SkipSpace()
        {
            while (_i < _s.Length && _s[_i] is ' ' or '\t' or '\n' or '\r') _i++;
        }

        public Value ParseDocument()
        {
            Value v = ParseValue();
            SkipSpace();
            if (_i < _s.Length) throw Fail("unexpected text after the JSON value");
            return v;
        }

        private Value ParseValue()
        {
            SkipSpace();
            if (_i >= _s.Length) throw Fail("unexpected end of input");
            char ch = _s[_i];
            switch (ch)
            {
                case '{': return ParseObject();
                case '[': return ParseArray();
                case '"': return Value.FromString(ParseString());
                case 't': Expect("true"); return Value.True;
                case 'f': Expect("false"); return Value.False;
                case 'n': Expect("null"); return Value.FromTable(_shared.Null);
                default:
                    if (ch == '-' || (ch >= '0' && ch <= '9')) return ParseNumber();
                    throw Fail($"unexpected character '{Show(ch)}'");
            }
        }

        private static string Show(char ch) => ch < 0x20 || ch >= 0x7F ? $"\\x{(int)ch:X2}" : ch.ToString();

        private void Expect(string word)
        {
            if (string.CompareOrdinal(_s, _i, word, 0, word.Length) != 0) throw Fail($"invalid literal (expected '{word}')");
            _i += word.Length;
        }

        private void Enter()
        {
            if (++_depth > MaxDepth) throw new JsonException(JsonErrorKind.TooDeep, "JSON is nested too deeply", _i + 1);
            RuntimeHelpers.EnsureSufficientExecutionStack();
        }

        private Value ParseObject()
        {
            Enter();
            _i++;
            var t = new Table();
            SkipSpace();
            if (_i < _s.Length && _s[_i] == '}') { _i++; _depth--; return Value.FromTable(t); }
            while (true)
            {
                SkipSpace();
                if (_i >= _s.Length || _s[_i] != '"') throw Fail("expected a string key");
                string key = ParseString();
                SkipSpace();
                if (_i >= _s.Length || _s[_i] != ':') throw Fail("expected ':' after the key");
                _i++;
                t.SetStr(key, ParseValue());
                SkipSpace();
                if (_i >= _s.Length) throw Fail("unexpected end of input in an object");
                if (_s[_i] == ',') { _i++; continue; }
                if (_s[_i] == '}') { _i++; _depth--; return Value.FromTable(t); }
                throw Fail("expected ',' or '}'");
            }
        }

        private Value ParseArray()
        {
            Enter();
            _i++;
            var t = new Table { Metatable = _shared.ArrayMeta };
            SkipSpace();
            if (_i < _s.Length && _s[_i] == ']') { _i++; _depth--; return Value.FromTable(t); }
            while (true)
            {
                t.Append(ParseValue());
                SkipSpace();
                if (_i >= _s.Length) throw Fail("unexpected end of input in an array");
                if (_s[_i] == ',') { _i++; continue; }
                if (_s[_i] == ']') { _i++; _depth--; return Value.FromTable(t); }
                throw Fail("expected ',' or ']'");
            }
        }

        private Value ParseNumber()
        {
            int start = _i;
            bool isFloat = false;
            if (_s[_i] == '-') _i++;
            if (_i >= _s.Length || _s[_i] < '0' || _s[_i] > '9') throw Fail("invalid number");
            if (_s[_i] == '0') _i++;
            else while (_i < _s.Length && _s[_i] >= '0' && _s[_i] <= '9') _i++;
            if (_i < _s.Length && _s[_i] == '.')
            {
                isFloat = true;
                _i++;
                if (_i >= _s.Length || _s[_i] < '0' || _s[_i] > '9') throw Fail("invalid number (digits expected after '.')");
                while (_i < _s.Length && _s[_i] >= '0' && _s[_i] <= '9') _i++;
            }
            if (_i < _s.Length && (_s[_i] == 'e' || _s[_i] == 'E'))
            {
                isFloat = true;
                _i++;
                if (_i < _s.Length && (_s[_i] == '+' || _s[_i] == '-')) _i++;
                if (_i >= _s.Length || _s[_i] < '0' || _s[_i] > '9') throw Fail("invalid number (digits expected in the exponent)");
                while (_i < _s.Length && _s[_i] >= '0' && _s[_i] <= '9') _i++;
            }
            ReadOnlySpan<char> text = _s.AsSpan(start, _i - start);
            // "-0" is the float negative zero; integers that do not fit 64 bits become floats
            if (!isFloat && !(text.Length == 2 && text[0] == '-' && text[1] == '0')
                && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long l))
                return Value.FromInt(l);
            return Value.FromFloat(double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture));
        }

        private string ParseString()
        {
            _i++; // opening quote
            int start = _i;
            // fast path: no escapes
            while (_i < _s.Length && _s[_i] != '"' && _s[_i] != '\\' && _s[_i] >= 0x20) _i++;
            if (_i < _s.Length && _s[_i] == '"') return _s.Substring(start, _i++ - start);
            var sb = new StringBuilder();
            sb.Append(_s, start, _i - start);
            while (true)
            {
                if (_i >= _s.Length) throw Fail("unterminated string");
                char ch = _s[_i];
                if (ch == '"') { _i++; return sb.ToString(); }
                if (ch < 0x20) throw Fail("control character in a string (use an escape)");
                if (ch != '\\') { sb.Append(ch); _i++; continue; }
                _i++;
                if (_i >= _s.Length) throw Fail("unterminated string");
                char e = _s[_i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        {
                            int cp = Hex4();
                            if (cp >= 0xD800 && cp <= 0xDBFF && _i + 1 < _s.Length && _s[_i] == '\\' && _s[_i + 1] == 'u')
                            {
                                int save = _i;
                                _i += 2;
                                int low = Hex4();
                                if (low >= 0xDC00 && low <= 0xDFFF) cp = 0x10000 + ((cp - 0xD800) << 10) + (low - 0xDC00);
                                else _i = save; // not a pair: the next escape stands on its own
                            }
                            if (cp >= 0xD800 && cp <= 0xDFFF) cp = 0xFFFD; // a lone surrogate is not a character
                            LuaText.AppendUtf8(sb, cp);
                            break;
                        }
                    default:
                        _i--;
                        throw Fail($"invalid escape '\\{Show(e)}'");
                }
            }
        }

        private int Hex4()
        {
            if (_i + 4 > _s.Length) throw Fail("incomplete \\u escape");
            int v = 0;
            for (int k = 0; k < 4; k++)
            {
                int d = LuaNumber.HexVal(_s[_i + k]);
                if (d < 0) throw Fail("invalid \\u escape");
                v = v * 16 + d;
            }
            _i += 4;
            return v;
        }
    }

    // ------------------------------------------------------------------ encoding

    private sealed class Writer(LuaState L, Shared shared, int indent, bool sortKeys)
    {
        private readonly StringBuilder _sb = new();
        private readonly HashSet<object> _active = new(ReferenceEqualityComparer.Instance);

        public override string ToString() => _sb.ToString();

        private void NewLine(int depth)
        {
            if (indent == 0) return;
            _sb.Append('\n').Append(' ', depth * indent);
        }

        public void Write(in Value v, int depth)
        {
            switch (v.Kind)
            {
                case ValueKind.Nil: _sb.Append("null"); break;
                case ValueKind.Boolean: _sb.Append(v.AsBool ? "true" : "false"); break;
                case ValueKind.Integer: _sb.Append(v.I.ToString(CultureInfo.InvariantCulture)); break;
                case ValueKind.Float: WriteFloat(v.N); break;
                case ValueKind.String: WriteString(v.AsString); break;
                case ValueKind.Table: WriteTable(v.AsTable, depth); break;
                case ValueKind.Userdata when v.O is LuaList list: WriteList(list, depth); break;
                case ValueKind.Userdata when v.O is LuaDict dict: WriteDict(dict, depth); break;
                default:
                    throw new JsonException(JsonErrorKind.Unsupported, $"cannot encode a {LuaState.TypeNameOf(v)} as JSON");
            }
            L.CheckStringLength(_sb.Length);
        }

        private void WriteList(LuaList list, int depth)
        {
            if (depth >= MaxDepth) throw new JsonException(JsonErrorKind.TooDeep, "value is nested too deeply");
            RuntimeHelpers.EnsureSufficientExecutionStack();
            if (!_active.Add(list)) throw new JsonException(JsonErrorKind.Cycle, "cannot encode a List that contains itself");
            try
            {
                _sb.Append('[');
                for (int i = 0; i < list.Items.Count; i++)
                {
                    if (i > 0) _sb.Append(',');
                    NewLine(depth + 1);
                    Write(list.Items[i], depth + 1);
                }
                if (list.Items.Count > 0) NewLine(depth);
                _sb.Append(']');
            }
            finally { _active.Remove(list); }
        }

        private void WriteDict(LuaDict dict, int depth)
        {
            if (depth >= MaxDepth) throw new JsonException(JsonErrorKind.TooDeep, "value is nested too deeply");
            RuntimeHelpers.EnsureSufficientExecutionStack();
            if (!_active.Add(dict)) throw new JsonException(JsonErrorKind.Cycle, "cannot encode a Dict that contains itself");
            try
            {
                var fields = new List<(string Key, Value Value)>(dict.Entries.Count);
                foreach (KeyValuePair<Value, Value> kv in dict.Entries)
                {
                    string key = kv.Key.Kind switch
                    {
                        ValueKind.String => kv.Key.AsString,
                        ValueKind.Integer or ValueKind.Float => kv.Key.ToString(),
                        _ => throw new JsonException(JsonErrorKind.Unsupported, $"cannot use a {LuaState.TypeNameOf(kv.Key)} as a JSON object key"),
                    };
                    fields.Add((key, kv.Value));
                }
                if (sortKeys) fields.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));
                _sb.Append('{');
                for (int i = 0; i < fields.Count; i++)
                {
                    if (i > 0) _sb.Append(',');
                    NewLine(depth + 1);
                    WriteString(fields[i].Key);
                    _sb.Append(indent > 0 ? ": " : ":");
                    Write(fields[i].Value, depth + 1);
                }
                if (fields.Count > 0) NewLine(depth);
                _sb.Append('}');
            }
            finally { _active.Remove(dict); }
        }

        private void WriteFloat(double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d))
                throw new JsonException(JsonErrorKind.InvalidNumber, "cannot encode nan or inf as JSON");
            string text = d.ToString("R", CultureInfo.InvariantCulture);
            _sb.Append(text);
            // keep it a float for whoever reads it back
            if (text.AsSpan().IndexOfAny('.', 'E', 'e') < 0) _sb.Append(".0");
        }

        private void WriteString(string s)
        {
            _sb.Append('"');
            int run = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                if (ch >= 0x20 && ch != '"' && ch != '\\') continue;
                _sb.Append(s, run, i - run);
                run = i + 1;
                switch (ch)
                {
                    case '"': _sb.Append("\\\""); break;
                    case '\\': _sb.Append("\\\\"); break;
                    case '\n': _sb.Append("\\n"); break;
                    case '\r': _sb.Append("\\r"); break;
                    case '\t': _sb.Append("\\t"); break;
                    case '\b': _sb.Append("\\b"); break;
                    case '\f': _sb.Append("\\f"); break;
                    default: _sb.Append("\\u00").Append(((int)ch).ToString("x2")); break;
                }
            }
            _sb.Append(s, run, s.Length - run).Append('"');
        }

        private void WriteTable(Table t, int depth)
        {
            if (ReferenceEquals(t, shared.Null)) { _sb.Append("null"); return; }
            if (depth >= MaxDepth) throw new JsonException(JsonErrorKind.TooDeep, "value is nested too deeply");
            RuntimeHelpers.EnsureSufficientExecutionStack();
            if (!_active.Add(t)) throw new JsonException(JsonErrorKind.Cycle, "cannot encode a table that contains itself");

            int n = t.Length;
            bool array = ReferenceEquals(t.Metatable, shared.ArrayMeta);
            if (!array && n > 0)
            {
                // a sequence: exactly the keys 1..n
                int count = 0;
                foreach (KeyValuePair<Value, Value> _ in t.Pairs()) count++;
                array = count == n;
            }
            if (array)
            {
                _sb.Append('[');
                for (int i = 1; i <= n; i++)
                {
                    if (i > 1) _sb.Append(',');
                    NewLine(depth + 1);
                    Write(t.GetInt(i), depth + 1);
                }
                if (n > 0) NewLine(depth);
                _sb.Append(']');
            }
            else
            {
                var fields = new List<(string Key, Value Value)>();
                foreach (KeyValuePair<Value, Value> kv in t.Pairs())
                {
                    // private and protected members of an object stay out of its JSON
                    if (t.Guarded && LuaState.IsHiddenFrom(t, kv.Key, null, out _)) continue;
                    string key = kv.Key.Kind switch
                    {
                        ValueKind.String => kv.Key.AsString,
                        ValueKind.Integer or ValueKind.Float => kv.Key.ToString(),
                        _ => throw new JsonException(JsonErrorKind.Unsupported, $"cannot use a {LuaState.TypeNameOf(kv.Key)} as a JSON object key"),
                    };
                    fields.Add((key, kv.Value));
                }
                if (sortKeys) fields.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));
                _sb.Append('{');
                for (int i = 0; i < fields.Count; i++)
                {
                    if (i > 0) _sb.Append(',');
                    NewLine(depth + 1);
                    WriteString(fields[i].Key);
                    _sb.Append(indent == 0 ? ":" : ": ");
                    Write(fields[i].Value, depth + 1);
                }
                if (fields.Count > 0) NewLine(depth);
                _sb.Append('}');
            }
            _active.Remove(t);
        }
    }
}

using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Cameo.Runtime.Vm;

namespace Cameo.Runtime.Stdlib;

/// <summary>A compiled regular expression (userdata).</summary>
public sealed class LuaRegex : IUserdata
{
    public readonly Regex Regex;
    public readonly string Source;
    private readonly Table _meta;
    public LuaRegex(Regex regex, string source, Table meta) { Regex = regex; Source = source; _meta = meta; }
    public string TypeName => "Regex";
    public Table? Metatable { get => _meta; set { } }
}

/// <summary>
/// <c>regex</c>: full regular expressions (.NET syntax: alternation, lookaround, named groups,
/// Unicode classes, ...) next to Lua's patterns.
///
/// Text is treated as UTF-8: <c>.</c> matches a whole character and positions are reported in
/// bytes, as everywhere else in Lua (a string that is not valid UTF-8 is matched byte by byte).
/// One limit comes from the .NET engine, which works on UTF-16: a character outside the Basic
/// Multilingual Plane (most emoji) counts as two for <c>.</c> and for character classes.
/// Flags: <c>i</c> ignore case, <c>m</c> multi-line anchors, <c>s</c> dot matches newline,
/// <c>x</c> ignore pattern whitespace, <c>n</c> linear-time matching (no backtracking; such
/// patterns cannot use backreferences or lookaround). Without <c>n</c> a match that runs for
/// longer than <see cref="MatchTimeout"/> is stopped and raises a <c>RegexError</c> of kind
/// "timeout", so a hostile pattern cannot hang the VM.
/// </summary>
public static class RegexLib
{
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(5);

    private static readonly ConcurrentDictionary<(string Pattern, string Flags), Regex> Cache = new();

    /// <summary>Compiles a pattern given in the VM's byte-string form (shared with stream:expect).</summary>
    internal static Regex? TryCompile(string pattern, string flags, out string? message)
    {
        message = null;
        if (Cache.TryGetValue((pattern, flags), out Regex? cached)) return cached;
        var options = RegexOptions.CultureInvariant;
        foreach (char f in flags)
        {
            switch (f)
            {
                case 'i': options |= RegexOptions.IgnoreCase; break;
                case 'm': options |= RegexOptions.Multiline; break;
                case 's': options |= RegexOptions.Singleline; break;
                case 'x': options |= RegexOptions.IgnorePatternWhitespace; break;
                case 'n': options |= RegexOptions.NonBacktracking; break;
                default:
                    message = $"unknown flag '{f}' (flags are i, m, s, x, n)";
                    return null;
            }
        }
        try
        {
            var r = new Regex(new Subject(pattern).Text, options, MatchTimeout);
            if (Cache.Count > 500) Cache.Clear();
            Cache[(pattern, flags)] = r;
            return r;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException)
        {
            message = e.Message;
            return null;
        }
    }

    // A subject string seen as text, with the way back from text positions to byte positions.
    internal readonly struct Subject
    {
        public readonly string Text;
        private readonly int[]? _byteAt; // byte offset of each UTF-16 position; null = identity
        public readonly bool Utf8;

        public Subject(string bytes)
        {
            bool ascii = true;
            foreach (char ch in bytes) if (ch >= 0x80) { ascii = false; break; }
            if (ascii) { Text = bytes; _byteAt = null; Utf8 = false; return; }
            try
            {
                Text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(LuaText.ToBytes(bytes));
            }
            catch (DecoderFallbackException)
            {
                Text = bytes; _byteAt = null; Utf8 = false; // not UTF-8: match the bytes themselves
                return;
            }
            Utf8 = true;
            _byteAt = new int[Text.Length + 1];
            int b = 0;
            for (int i = 0; i < Text.Length; i++)
            {
                _byteAt[i] = b;
                if (char.IsHighSurrogate(Text[i]) && i + 1 < Text.Length) { _byteAt[i + 1] = b; i++; b += 4; }
                else b += Text[i] < 0x80 ? 1 : Text[i] < 0x800 ? 2 : 3;
            }
            _byteAt[Text.Length] = b;
        }

        public int ByteAt(int textIndex) => _byteAt == null ? textIndex : _byteAt[textIndex];

        /// <summary>Text index of a 0-based byte offset (rounded up to a character start).</summary>
        public int TextIndex(int byteOffset)
        {
            if (_byteAt == null) return Math.Min(byteOffset, Text.Length);
            int i = Array.BinarySearch(_byteAt, byteOffset);
            if (i < 0) return Math.Min(~i, Text.Length);
            while (i > 0 && _byteAt[i - 1] == byteOffset) i--; // first of a surrogate pair
            return i;
        }

        public Value Out(string text) => Value.FromString(Utf8 ? LuaText.Encode(text) : text);

        /// <summary>The text range [index, index + length) widened to whole characters. The
        /// engine works on UTF-16 units, so a match can end in the middle of a character
        /// outside the Basic Multilingual Plane; reported matches never do.</summary>
        public (int Start, int End) Whole(int index, int length)
        {
            int a = index, b = index + length;
            if (!Utf8) return (a, b);
            if (a > 0 && a < Text.Length && char.IsLowSurrogate(Text[a]) && char.IsHighSurrogate(Text[a - 1])) a--;
            if (b > 0 && b < Text.Length && char.IsLowSurrogate(Text[b]) && char.IsHighSurrogate(Text[b - 1])) b++;
            return (a, b);
        }
        public string In(string bytes) => Utf8 ? LuaText.Decode(bytes) : bytes;
    }

    public static void Open(LuaState L)
    {
        var regex = new Table();
        var meta = new Table();
        var methods = new Table();
        var errorMeta = new Table();
        errorMeta.SetStr("__name", "RegexError");
        L.Register(errorMeta, "__tostring", static (ref CallContext c) =>
        {
            Table t = c.CheckTable(0);
            return c.Return($"RegexError({t.GetStr("kind")}): {t.GetStr("message")}");
        });
        Value Error(string kind, string message)
        {
            var t = new Table(0, 2);
            t.SetStr("kind", kind);
            t.SetStr("message", LuaText.Encode(message));
            t.Metatable = errorMeta;
            return Value.FromTable(t);
        }

        // Compiles (or finds in the cache) a pattern; null plus an error value when it is invalid.
        Regex? Compile(string pattern, string flags, out Value error)
        {
            Regex? r = TryCompile(pattern, flags, out string? message);
            error = r == null ? Error("invalid_pattern", message!) : default;
            return r;
        }

        // The regex of a call: a compiled Regex (methods), or subject-then-pattern (module functions).
        Regex Target(ref CallContext c, out int subjectArg, out int next, bool allowFlagsArg = false)
        {
            if (c[0].O is LuaRegex compiled) { subjectArg = 1; next = 2; return compiled.Regex; }
            subjectArg = 0;
            next = 2;
            if (c[1].O is LuaRegex compiledArg) return compiledArg.Regex;
            string pattern = c.CheckString(1);
            string flags = allowFlagsArg && !c.IsNoneOrNil(2) ? c.CheckString(2) : "";
            return Compile(pattern, flags, out Value error) ?? throw new LuaException(error, true);
        }

        Value MatchValue(in Subject subject, Match m)
        {
            var t = new Table(0, 4);
            var (a, b) = subject.Whole(m.Index, m.Length);
            t.SetStr("text", subject.Out(subject.Text.Substring(a, b - a)));
            t.SetStr("start", subject.ByteAt(a) + 1);
            t.SetStr("finish", subject.ByteAt(b));
            var groups = new Table(m.Groups.Count - 1, 0);
            Table? named = null;
            for (int g = 1; g < m.Groups.Count; g++)
            {
                Group group = m.Groups[g];
                if (!group.Success) continue; // an unmatched group is nil
                var (ga, gb) = subject.Whole(group.Index, group.Length);
                Value text = subject.Out(subject.Text.Substring(ga, gb - ga));
                groups.SetInt(g, text);
                if (!int.TryParse(group.Name, out _)) (named ??= new Table()).Set(Value.FromText(group.Name), text);
            }
            t.SetStr("groups", groups);
            if (named != null) t.SetStr("named", named);
            return Value.FromTable(t);
        }

        T Guard<T>(Func<T> run)
        {
            try
            {
                return run();
            }
            catch (RegexMatchTimeoutException)
            {
                throw new LuaException(Error("timeout", $"the match took longer than {MatchTimeout.TotalSeconds:0} seconds"), true);
            }
        }

        // new(pattern [, flags]) -> Regex | nil, RegexError
        L.Register(regex, "new", (ref CallContext c) =>
        {
            string pattern = c.CheckString(0);
            string flags = c.OptString(1, "");
            Regex? r = Compile(pattern, flags, out Value error);
            return r == null ? c.Return(Value.Nil, error) : c.Return(Value.FromUserdata(new LuaRegex(r, pattern, meta)), Value.Nil);
        });
        L.Register(regex, "escape", static (ref CallContext c) => c.Return(Regex.Escape(c.CheckString(0))));

        NativeFn test = (ref CallContext c) =>
        {
            Regex r = Target(ref c, out int at, out _, allowFlagsArg: true);
            var subject = new Subject(c.CheckString(at));
            return c.Return(Guard(() => r.IsMatch(subject.Text)));
        };
        // match(s [, init]) -> RegexMatch | nil; init is a 1-based byte position
        NativeFn match = (ref CallContext c) =>
        {
            Regex r = Target(ref c, out int at, out int next);
            var subject = new Subject(c.CheckString(at));
            long init = c.OptInt(next, 1);
            int from = subject.TextIndex((int)Math.Clamp(init - 1, 0, int.MaxValue));
            Match m = Guard(() => r.Match(subject.Text, from));
            return c.Return(m.Success ? MatchValue(subject, m) : Value.Nil);
        };
        NativeFn matches = (ref CallContext c) =>
        {
            Regex r = Target(ref c, out int at, out _);
            var subject = new Subject(c.CheckString(at));
            var list = new Table();
            MatchCollection all = Guard(() => { MatchCollection mc = r.Matches(subject.Text); _ = mc.Count; return mc; });
            foreach (Match m in all) list.Append(MatchValue(subject, m));
            return c.Return(Value.FromTable(list));
        };
        // replace(s, replacement [, max]) -> string, count. The replacement is text with $1,
        // ${name}, $0 and $$ substitutions, or a function from the match to the new text.
        NativeFn replace = (ref CallContext c) =>
        {
            Regex r = Target(ref c, out int at, out int next);
            var subject = new Subject(c.CheckString(at));
            Value rep = c[next];
            long max = c.OptInt(next + 1, -1);
            if (max == 0) return c.Return(c[at], 0);
            int limit = max < 0 ? -1 : (int)Math.Min(max, int.MaxValue);
            int count = 0;
            LuaState state = c.L;
            string result;
            if (rep.IsString)
            {
                string template = subject.In(rep.AsString);
                // count first: Regex.Replace does not report how many it made
                result = Guard(() => r.Replace(subject.Text, m => { count++; return m.Result(template); }, limit));
            }
            else if (rep.IsFunction)
            {
                result = Guard(() => r.Replace(subject.Text, m =>
                {
                    count++;
                    Value v = state.Call1(rep, MatchValue(subject, m));
                    if (v.IsFalsy) return m.Value; // nil or false keeps the original text, as in gsub
                    if (!v.IsString && !v.IsNumber) throw new LuaException($"invalid replacement value (a {LuaState.TypeNameOf(v)})");
                    return subject.In(v.ToString());
                }, limit));
            }
            else throw c.TypeError(next, "string or function");
            Value text = subject.Out(result);
            state.CheckStringLength(text.AsString.Length);
            return c.Return(text, count);
        };
        NativeFn split = (ref CallContext c) =>
        {
            Regex r = Target(ref c, out int at, out int next);
            var subject = new Subject(c.CheckString(at));
            long max = c.OptInt(next, long.MaxValue);
            var list = new Table();
            int pos = 0;
            MatchCollection all = Guard(() => { MatchCollection mc = r.Matches(subject.Text); _ = mc.Count; return mc; });
            foreach (Match m in all)
            {
                if (max-- <= 0) break;
                if (m.Length == 0 && (m.Index == 0 || m.Index == subject.Text.Length)) { max++; continue; } // no empty piece at either end
                list.Append(subject.Out(subject.Text.Substring(pos, m.Index - pos)));
                pos = m.Index + m.Length;
            }
            list.Append(subject.Out(subject.Text.Substring(pos)));
            return c.Return(Value.FromTable(list));
        };

        foreach (var (name, fn) in new (string, NativeFn)[] { ("test", test), ("match", match), ("matches", matches), ("replace", replace), ("split", split) })
        {
            L.Register(regex, name, fn);
            L.RegisterInstance<LuaRegex>(methods, name, fn);
        }
        // Short forms as string methods, in the spirit of Perl's =~ :
        //   s:retest(pat)        true or false
        //   s:rematch(pat)       the captures (or the whole match when the pattern has none), like string.match
        //   s:resub(pat, repl)   replace all; returns the string and the count
        //   s:resplit(pat)       pieces between matches
        Table str = L.Globals.GetStr("string").AsTable;
        L.RegisterInstance<string>(str, "retest", test);
        L.RegisterInstance<string>(str, "resub", replace);
        L.RegisterInstance<string>(str, "resplit", split);
        L.RegisterInstance<string>(str, "rematch", (ref CallContext c) =>
        {
            Regex r = Target(ref c, out int at, out int next);
            var subject = new Subject(c.CheckString(at));
            long init = c.OptInt(next, 1);
            int from = subject.TextIndex((int)Math.Clamp(init - 1, 0, int.MaxValue));
            Match m = Guard(() => r.Match(subject.Text, from));
            if (!m.Success) return c.Return(Value.Nil);
            if (m.Groups.Count == 1) return c.Return(subject.Out(m.Value));
            var captures = new Value[m.Groups.Count - 1];
            for (int g = 1; g < m.Groups.Count; g++)
                captures[g - 1] = m.Groups[g].Success ? subject.Out(m.Groups[g].Value) : Value.Nil;
            return c.Return(captures);
        });

        meta.SetStr("__index", methods);
        meta.SetStr("__name", "Regex");
        L.Register(meta, "__tostring", static (ref CallContext c) =>
            c.Return("Regex(" + ((c[0].O as LuaRegex)?.Source ?? "?") + ")"));

        L.Globals.SetStr("regex", regex);
        L.LoadedModules.SetStr("regex", regex);
    }
}

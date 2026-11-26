using System.Text;
using System.Text.RegularExpressions;
using Cameo.Runtime.Vm;

namespace Cameo.Runtime.Stdlib;

/// <summary>
/// Text and file tools in the Perl tradition: <c>csv</c>, <c>table.render</c> (aligned
/// columns), <c>string.tr</c> (character translation), <c>fs.glob</c> and <c>fs.walk</c>.
/// </summary>
public static class TextToolsLib
{
    public static void Open(LuaState L)
    {
        OpenCsv(L);
        Table table = L.Globals.GetStr("table").AsTable;
        L.Register(table, "render", Render);
        Table str = L.Globals.GetStr("string").AsTable;
        L.RegisterInstance<string>(str, "tr", Tr);
        if (L.Globals.GetStr("fs").O is not Table fs) return; // the file-system library is not part of this VM
        L.Register(fs, "glob", Glob);
        L.Register(fs, "walk", Walk);
    }

    // ------------------------------------------------------------------ csv

    private static void OpenCsv(LuaState L)
    {
        var csv = new Table();
        Table errorMeta = L.MetaFor("CsvError");
        L.Register(errorMeta, "__tostring", static (ref CallContext c) =>
        {
            Table t = c.CheckTable(0);
            return c.Return($"CsvError({t.GetStr("kind")}): {t.GetStr("message")}");
        });

        // decode(text [, {header = true, separator = ","}]) -> rows | nil, CsvError
        // With a header (the default) each row is a table from column name to text; without,
        // a list of texts. Fields are always strings: convert with tonumber where needed.
        L.Register(csv, "decode", (ref CallContext c) =>
        {
            string text = c.CheckString(0);
            bool header = true;
            char sep = ',';
            if (!c.IsNoneOrNil(1))
            {
                Table o = c.CheckTable(1);
                if (o.GetStr("header").IsBoolean) header = o.GetStr("header").AsBool;
                sep = Separator(ref c, o);
            }
            var records = new List<List<string>>();
            var record = new List<string>();
            var field = new StringBuilder();
            int line = 1, i = 0;
            bool any = false; // something has been read for the current record
            if (text.StartsWith("ï»¿", StringComparison.Ordinal)) i = 3; // UTF-8 byte order mark
            while (i < text.Length)
            {
                char ch = text[i];
                if (ch == '"' && field.Length == 0)
                {
                    int startLine = line;
                    i++;
                    while (true)
                    {
                        if (i >= text.Length)
                            return c.Return(Value.Nil, Error(errorMeta, "syntax", $"unterminated quoted field starting on line {startLine}", startLine));
                        if (text[i] == '"')
                        {
                            if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i += 2; continue; }
                            i++;
                            break;
                        }
                        if (text[i] == '\n') line++;
                        field.Append(text[i++]);
                    }
                    if (i < text.Length && text[i] != sep && text[i] != '\n' && text[i] != '\r')
                        return c.Return(Value.Nil, Error(errorMeta, "syntax", $"text after a closing quote on line {line}", line));
                    any = true;
                    continue;
                }
                if (ch == sep) { record.Add(field.ToString()); field.Clear(); any = true; i++; continue; }
                if (ch == '\n' || ch == '\r')
                {
                    if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    i++;
                    if (any || field.Length > 0) { record.Add(field.ToString()); records.Add(record); } // a blank line is not a record
                    record = new List<string>();
                    field.Clear();
                    any = false;
                    line++;
                    continue;
                }
                field.Append(ch);
                any = true;
                i++;
            }
            if (any || field.Length > 0) { record.Add(field.ToString()); records.Add(record); }

            var rows = new Table();
            int first = 0;
            List<string>? names = null;
            if (header && records.Count > 0) { names = records[0]; first = 1; }
            for (int r = first; r < records.Count; r++)
            {
                List<string> rec = records[r];
                var row = new Table();
                if (names == null) foreach (string f in rec) row.Append(Value.FromString(f));
                else
                {
                    if (rec.Count > names.Count)
                        return c.Return(Value.Nil, Error(errorMeta, "syntax", $"record {r + 1} has {rec.Count} fields but the header has {names.Count}", r + 1));
                    for (int k = 0; k < rec.Count; k++) row.SetStr(names[k], Value.FromString(rec[k]));
                }
                rows.Append(Value.FromTable(row));
            }
            return c.Return(Value.FromTable(rows), Value.Nil);
        });

        // encode(rows [, {columns = {...}, header = true, separator = ","}]) -> text
        // Rows are records (then `columns` gives the order; default: the keys of the first row,
        // sorted) or lists. Fields are quoted only when they need it.
        L.Register(csv, "encode", (ref CallContext c) =>
        {
            Table rows = c.CheckTable(0);
            bool header = true;
            char sep = ',';
            List<Value>? columns = null;
            if (!c.IsNoneOrNil(1))
            {
                Table o = c.CheckTable(1);
                if (o.GetStr("header").IsBoolean) header = o.GetStr("header").AsBool;
                sep = Separator(ref c, o);
                if (o.GetStr("columns").O is Table cols) columns = Enumerable.Range(1, cols.Length).Select(i => cols.GetInt(i)).ToList();
            }
            var sb = new StringBuilder();
            LuaState state = c.L;
            void Field(in Value v, bool firstField)
            {
                if (!firstField) sb.Append(sep);
                string s = v.IsNil ? "" : v.IsString ? v.AsString : state.ToStringMeta(v);
                bool quote = s.Length > 0 && (s.AsSpan().IndexOfAny(sep, '"', '\n') >= 0 || s.Contains('\r') || s[0] == ' ' || s[^1] == ' ');
                if (!quote) { sb.Append(s); return; }
                sb.Append('"').Append(s.Replace("\"", "\"\"")).Append('"');
            }
            int n = rows.Length;
            bool records = n > 0 && rows.GetInt(1).O is Table firstRow && firstRow.Length == 0 && firstRow.Pairs().Any();
            if (records || columns != null)
            {
                columns ??= Columns(rows);
                if (header)
                {
                    for (int k = 0; k < columns.Count; k++) Field(columns[k], k == 0);
                    sb.Append('\n');
                }
            }
            for (int r = 1; r <= n; r++)
            {
                if (rows.GetInt(r).O is not Table row) throw c.ArgError(0, $"row {r} is not a table");
                if (columns != null) for (int k = 0; k < columns.Count; k++) Field(row.Get(columns[k]), k == 0);
                else for (int k = 1, m = row.Length; k <= m; k++) Field(row.GetInt(k), k == 1);
                sb.Append('\n');
                c.L.CheckStringLength(sb.Length);
            }
            return c.Return(sb.ToString());
        });
        L.Globals.SetStr("csv", csv);
        L.LoadedModules.SetStr("csv", csv);
    }

    private static char Separator(ref CallContext c, Table options)
    {
        Value s = options.GetStr("separator");
        if (s.IsNil) return ',';
        if (!s.IsString || s.AsString.Length != 1 || s.AsString[0] is '"' or '\n' or '\r') throw c.ArgError(1, "field 'separator' must be one character");
        return s.AsString[0];
    }

    private static Value Error(Table meta, string kind, string message, int line)
    {
        var t = new Table(0, 3);
        t.SetStr("kind", kind);
        t.SetStr("message", message);
        t.SetStr("line", line);
        t.Metatable = meta;
        return Value.FromTable(t);
    }

    // The string keys of the first row, sorted: a stable, predictable column order.
    private static List<Value> Columns(Table rows)
    {
        var names = new List<string>();
        if (rows.GetInt(1).O is Table first)
            foreach (KeyValuePair<Value, Value> kv in first.Pairs())
                if (kv.Key.IsString) names.Add(kv.Key.AsString);
        names.Sort(StringComparer.Ordinal);
        return names.Select(Value.FromString).ToList();
    }

    // ------------------------------------------------------------------ table.render

    // render(rows [, columns]): the rows as text in aligned columns with a header line.
    // Numbers are right-aligned. Column widths count characters (UTF-8 aware), not bytes.
    private static int Render(ref CallContext c)
    {
        Table rows = c.CheckTable(0);
        LuaState state = c.L;
        Table? given = c.IsNoneOrNil(1) ? null : c.CheckTable(1);
        List<Value> columns = given == null ? Columns(rows) : Enumerable.Range(1, given.Length).Select(i => given.GetInt(i)).ToList();
        if (columns.Count == 0) return c.Return("");
        int n = rows.Length;
        var cells = new string[n + 1][];
        var numeric = new bool[columns.Count];
        Array.Fill(numeric, true);
        cells[0] = columns.Select(k => k.IsString ? k.AsString : state.ToStringMeta(k)).ToArray();
        for (int r = 1; r <= n; r++)
        {
            if (rows.GetInt(r).O is not Table row) throw c.ArgError(0, $"row {r} is not a table");
            cells[r] = new string[columns.Count];
            for (int k = 0; k < columns.Count; k++)
            {
                Value v = row.Get(columns[k]);
                cells[r][k] = v.IsNil ? "" : v.IsString ? v.AsString : c.L.ToStringMeta(v);
                if (!v.IsNil && !v.IsNumber) numeric[k] = false;
            }
        }
        static int Width(string s) { int w = 0; foreach (char ch in s) if ((ch & 0xC0) != 0x80) w++; return w; }
        int[] widths = Enumerable.Range(0, columns.Count).Select(k => cells.Max(row => Width(row[k]))).ToArray();
        var sb = new StringBuilder();
        void Line(string[] row)
        {
            for (int k = 0; k < row.Length; k++)
            {
                if (k > 0) sb.Append("  ");
                int pad = widths[k] - Width(row[k]);
                if (numeric[k]) sb.Append(' ', pad).Append(row[k]);
                else { sb.Append(row[k]); if (k < row.Length - 1) sb.Append(' ', pad); }
            }
            sb.Append('\n');
        }
        Line(cells[0]);
        for (int k = 0; k < columns.Count; k++) { if (k > 0) sb.Append("  "); sb.Append('-', widths[k]); }
        sb.Append('\n');
        for (int r = 1; r <= n; r++) Line(cells[r]);
        c.L.CheckStringLength(sb.Length);
        return c.Return(sb.ToString());
    }

    // ------------------------------------------------------------------ string.tr

    // tr(s, from, to): each character of `from` becomes the character at the same place in
    // `to`. Both may hold ranges ("a-z"); a short `to` repeats its last character; an empty
    // `to` deletes. Returns the new string and how many characters were changed or deleted.
    private static int Tr(ref CallContext c)
    {
        string s = c.CheckString(0);
        string from = Expand(ref c, 1), to = Expand(ref c, 2);
        var map = new int[256];
        Array.Fill(map, -2); // -2: unchanged, -1: delete
        for (int i = 0; i < from.Length; i++)
        {
            if (map[from[i]] != -2) continue; // the first mention of a character wins
            map[from[i]] = to.Length == 0 ? -1 : to[Math.Min(i, to.Length - 1)];
        }
        var sb = new StringBuilder(s.Length);
        long count = 0;
        foreach (char ch in s)
        {
            int m = map[ch];
            if (m == -2) { sb.Append(ch); continue; }
            count++;
            if (m >= 0) sb.Append((char)m);
        }
        return c.Return(sb.ToString(), count);
    }

    private static string Expand(ref CallContext c, int arg)
    {
        string spec = c.CheckString(arg);
        var sb = new StringBuilder();
        for (int i = 0; i < spec.Length; i++)
        {
            if (i + 2 < spec.Length && spec[i + 1] == '-')
            {
                if (spec[i + 2] < spec[i]) throw c.ArgError(arg, $"range '{spec.Substring(i, 3)}' is backwards");
                for (int ch = spec[i]; ch <= spec[i + 2]; ch++) sb.Append((char)ch);
                i += 2;
            }
            else sb.Append(spec[i]);
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ fs.glob / fs.walk

    /// <summary>Translates a glob (<c>*</c>, <c>?</c>, <c>**</c>, <c>[a-z]</c>, <c>{a,b}</c>) to a regex over '/'-separated paths.</summary>
    internal static Regex GlobToRegex(string glob)
    {
        var sb = new StringBuilder("^");
        int braces = 0;
        for (int i = 0; i < glob.Length; i++)
        {
            char ch = glob[i];
            switch (ch)
            {
                case '*':
                    if (i + 1 < glob.Length && glob[i + 1] == '*')
                    {
                        // "**/" matches any number of directories, including none; a bare "**" matches anything
                        if (i + 2 < glob.Length && glob[i + 2] == '/') { sb.Append("(?:[^/]+/)*"); i += 2; }
                        else { sb.Append(".*"); i++; }
                    }
                    else sb.Append("[^/]*");
                    break;
                case '?': sb.Append("[^/]"); break;
                case '[':
                    {
                        int close = glob.IndexOf(']', i + 2 <= glob.Length ? i + 2 : i + 1);
                        if (close < 0) { sb.Append("\\["); break; }
                        string body = glob.Substring(i + 1, close - i - 1);
                        if (body.StartsWith('!')) body = "^" + body.Substring(1);
                        sb.Append('[').Append(body.Replace("\\", "\\\\")).Append(']');
                        i = close;
                        break;
                    }
                case '{': sb.Append("(?:"); braces++; break;
                case '}' when braces > 0: sb.Append(')'); braces--; break;
                case ',' when braces > 0: sb.Append('|'); break;
                default: sb.Append(Regex.Escape(ch.ToString())); break;
            }
        }
        while (braces-- > 0) sb.Append(')');
        return new Regex(sb.Append('$').ToString(), RegexOptions.CultureInvariant);
    }

    private static readonly EnumerationOptions Everything = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.None, // hidden files too: a glob says what it wants
        IgnoreInaccessible = true,
        ReturnSpecialDirectories = false,
    };

    // glob(pattern [, base]): the paths matching the pattern, sorted. Relative patterns are
    // resolved against base (default: the working directory) and come back relative to it.
    private static int Glob(ref CallContext c)
    {
        string pattern = c.CheckText(0).Replace('\\', '/');
        string @base = c.IsNoneOrNil(1) ? Directory.GetCurrentDirectory() : Path.GetFullPath(c.CheckText(1));
        bool absolute = Path.IsPathRooted(pattern);
        // the directory part before the first wildcard is where the search starts
        int wild = pattern.IndexOfAny(['*', '?', '[', '{']);
        int cut = wild < 0 ? pattern.LastIndexOf('/') : pattern.LastIndexOf('/', wild);
        string prefix = cut < 0 ? "" : pattern.Substring(0, cut + 1);
        string rest = pattern.Substring(prefix.Length);
        string root = absolute ? (prefix.Length == 0 ? "/" : prefix) : Path.Combine(@base, prefix);
        var found = new List<string>();
        if (c.L.AuthorizeFile(root, write: false, out string canonicalRoot) && Directory.Exists(canonicalRoot))
        {
            Regex re = GlobToRegex(rest);
            bool deep = rest.Contains("**") || rest.Contains('/');
            var options = new EnumerationOptions { RecurseSubdirectories = deep, AttributesToSkip = FileAttributes.None, IgnoreInaccessible = true };
            string full = Path.GetFullPath(canonicalRoot);
            foreach (string path in Directory.EnumerateFileSystemEntries(full, "*", options))
            {
                if (!c.L.AuthorizeFile(path, write: false, out _)) continue;
                string rel = Path.GetRelativePath(full, path).Replace('\\', '/');
                if (re.IsMatch(rel)) found.Add(prefix + rel);
                c.L.CheckArrayLength(found.Count);
            }
        }
        found.Sort(StringComparer.Ordinal);
        var t = new Table(found.Count, 0);
        foreach (string p in found) t.Append(Value.FromText(p));
        return c.Return(Value.FromTable(t));
    }

    // walk(dir): every file and directory below dir, sorted by path, as
    // {path =, name =, isdir =, size =, modified =}; nil and an IoError if dir cannot be read.
    private static int Walk(ref CallContext c)
    {
        string dir = c.CheckText(0);
        LuaState state = c.L;
        if (!state.AuthorizeFile(dir, write: false, out string canonicalDir))
            return c.Return(Value.Nil, new IoError(IoErrorKind.PermissionDenied, "file access is not permitted by host file policy", dir).ToValue(state));
        try
        {
            var entries = new DirectoryInfo(canonicalDir).EnumerateFileSystemInfos("*", Everything)
                .Where(e => state.AuthorizeFile(e.FullName, write: false, out _))
                .OrderBy(e => e.FullName, StringComparer.Ordinal).ToList();
            c.L.CheckArrayLength(entries.Count);
            var t = new Table(entries.Count, 0);
            foreach (FileSystemInfo e in entries)
            {
                var row = new Table(0, 5);
                row.SetStr("path", Value.FromText(Path.Combine(dir, Path.GetRelativePath(canonicalDir, e.FullName))));
                row.SetStr("name", Value.FromText(e.Name));
                bool isDir = (e.Attributes & FileAttributes.Directory) != 0;
                row.SetStr("isdir", isDir);
                row.SetStr("size", e is FileInfo f && !isDir ? f.Length : 0);
                row.SetStr("modified", (e.LastWriteTimeUtc - DateTime.UnixEpoch).TotalSeconds);
                t.Append(Value.FromTable(row));
            }
            return c.Return(Value.FromTable(t), Value.Nil);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return c.Return(Value.Nil, IoError.From(e, dir, timedOut: false).ToValue(c.L));
        }
    }
}

using System.Globalization;
using System.Text;
using Cameo.Runtime.Vm;

namespace Cameo.Runtime.Stdlib;

/// <summary>
/// <c>cli</c>: a script declares its command-line parameters once and gets parsing, type
/// conversion, validation and <c>--help</c> from the declaration.
///
/// <c>cli.parse(spec)</c> returns the values, or prints the help (exit 0) or the problem and a
/// usage line (exit 2). <c>cli.tryParse(spec [, argv])</c> returns <c>values, CliError</c>
/// instead of exiting, and <c>cli.usage(spec)</c> the help text.
/// </summary>
public static class CliLib
{
    private sealed record Param(string Name, string? Short, string Type, Value Default, string Help, bool Required, bool Multiple)
    {
        public string Key => Name.Replace('-', '_'); // the field of the result: "dry-run" is values.dry_run
    }

    private sealed class CliFailure(string kind, string message) : Exception(message)
    {
        public string Kind => kind;
    }

    public static void Open(LuaState L)
    {
        var cli = new Table();
        Table errorMeta = L.MetaFor("CliError");
        L.Register(errorMeta, "__tostring", static (ref CallContext c) =>
        {
            Table t = c.CheckTable(0);
            return c.Return($"CliError({t.GetStr("kind")}): {t.GetStr("message")}");
        });
        L.Register(cli, "usage", static (ref CallContext c) => c.Return(Usage(ref c, c.CheckTable(0))));
        L.Register(cli, "tryParse", (ref CallContext c) =>
        {
            Table spec = c.CheckTable(0);
            try
            {
                return c.Return(Value.FromTable(Parse(ref c, spec, Argv(ref c, 1))), Value.Nil);
            }
            catch (CliFailure f)
            {
                var t = new Table(0, 2);
                t.SetStr("kind", f.Kind);
                t.SetStr("message", LuaText.Encode(f.Message));
                t.Metatable = errorMeta;
                return c.Return(Value.Nil, Value.FromTable(t));
            }
        });
        L.Register(cli, "parse", static (ref CallContext c) =>
        {
            Table spec = c.CheckTable(0);
            try
            {
                return c.Return(Value.FromTable(Parse(ref c, spec, Argv(ref c, 1))));
            }
            catch (CliFailure f) when (f.Kind == "help")
            {
                c.L.WriteOut(Usage(ref c, spec));
                c.L.FlushOut();
                throw new LuaExitException(0);
            }
            catch (CliFailure f)
            {
                string name = Text(spec, "name") ?? "script";
                c.L.WriteErr(LuaText.Encode($"{name}: {f.Message}\n(--help shows the options)\n"));
                c.L.FlushErr();
                throw new LuaExitException(2);
            }
        });
        L.Globals.SetStr("cli", cli);
        L.LoadedModules.SetStr("cli", cli);
    }

    private static string? Text(Table t, string field) => t.GetStr(field).IsString ? LuaText.Decode(t.GetStr(field).AsString) : null;

    private static List<string> Argv(ref CallContext c, int arg)
    {
        if (c.IsNoneOrNil(arg)) return c.L.ScriptArgs.ToList();
        Table t = c.CheckTable(arg);
        var list = new List<string>();
        for (int i = 1; i <= t.Length; i++) list.Add(LuaText.Decode(t.GetInt(i).ToString()));
        return list;
    }

    private static List<Param> Params(ref CallContext c, Table spec, string field, bool positional)
    {
        var list = new List<Param>();
        if (spec.GetStr(field).O is not Table items) return list;
        for (int i = 1; i <= items.Length; i++)
        {
            if (items.GetInt(i).O is not Table p || Text(p, "name") is not { Length: > 0 } name)
                throw c.ArgError(0, $"{field}[{i}] needs a 'name'");
            string type = Text(p, "type") ?? "string";
            if (type is not ("string" or "integer" or "number" or "boolean")) throw c.ArgError(0, $"{field}[{i}]: unknown type '{type}'");
            if (positional && type == "boolean") throw c.ArgError(0, $"{field}[{i}]: a positional argument cannot be a boolean");
            string? shortName = Text(p, "short");
            if (shortName != null && shortName.Length != 1) throw c.ArgError(0, $"{field}[{i}]: 'short' must be one character");
            Value required = p.GetStr("required");
            list.Add(new Param(name, shortName, type, p.GetStr("default"), Text(p, "help") ?? "",
                required.IsNil ? positional && p.GetStr("default").IsNil : required.IsTruthy,
                p.GetStr(positional ? "rest" : "multiple").IsTruthy));
        }
        return list;
    }

    private static Value Convert(Param p, string text)
    {
        switch (p.Type)
        {
            case "integer":
                if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long i)) return Value.FromInt(i);
                throw new CliFailure("invalid_value", $"'{text}' is not a whole number (for {Shown(p)})");
            case "number":
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return Value.FromFloat(d);
                throw new CliFailure("invalid_value", $"'{text}' is not a number (for {Shown(p)})");
            case "boolean":
                return text.ToLowerInvariant() switch
                {
                    "true" or "yes" or "1" or "on" => Value.True,
                    "false" or "no" or "0" or "off" => Value.False,
                    _ => throw new CliFailure("invalid_value", $"'{text}' is not true or false (for {Shown(p)})"),
                };
            default:
                return Value.FromText(text);
        }
    }

    private static string Shown(Param p) => p.Name.StartsWith('<') ? p.Name : "--" + p.Name;

    private static Table Parse(ref CallContext c, Table spec, List<string> argv)
    {
        List<Param> options = Params(ref c, spec, "options", positional: false);
        List<Param> positionals = Params(ref c, spec, "positionals", positional: true);
        var values = new Table();
        var given = new List<string>();
        void Set(Param p, Value v)
        {
            if (!p.Multiple) { values.SetStr(p.Key, v); return; }
            if (values.GetStr(p.Key).O is not Table list) { list = new Table(); values.SetStr(p.Key, list); }
            list.Append(v);
        }
        for (int i = 0; i < argv.Count; i++)
        {
            string a = argv[i];
            if (a == "--") { given.AddRange(argv.Skip(i + 1)); break; }
            if (a is "--help" or "-h") throw new CliFailure("help", "help requested");
            // a lone "-" (standard input by convention) and negative numbers are values, not options
            if (!a.StartsWith('-') || a == "-" || (a.Length > 1 && (char.IsAsciiDigit(a[1]) || a[1] == '.'))) { given.Add(a); continue; }
            string name, shown;
            string? inline = null;
            Param? p;
            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                int eq = a.IndexOf('=');
                name = eq < 0 ? a.Substring(2) : a.Substring(2, eq - 2);
                if (eq >= 0) inline = a.Substring(eq + 1);
                shown = "--" + name;
                p = options.Find(o => o.Name == name);
                if (p == null && name.StartsWith("no-", StringComparison.Ordinal) && options.Find(o => o.Name == name.Substring(3)) is { Type: "boolean" } negated)
                {
                    if (inline != null) throw new CliFailure("invalid_value", $"{shown} takes no value");
                    Set(negated, Value.False);
                    continue;
                }
            }
            else
            {
                name = a.Substring(1, 1);
                shown = "-" + name;
                if (a.Length > 2) inline = a.Substring(2); // -n5
                p = options.Find(o => o.Short == name);
            }
            if (p == null) throw new CliFailure("unknown_option", $"unknown option {shown}");
            if (p.Type == "boolean" && inline == null) { Set(p, Value.True); continue; }
            if (inline == null)
            {
                if (i + 1 >= argv.Count) throw new CliFailure("missing_value", $"{shown} needs a value");
                inline = argv[++i];
            }
            Set(p, Convert(p, inline));
        }
        int next = 0;
        foreach (Param p in positionals)
        {
            Param named = p with { Name = "<" + p.Name + ">" };
            if (p.Multiple)
            {
                var rest = new Table();
                while (next < given.Count) rest.Append(Convert(named, given[next++]));
                if (rest.Length == 0 && p.Required) throw new CliFailure("missing_argument", $"missing argument <{p.Name}>");
                values.SetStr(p.Key, rest);
            }
            else if (next < given.Count) values.SetStr(p.Key, Convert(named, given[next++]));
            else if (p.Required) throw new CliFailure("missing_argument", $"missing argument <{p.Name}>");
            else values.SetStr(p.Key, p.Default);
        }
        if (next < given.Count) throw new CliFailure("too_many_arguments", $"unexpected argument '{given[next]}'");
        foreach (Param p in options)
        {
            if (!values.GetStr(p.Key).IsNil) continue;
            if (p.Required) throw new CliFailure("missing_option", $"--{p.Name} is required");
            if (!p.Default.IsNil) values.SetStr(p.Key, p.Default);
            else if (p.Type == "boolean") values.SetStr(p.Key, Value.False);
            else if (p.Multiple) values.SetStr(p.Key, new Table());
        }
        return values;
    }

    private static string Usage(ref CallContext c, Table spec)
    {
        List<Param> options = Params(ref c, spec, "options", positional: false);
        List<Param> positionals = Params(ref c, spec, "positionals", positional: true);
        var sb = new StringBuilder();
        sb.Append("Usage: ").Append(Text(spec, "name") ?? "script");
        if (options.Count > 0) sb.Append(" [options]");
        foreach (Param p in positionals)
            sb.Append(' ').Append(p.Required ? $"<{p.Name}>" : $"[{p.Name}]").Append(p.Multiple ? "..." : "");
        sb.Append('\n');
        if (Text(spec, "description") is { } description) sb.Append('\n').Append(description).Append('\n');
        var rows = new List<(string Left, string Right)>();
        foreach (Param p in positionals) rows.Add(($"<{p.Name}>", p.Help));
        if (rows.Count > 0) { sb.Append("\nArguments:\n"); Rows(sb, rows); rows.Clear(); }
        foreach (Param p in options)
        {
            string left = (p.Short != null ? $"-{p.Short}, " : "    ") + "--" + p.Name + (p.Type == "boolean" ? "" : $" <{p.Type}>");
            string right = p.Help;
            if (p.Required) right += " (required)";
            else if (!p.Default.IsNil) right += $" (default: {LuaText.Decode(p.Default.ToString())})";
            if (p.Multiple) right += " (may be repeated)";
            rows.Add((left, right.Trim()));
        }
        rows.Add(("-h, --help", "show this help"));
        sb.Append("\nOptions:\n");
        Rows(sb, rows);
        return LuaText.Encode(sb.ToString());
    }

    private static void Rows(StringBuilder sb, List<(string Left, string Right)> rows)
    {
        int width = rows.Max(r => r.Left.Length);
        foreach (var (left, right) in rows)
            sb.Append("  ").Append(right.Length == 0 ? left : left.PadRight(width + 2) + right).Append('\n');
    }
}

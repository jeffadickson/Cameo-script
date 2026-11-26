using System.Diagnostics;
using System.Globalization;
using System.Text;
using Cameo.Runtime.Vm;

namespace Cameo.Runtime.Stdlib;

public static class OsLib
{
    public static void Open(LuaState L, bool sandboxed = false)
    {
        var os = new Table();
        L.Register(os, "time", Time);
        L.Register(os, "clock", Clock);
        L.Register(os, "date", Date);
        L.Register(os, "difftime", static (ref CallContext c) => c.Return(Value.FromFloat(c.CheckNumber(0) - c.CheckNumber(1))));
        L.Register(os, "exit", Exit);
        if (!sandboxed)
        {
            L.Register(os, "getenv", static (ref CallContext c) => c.ReturnText(Environment.GetEnvironmentVariable(c.CheckText(0))));
            L.Register(os, "remove", Remove);
            L.Register(os, "rename", Rename);
            L.Register(os, "tmpname", static (ref CallContext c) =>
            {
                if (c.L.FilePolicy != null && !c.L.AuthorizeFile(Path.GetTempPath(), write: true, out _))
                    throw new LuaException("os.tmpname: permission denied by host file policy");
                return c.ReturnText(Path.GetTempFileName());
            });
            L.Register(os, "setlocale", static (ref CallContext c) =>
            {
                string loc = c.OptString(0, "C");
                return c.Return(loc is "" or "C" or "POSIX" ? Value.FromString("C") : Value.Nil);
            });
        }
        L.Globals.SetStr("os", os);
        L.LoadedModules.SetStr("os", os);
    }

    private static int Clock(ref CallContext c) =>
        c.Return(Value.FromFloat(Process.GetCurrentProcess().TotalProcessorTime.TotalSeconds));

    private static long GetField(ref CallContext c, Table t, string key, long def, bool required)
    {
        Value v = t.GetStr(key);
        if (v.IsNil)
        {
            if (required) throw new LuaException($"field '{key}' missing in date table");
            return def;
        }
        if (!v.TryToInteger(out long r))
        {
            if (v.IsNumber) throw new LuaException($"field '{key}' is not an integer");
            throw new LuaException($"field '{key}' missing in date table");
        }
        return r;
    }

    private static int Time(ref CallContext c)
    {
        if (c.IsNoneOrNil(0)) return c.Return(Value.FromInt(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        Table t = c.CheckTable(0);
        long year = GetField(ref c, t, "year", 0, true);
        long month = GetField(ref c, t, "month", 0, true);
        long day = GetField(ref c, t, "day", 0, true);
        long hour = GetField(ref c, t, "hour", 12, false);
        long min = GetField(ref c, t, "min", 0, false);
        long sec = GetField(ref c, t, "sec", 0, false);
        DateTime dt;
        try
        {
            // Normalize out-of-range fields the way mktime does.
            dt = new DateTime(1, 1, 1, 0, 0, 0, DateTimeKind.Unspecified)
                .AddYears((int)year - 1).AddMonths((int)month - 1).AddDays(day - 1)
                .AddHours(hour).AddMinutes(min).AddSeconds(sec);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new LuaException("time result cannot be represented in this installation");
        }
        TimeSpan off = TimeZoneInfo.Local.GetUtcOffset(dt);
        long unix = new DateTimeOffset(dt, off).ToUnixTimeSeconds();
        FillDateTable(t, dt, TimeZoneInfo.Local.IsDaylightSavingTime(dt));
        return c.Return(Value.FromInt(unix));
    }

    private static void FillDateTable(Table t, DateTime dt, bool isDst)
    {
        t.SetStr("year", Value.FromInt(dt.Year));
        t.SetStr("month", Value.FromInt(dt.Month));
        t.SetStr("day", Value.FromInt(dt.Day));
        t.SetStr("hour", Value.FromInt(dt.Hour));
        t.SetStr("min", Value.FromInt(dt.Minute));
        t.SetStr("sec", Value.FromInt(dt.Second));
        t.SetStr("wday", Value.FromInt((int)dt.DayOfWeek + 1));
        t.SetStr("yday", Value.FromInt(dt.DayOfYear));
        t.SetStr("isdst", isDst);
    }

    private static int Date(ref CallContext c)
    {
        string fmt = c.OptString(0, "%c");
        long time = c.IsNoneOrNil(1) ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() : c.CheckInt(1);
        bool utc = false;
        int p = 0;
        if (fmt.Length > 0 && fmt[0] == '!')
        {
            utc = true;
            p = 1;
        }
        DateTimeOffset dto;
        try
        {
            dto = DateTimeOffset.FromUnixTimeSeconds(time);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new LuaException("date result cannot be represented in this installation");
        }
        DateTime dt;
        TimeSpan offset;
        bool isDst = false;
        string zone;
        if (utc)
        {
            dt = dto.UtcDateTime;
            offset = TimeSpan.Zero;
            zone = "GMT";
        }
        else
        {
            dt = TimeZoneInfo.ConvertTime(dto, TimeZoneInfo.Local).DateTime;
            offset = TimeZoneInfo.Local.GetUtcOffset(dto);
            isDst = TimeZoneInfo.Local.IsDaylightSavingTime(dto);
            zone = isDst ? TimeZoneInfo.Local.DaylightName : TimeZoneInfo.Local.StandardName;
            if (zone.Contains(' ') || zone.Length == 0) zone = offset == TimeSpan.Zero ? "UTC" : zone;
        }
        if (string.CompareOrdinal(fmt, p, "*t", 0, 2) == 0)
        {
            var t = new Table(0, 9);
            FillDateTable(t, dt, isDst);
            return c.Return(Value.FromTable(t));
        }
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        for (int i = p; i < fmt.Length; i++)
        {
            char ch = fmt[i];
            if (ch != '%') { sb.Append(ch); continue; }
            if (++i >= fmt.Length) throw c.ArgError(0, "invalid conversion specifier '%'");
            char f = fmt[i];
            if ((f == 'E' || f == 'O') && i + 1 < fmt.Length) f = fmt[++i]; // locale modifiers are ignored
            switch (f)
            {
                case 'a': sb.Append(dt.ToString("ddd", ci)); break;
                case 'A': sb.Append(dt.ToString("dddd", ci)); break;
                case 'b': case 'h': sb.Append(dt.ToString("MMM", ci)); break;
                case 'B': sb.Append(dt.ToString("MMMM", ci)); break;
                case 'c': sb.Append(dt.ToString("ddd MMM ", ci)).Append(dt.Day.ToString().PadLeft(2)).Append(dt.ToString(" HH:mm:ss yyyy", ci)); break;
                case 'C': sb.Append((dt.Year / 100).ToString("00")); break;
                case 'd': sb.Append(dt.Day.ToString("00")); break;
                case 'D': sb.Append(dt.ToString("MM/dd/yy", ci)); break;
                case 'e': sb.Append(dt.Day.ToString().PadLeft(2)); break;
                case 'F': sb.Append(dt.ToString("yyyy-MM-dd", ci)); break;
                case 'H': sb.Append(dt.Hour.ToString("00")); break;
                case 'I': sb.Append((dt.Hour % 12 == 0 ? 12 : dt.Hour % 12).ToString("00")); break;
                case 'j': sb.Append(dt.DayOfYear.ToString("000")); break;
                case 'm': sb.Append(dt.Month.ToString("00")); break;
                case 'M': sb.Append(dt.Minute.ToString("00")); break;
                case 'n': sb.Append('\n'); break;
                case 'p': sb.Append(dt.Hour < 12 ? "AM" : "PM"); break;
                case 'r': sb.Append((dt.Hour % 12 == 0 ? 12 : dt.Hour % 12).ToString("00")).Append(dt.ToString(":mm:ss", ci)).Append(dt.Hour < 12 ? " AM" : " PM"); break;
                case 'R': sb.Append(dt.ToString("HH:mm", ci)); break;
                case 's': sb.Append(time.ToString(ci)); break;
                case 'S': sb.Append(dt.Second.ToString("00")); break;
                case 't': sb.Append('\t'); break;
                case 'T': case 'X': sb.Append(dt.ToString("HH:mm:ss", ci)); break;
                case 'u': sb.Append(dt.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)dt.DayOfWeek); break;
                case 'w': sb.Append((int)dt.DayOfWeek); break;
                case 'x': sb.Append(dt.ToString("MM/dd/yy", ci)); break;
                case 'y': sb.Append((dt.Year % 100).ToString("00")); break;
                case 'Y': sb.Append(dt.Year.ToString(ci)); break;
                case 'z':
                    sb.Append(offset < TimeSpan.Zero ? '-' : '+').Append(Math.Abs(offset.Hours).ToString("00")).Append(Math.Abs(offset.Minutes).ToString("00"));
                    break;
                case 'Z': sb.Append(zone); break;
                case 'U': sb.Append(((dt.DayOfYear + 6 - (int)dt.DayOfWeek) / 7).ToString("00")); break;
                case 'W': sb.Append(((dt.DayOfYear + 6 - ((int)dt.DayOfWeek + 6) % 7) / 7).ToString("00")); break;
                case 'G': sb.Append(ISOWeek.GetYear(dt).ToString(ci)); break;
                case 'g': sb.Append((ISOWeek.GetYear(dt) % 100).ToString("00")); break;
                case 'V': sb.Append(ISOWeek.GetWeekOfYear(dt).ToString("00")); break;
                case '%': sb.Append('%'); break;
                default:
                    throw c.ArgError(0, $"invalid conversion specifier '%{f}'");
            }
        }
        return c.Return(sb.ToString());
    }

    private static int Remove(ref CallContext c)
    {
        string name = c.CheckString(0);
        try
        {
            string path = LuaText.Decode(name);
            if (!c.L.AuthorizeFile(path, write: true, out string canonical))
                return c.Return(Value.Nil, name + ": Permission denied by host file policy", Value.FromInt(13));
            if (File.Exists(canonical)) File.Delete(canonical);
            else if (Directory.Exists(canonical)) Directory.Delete(canonical, false);
            else return c.Return(Value.Nil, name + ": No such file or directory", Value.FromInt(2));
            return c.Return(Value.True);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return c.Return(Value.Nil, name + ": " + e.Message, Value.FromInt(e is UnauthorizedAccessException ? 13 : 5));
        }
    }

    private static int Rename(ref CallContext c)
    {
        string from = c.CheckString(0), to = c.CheckString(1);
        try
        {
            string pf = LuaText.Decode(from), pt = LuaText.Decode(to);
            if (!c.L.AuthorizeFile(pf, write: true, out string cf) || !c.L.AuthorizeFile(pt, write: true, out string ct))
                return c.Return(Value.Nil, from + ": Permission denied by host file policy", Value.FromInt(13));
            if (File.Exists(cf)) File.Move(cf, ct, true);
            else if (Directory.Exists(cf)) Directory.Move(cf, ct);
            else return c.Return(Value.Nil, from + ": No such file or directory", Value.FromInt(2));
            return c.Return(Value.True);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return c.Return(Value.Nil, from + ": " + e.Message, Value.FromInt(e is UnauthorizedAccessException ? 13 : 5));
        }
    }

    private static int Exit(ref CallContext c)
    {
        int code;
        Value v = c[0];
        if (v.IsNil || (v.IsBoolean && v.IsTruthy)) code = 0;
        else if (v.IsBoolean) code = 1;
        else code = (int)c.CheckInt(0);
        c.L.FlushOut();
        c.L.FlushErr();
        // Unwinds the whole VM (pcall cannot catch it); the host decides what exiting means.
        throw new LuaExitException(code);
    }
}

using System.Diagnostics;
using System.Globalization;
using Cameo.Runtime.Vm;

namespace Cameo.Runtime.Stdlib;

/// <summary>
/// <c>time</c>: clocks, sleeping, and formatting/parsing of dates with time zones. A point in
/// time is a number: seconds since 1970-01-01 UTC, with a fraction. Formats are .NET custom
/// date format strings ("yyyy-MM-dd HH:mm"); zones are "utc" (the default), "local", or an
/// IANA name such as "Europe/Berlin". <c>format</c> and <c>parse</c> return the result, or nil
/// and a typed <c>TimeError</c>.
/// </summary>
public static class TimeLib
{
    private const string Iso = "yyyy-MM-dd'T'HH:mm:ss.fffzzz";

    public static void Open(LuaState L)
    {
        var time = new Table();
        var errorMeta = new Table();
        errorMeta.SetStr("__name", "TimeError");
        L.Register(errorMeta, "__tostring", static (ref CallContext c) =>
        {
            Table t = c.CheckTable(0);
            return c.Return($"TimeError({t.GetStr("kind")}): {t.GetStr("message")}");
        });

        L.Register(time, "now", static (ref CallContext c) => c.Return((DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds));
        L.Register(time, "millis", static (ref CallContext c) => c.Return(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        L.Register(time, "monotonic", static (ref CallContext c) => c.Return(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency));
        // sleep(seconds): other tasks run meanwhile; the watchdog can interrupt it
        L.Register(time, "sleep", static (ref CallContext c) =>
        {
            double seconds = c.CheckNumber(0);
            var fut = new LuaFuture(c.L);
            c.L.Scheduler.AddTimer(seconds, () => fut.Complete(Value.True));
            return FutureLib.WaitFor(ref c, fut, throws: true, timeout: -1);
        });
        L.Register(time, "format", (ref CallContext c) =>
        {
            double t = c.IsNoneOrNil(0) ? (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds : c.CheckNumber(0);
            string? format = c.IsNoneOrNil(1) ? null : c.CheckText(1);
            if (!TryZone(ref c, 2, out TimeZoneInfo zone, out string? zoneError))
                return c.Return(Value.Nil, Error(errorMeta, "unknown_zone", zoneError!));
            if (double.IsNaN(t) || Math.Abs(t) > 253402300799.0)
                return c.Return(Value.Nil, Error(errorMeta, "invalid_time", "time is out of range"));
            DateTimeOffset at = TimeZoneInfo.ConvertTime(DateTimeOffset.UnixEpoch.AddTicks((long)Math.Round(t * TimeSpan.TicksPerSecond)), zone);
            try
            {
                string text = at.ToString(format ?? Iso, CultureInfo.InvariantCulture);
                if (format == null && at.Offset == TimeSpan.Zero) text = text[..^6] + "Z";
                return c.Return(Value.FromText(text), Value.Nil);
            }
            catch (FormatException e)
            {
                return c.Return(Value.Nil, Error(errorMeta, "invalid_format", e.Message));
            }
        });
        L.Register(time, "parse", (ref CallContext c) =>
        {
            string text = c.CheckText(0);
            string? format = c.IsNoneOrNil(1) ? null : c.CheckText(1);
            if (!TryZone(ref c, 2, out TimeZoneInfo zone, out string? zoneError))
                return c.Return(Value.Nil, Error(errorMeta, "unknown_zone", zoneError!));
            // RoundtripKind tells an explicit offset or "Z" (Local/Utc) from none (Unspecified)
            DateTime dt;
            bool ok;
            try
            {
                ok = format == null
                    ? DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out dt)
                    : DateTime.TryParseExact(text, format, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out dt);
            }
            catch (FormatException e)
            {
                return c.Return(Value.Nil, Error(errorMeta, "invalid_format", e.Message));
            }
            if (!ok) return c.Return(Value.Nil, Error(errorMeta, "invalid_time", $"'{text}' is not a date/time" + (format == null ? "" : $" in the format '{format}'")));
            DateTime utc = dt.Kind == DateTimeKind.Unspecified
                ? dt - zone.GetUtcOffset(dt) // a wall-clock time in the given zone
                : dt.ToUniversalTime();
            return c.Return((DateTime.SpecifyKind(utc, DateTimeKind.Utc) - DateTime.UnixEpoch).TotalSeconds, Value.Nil);
        });

        L.Globals.SetStr("time", time);
        L.LoadedModules.SetStr("time", time);
    }

    private static bool TryZone(ref CallContext c, int arg, out TimeZoneInfo zone, out string? error)
    {
        error = null;
        zone = TimeZoneInfo.Utc;
        if (c.IsNoneOrNil(arg)) return true;
        string name = c.CheckText(arg);
        if (name.Equals("utc", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Equals("local", StringComparison.OrdinalIgnoreCase)) { zone = TimeZoneInfo.Local; return true; }
        if (TimeZoneInfo.TryFindSystemTimeZoneById(name, out TimeZoneInfo? found)) { zone = found; return true; }
        error = $"unknown time zone '{name}'";
        return false;
    }

    private static Value Error(Table meta, string kind, string message)
    {
        var t = new Table(0, 2);
        t.SetStr("kind", kind);
        t.SetStr("message", LuaText.Encode(message));
        t.Metatable = meta;
        return Value.FromTable(t);
    }
}

using System.Globalization;
using System.Text;

namespace Cameo.Embed;

/// <summary>Reason why an on-target <c>Cameo.Embed</c> debug session stopped.</summary>
public enum EmbedDebugStopKind
{
    /// <summary>Paused at function entry (via <c>script start</c>).</summary>
    Paused,
    /// <summary>Paused after executing a single VM instruction.</summary>
    Stepped,
    /// <summary>Hit an installed PC/line breakpoint.</summary>
    Breakpoint,
    /// <summary>Triggered a hardware watchpoint with <c>WatchFlags::kBreak</c>.</summary>
    WatchpointHit,
    /// <summary>Execution returned normally from the target function.</summary>
    Completed,
    /// <summary>Target returned a fault or protocol error.</summary>
    Error,
}

/// <summary>Structured stop event returned by <see cref="EmbedRemoteDebugSession"/>.</summary>
public sealed record EmbedDebugStop(
    EmbedDebugStopKind Kind,
    int ProtoId,
    int Pc,
    int SourceLine,
    long? ReturnValue = null,
    ulong? WatchAddress = null,
    string? RawResponse = null);

/// <summary>Snapshot of the active target frame registers returned by <c>script regs</c>.</summary>
public sealed record EmbedDebugRegisters(
    bool IsPaused,
    int ProtoId,
    int Pc,
    int SourceLine,
    int CallDepth,
    IReadOnlyDictionary<int, string> Registers);

/// <summary>
/// Source-mapped host remote debugger translating Cameo source lines (via <see cref="EmbedDebugMap"/>)
/// to target <c>(proto, pc)</c> breakpoints and single-stepping over NexRT's <c>MonitorBridge</c>
/// with zero debug metadata stored in target Flash.
/// </summary>
public sealed class EmbedRemoteDebugSession
{
    private readonly IEmbedLineTransport _transport;
    private readonly EmbedDebugMap _map;
    private readonly TimeSpan _timeout;

    /// <summary>Creates a remote debug session bound to a compiled <see cref="EmbedDebugMap"/> and transport.</summary>
    public EmbedRemoteDebugSession(IEmbedLineTransport transport, EmbedDebugMap map, TimeSpan? timeout = null)
    {
        _transport = transport;
        _map = map;
        _timeout = timeout ?? TimeSpan.FromSeconds(5);
    }

    /// <summary>Sets a target PC breakpoint at the first instruction emitted for 1-based Cameo source <paramref name="line"/>.</summary>
    public bool SetBreakpointAtLine(int line, out int protoId, out int pc)
    {
        if (!_map.TryFindLocationForLine(line, out protoId, out pc))
            return false;
        string? resp = EmbedUploader.SendAndAwaitPrefix(
            _transport, $"script break {protoId} {pc}", ["ok ", "err "], _timeout);
        return resp is not null && resp.StartsWith("ok bp set", StringComparison.Ordinal);
    }

    /// <summary>Clears all breakpoints on the target VM.</summary>
    public bool ClearBreakpoints()
    {
        string? resp = EmbedUploader.SendAndAwaitPrefix(_transport, "script clearbp", ["ok ", "err "], _timeout);
        return resp is not null && resp.StartsWith("ok clearbp", StringComparison.Ordinal);
    }

    /// <summary>Prepares <paramref name="exportName"/> paused at PC 0 for single-stepping.</summary>
    public EmbedDebugStop Start(string exportName, params long[] args)
    {
        var sb = new StringBuilder("script start ").Append(exportName);
        foreach (long a in args)
            sb.Append(' ').Append(a.ToString(CultureInfo.InvariantCulture));
        string? resp = EmbedUploader.SendAndAwaitPrefix(_transport, sb.ToString(), ["ok ", "err "], _timeout);
        return ParseStop(resp);
    }

    /// <summary>Executes exactly one VM instruction on the target.</summary>
    public EmbedDebugStop StepInstruction()
    {
        string? resp = EmbedUploader.SendAndAwaitPrefix(_transport, "script step", ["ok", "err "], _timeout);
        return ParseStop(resp);
    }

    /// <summary>Steps VM instructions until the mapped Cameo source line changes, execution completes, or a watchpoint hits.</summary>
    public EmbedDebugStop StepLine(int maxInstructions = 256)
    {
        EmbedDebugStop stop = StepInstruction();
        int startLine = stop.SourceLine;
        for (int i = 1; i < maxInstructions && stop.Kind == EmbedDebugStopKind.Stepped; i++)
        {
            if (stop.SourceLine > 0 && startLine > 0 && stop.SourceLine != startLine)
                break;
            stop = StepInstruction();
        }
        return stop;
    }

    /// <summary>Resumes execution until completion, breakpoint, or watchpoint.</summary>
    public EmbedDebugStop Continue()
    {
        string? resp = EmbedUploader.SendAndAwaitPrefix(_transport, "script cont", ["ok", "err "], _timeout);
        return ParseStop(resp);
    }

    /// <summary>Reads the active target frame location and non-nil register snapshot via <c>script regs</c>.</summary>
    public EmbedDebugRegisters ReadRegisters()
    {
        string? resp = EmbedUploader.SendAndAwaitPrefix(_transport, "script regs", ["paused=", "err "], _timeout);
        bool paused = false;
        int proto = 0, pc = 0, depth = 0;
        var regs = new Dictionary<int, string>();
        if (resp is not null)
        {
            foreach (string tok in resp.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = tok.IndexOf('=');
                if (eq <= 0 || eq + 1 >= tok.Length) continue;
                string k = tok[..eq];
                string v = tok[(eq + 1)..];
                switch (k)
                {
                    case "paused": paused = v == "1"; break;
                    case "proto": int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out proto); break;
                    case "pc": int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out pc); break;
                    case "depth": int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out depth); break;
                    default:
                        if (k.Length >= 2 && k[0] == 'r' && int.TryParse(k[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int rIdx))
                            regs[rIdx] = v;
                        break;
                }
            }
        }
        return new EmbedDebugRegisters(paused, proto, pc, _map.ResolveLine(proto, pc), depth, regs);
    }

    /// <summary>Fetches all captured AXI bus-trace events from the target via <c>script bustrace</c>.</summary>
    public IReadOnlyList<BusTraceRecord> CaptureBusTrace()
    {
        var lines = new List<string>();
        _ = EmbedUploader.SendAndAwaitPrefix(_transport, "script bustrace", ["ok bustrace", "err "], _timeout, lines);
        return BusTraceFstExporter.ParseBusTraceText(string.Join('\n', lines));
    }

    private EmbedDebugStop ParseStop(string? resp)
    {
        if (resp is null || resp.StartsWith("err ", StringComparison.Ordinal))
            return new EmbedDebugStop(EmbedDebugStopKind.Error, 0, 0, 0, RawResponse: resp);

        int proto = ParseIntField(resp, "proto=");
        int pc = ParseIntField(resp, "pc=");
        int line = _map.ResolveLine(proto, pc);

        if (resp.StartsWith("ok paused", StringComparison.Ordinal))
            return new EmbedDebugStop(EmbedDebugStopKind.Paused, proto, pc, line, RawResponse: resp);
        if (resp.StartsWith("ok stepped", StringComparison.Ordinal))
            return new EmbedDebugStop(EmbedDebugStopKind.Stepped, proto, pc, line, RawResponse: resp);
        if (resp.StartsWith("ok bp ", StringComparison.Ordinal))
            return new EmbedDebugStop(EmbedDebugStopKind.Breakpoint, proto, pc, line, RawResponse: resp);
        if (resp.StartsWith("ok watch ", StringComparison.Ordinal))
        {
            ulong? watchAddr = ParseHexField(resp, "addr=0x");
            return new EmbedDebugStop(EmbedDebugStopKind.WatchpointHit, proto, pc, line, WatchAddress: watchAddr, RawResponse: resp);
        }
        if (resp.StartsWith("ok", StringComparison.Ordinal))
        {
            long? ret = TryParseLongField(resp, "ret=");
            return new EmbedDebugStop(EmbedDebugStopKind.Completed, 0, 0, 0, ReturnValue: ret, RawResponse: resp);
        }
        return new EmbedDebugStop(EmbedDebugStopKind.Error, proto, pc, line, RawResponse: resp);
    }

    private static int ParseIntField(string line, string key)
    {
        long? v = TryParseLongField(line, key);
        return v.HasValue ? (int)v.Value : 0;
    }

    private static long? TryParseLongField(string line, string key)
    {
        int idx = line.IndexOf(key, StringComparison.Ordinal);
        if (idx < 0) return null;
        string rest = line[(idx + key.Length)..];
        int end = rest.IndexOf(' ');
        if (end >= 0) rest = rest[..end];
        return long.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : null;
    }

    private static ulong? ParseHexField(string line, string key)
    {
        int idx = line.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        string rest = line[(idx + key.Length)..];
        int end = rest.IndexOf(' ');
        if (end >= 0) rest = rest[..end];
        return ulong.TryParse(rest, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong v) ? v : null;
    }
}

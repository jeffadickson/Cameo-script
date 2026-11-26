using System.Buffers.Binary;
using System.Globalization;
using Cameo.Fst;

namespace Cameo.Embed;

/// <summary>Captured AXI/MMIO bus transaction event (matches <c>cameo::embed::BusTraceEvent</c>).</summary>
public sealed record BusTraceRecord(
    uint Sequence,
    bool IsWrite,
    byte Width,
    ulong Address,
    ulong Value,
    bool WatchpointHit);

/// <summary>Captured NexRT <c>rtos::trace::Event</c> from an <c>NXTR</c> binary flight-recorder dump.</summary>
public sealed record NexrtTraceEvent(
    ulong Timestamp,
    ushort EventId,
    ushort SourceId,
    uint Arg);

/// <summary>
/// Parses <c>Cameo.Embed</c> <c>script bustrace</c> text logs and NexRT <c>NXTR</c> binary
/// <c>TraceRing</c> dumps, exporting them to GTKWave-compatible <c>.fst</c> waveform files
/// via <see cref="FstWriter"/>.
/// </summary>
public static class BusTraceFstExporter
{
    /// <summary>Magic word for NexRT binary <c>rtos::trace::DumpHeader</c> (<c>"NXTR"</c> LE).</summary>
    public const uint NexrtDumpMagic = 0x5254584Eu;

    /// <summary>
    /// Parses lines emitted by <c>MonitorBridge</c> <c>script bustrace</c>
    /// (<c>bt seq=N wr=0|1 w=W addr=0x... val=0x... wp=0|1</c>).
    /// </summary>
    public static IReadOnlyList<BusTraceRecord> ParseBusTraceText(string text)
    {
        var result = new List<BusTraceRecord>();
        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim();
            if (!line.StartsWith("bt ", StringComparison.Ordinal))
                continue;

            uint seq = 0;
            bool wr = false;
            byte width = 4;
            ulong addr = 0;
            ulong val = 0;
            bool wp = false;

            foreach (string tok in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = tok.IndexOf('=');
                if (eq <= 0 || eq + 1 >= tok.Length) continue;
                string k = tok[..eq];
                string v = tok[(eq + 1)..];
                switch (k)
                {
                    case "seq":
                        uint.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out seq);
                        break;
                    case "wr":
                        wr = v == "1";
                        break;
                    case "w":
                        byte.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out width);
                        break;
                    case "addr":
                        addr = ParseHexOrDec(v);
                        break;
                    case "val":
                        val = ParseHexOrDec(v);
                        break;
                    case "wp":
                        wp = v == "1";
                        break;
                }
            }
            result.Add(new BusTraceRecord(seq, wr, width, addr, val, wp));
        }
        return result;
    }

    /// <summary>Parses a NexRT binary <c>NXTR</c> v1 <c>TraceRing::serialize</c> buffer.</summary>
    public static IReadOnlyList<NexrtTraceEvent> ParseNexrtTraceDump(ReadOnlySpan<byte> data)
    {
        if (data.Length < 16)
            throw new InvalidDataException("NXTR trace dump is shorter than the 16-byte header.");
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(data[0..4]);
        if (magic != NexrtDumpMagic)
            throw new InvalidDataException($"Invalid NXTR magic 0x{magic:X8}.");
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(data[4..8]);
        if (version != 1u)
            throw new InvalidDataException($"Unsupported NXTR version {version}.");
        uint retained = BinaryPrimitives.ReadUInt32LittleEndian(data[8..12]);
        int maxEvents = (data.Length - 16) / 16;
        int count = (int)Math.Min(retained, (uint)maxEvents);
        var events = new List<NexrtTraceEvent>(count);
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> entry = data.Slice(16 + i * 16, 16);
            ulong ts = BinaryPrimitives.ReadUInt64LittleEndian(entry[0..8]);
            ushort eid = BinaryPrimitives.ReadUInt16LittleEndian(entry[8..10]);
            ushort src = BinaryPrimitives.ReadUInt16LittleEndian(entry[10..12]);
            uint arg = BinaryPrimitives.ReadUInt32LittleEndian(entry[12..16]);
            events.Add(new NexrtTraceEvent(ts, eid, src, arg));
        }
        return events;
    }

    /// <summary>Writes AXI/MMIO bus trace records to a GTKWave-compatible <c>.fst</c> waveform file.</summary>
    public static void WriteBusTraceFst(
        string outputPath,
        IReadOnlyList<BusTraceRecord> records,
        ulong stepTimeNs = 10)
    {
        using var writer = new FstWriter(outputPath, timescaleExponent: -9, version: "Cameo.Embed BusTrace");
        writer.PushScope("cameo_axi");
        int hSeq = writer.AddVariable("axi_seq", 32);
        int hAddr = writer.AddVariable("axi_addr", 32);
        int hData = writer.AddVariable("axi_data", 32);
        int hWrite = writer.AddVariable("axi_write", 1);
        int hWidth = writer.AddVariable("axi_width", 4);
        int hWatch = writer.AddVariable("watchpoint_hit", 1);
        writer.PopScope();

        ulong time = 0;
        foreach (BusTraceRecord r in records)
        {
            writer.SetTime(time);
            writer.EmitInteger(hSeq, r.Sequence);
            writer.EmitInteger(hAddr, unchecked((long)(r.Address & 0xFFFFFFFFUL)));
            writer.EmitInteger(hData, unchecked((long)(r.Value & 0xFFFFFFFFUL)));
            writer.EmitInteger(hWrite, r.IsWrite ? 1 : 0);
            writer.EmitInteger(hWidth, r.Width);
            writer.EmitInteger(hWatch, r.WatchpointHit ? 1 : 0);
            time += Math.Max(1UL, stepTimeNs);
        }
        if (records.Count > 0)
            writer.SetTime(time);
        writer.Close();
    }

    /// <summary>Writes NexRT <c>TraceRing</c> events to a GTKWave-compatible <c>.fst</c> waveform file.</summary>
    public static void WriteNexrtTraceFst(
        string outputPath,
        IReadOnlyList<NexrtTraceEvent> events)
    {
        using var writer = new FstWriter(outputPath, timescaleExponent: -6, version: "NexRT TraceRing");
        writer.PushScope("nexrt_trace");
        int hEvent = writer.AddVariable("event_id", 16);
        int hSource = writer.AddVariable("source_id", 16);
        int hArg = writer.AddVariable("event_arg", 32);
        writer.PopScope();

        ulong lastTime = 0;
        for (int i = 0; i < events.Count; i++)
        {
            NexrtTraceEvent ev = events[i];
            ulong t = ev.Timestamp;
            if (i > 0 && t <= lastTime) t = lastTime + 1;
            writer.SetTime(t);
            writer.EmitInteger(hEvent, ev.EventId);
            writer.EmitInteger(hSource, ev.SourceId);
            writer.EmitInteger(hArg, ev.Arg);
            lastTime = t;
        }
        if (events.Count > 0)
            writer.SetTime(lastTime + 1);
        writer.Close();
    }

    /// <summary>
    /// Auto-detects whether <paramref name="inputPath"/> is an <c>NXTR</c> binary dump or
    /// <c>script bustrace</c> text log and exports it to <paramref name="outputFstPath"/>.
    /// Returns the number of events exported.
    /// </summary>
    public static int ExportFileToFst(string inputPath, string outputFstPath)
    {
        byte[] raw = File.ReadAllBytes(inputPath);
        if (raw.Length >= 16 && BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(0, 4)) == NexrtDumpMagic)
        {
            var events = ParseNexrtTraceDump(raw);
            WriteNexrtTraceFst(outputFstPath, events);
            return events.Count;
        }
        string text = System.Text.Encoding.UTF8.GetString(raw);
        var records = ParseBusTraceText(text);
        WriteBusTraceFst(outputFstPath, records);
        return records.Count;
    }

    private static ulong ParseHexOrDec(string s)
    {
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return ulong.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hx) ? hx : 0UL;
        return ulong.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong dec) ? dec : 0UL;
    }
}

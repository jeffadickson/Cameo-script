using System.Buffers.Binary;
using System.Text;
using static Cameo.Fst.FstFormat;

namespace Cameo.Fst;

/// <summary>A scope or variable of an FST file.</summary>
public sealed record FstSignal(
    string Name,            // full name, scopes joined with '.'
    string Scope,
    int Handle,             // 1, 2, ...; variables that are aliases of each other share one
    int Width,              // bits; 64 for a real, 0 for a string
    FstVarType Type,
    FstDirection Direction)
{
    public bool IsReal => FstFormat.IsReal(Type);
    public bool IsString => Type == FstVarType.String || (Width == 0 && !IsReal);
}

public sealed record FstScope(string Name, FstScopeType Type);

/// <summary>The changes of one variable: a time and a value each.</summary>
public sealed class FstTrace
{
    public readonly List<ulong> Times = new();
    /// <summary>Bit variables: the characters ('0' '1' 'x' 'z' ...), most significant first. Strings: the bytes.</summary>
    public readonly List<byte[]> Values = new();
    /// <summary>Real variables.</summary>
    public readonly List<double> Reals = new();
}

/// <summary>
/// Reads an FST waveform file: the header, the scopes and variables, and the value changes of
/// chosen variables. Reads files written by GTKWave's library (as used by Verilator, Icarus
/// Verilog, GHDL, vcd2fst, ...) with any of its three compression choices.
/// </summary>
public sealed class FstReader : IDisposable
{
    private readonly Stream _stream;
    private readonly List<(long Position, long Length, FstBlock Type)> _valueBlocks = new();
    private int[] _widths = [];            // per handle: bits; 0 = variable length
    private bool[] _real = [];
    private bool _doublesLittleEndian = true;

    public ulong StartTime { get; private set; }
    public ulong EndTime { get; private set; }
    public int TimescaleExponent { get; private set; }
    public string Version { get; private set; } = "";
    public string Date { get; private set; } = "";
    public long TimeZero { get; private set; }
    public int HandleCount => _widths.Length;
    public IReadOnlyList<FstSignal> Signals { get; private set; } = [];
    public IReadOnlyList<FstScope> Scopes { get; private set; } = [];

    public FstReader(string path) : this(File.OpenRead(path)) { }

    public FstReader(Stream stream)
    {
        _stream = stream;
        try { Open(); }
        catch { _stream.Dispose(); throw; }
    }

    public void Dispose() => _stream.Dispose();

    private byte[] ReadAt(long position, long length)
    {
        if (position < 0 || length < 0 || position + length > _stream.Length) throw new FstFormatException("the file is cut short");
        var buffer = new byte[CheckLength(length)];
        _stream.Position = position;
        _stream.ReadExactly(buffer);
        return buffer;
    }

    private void Open()
    {
        Stream s = _stream;
        if (s.Length < 9) throw new FstFormatException("not an FST file (too short)");
        bool header = false;
        byte[]? geometry = null;
        long geometryCount = 0;
        (long Position, long Length, FstBlock Type)? hierarchy = null;

        long at = 0;
        while (at < s.Length)
        {
            if (at + 9 > s.Length) break;                              // a writer was interrupted here
            byte[] head = ReadAt(at, 9);
            var type = (FstBlock)head[0];
            long length = (long)ReadU64(head, 1);
            if (length < 8 || at + 1 + length > s.Length)
            {
                if (at == 0) throw new FstFormatException("not an FST file");
                break;                                                 // an unfinished last block
            }
            long body = at + 9, bodyLength = length - 8;
            switch (type)
            {
                case FstBlock.Header:
                    if (length != HeaderLength) throw new FstFormatException("not an FST file (unexpected header size)");
                    ReadHeader(ReadAt(at, 1 + length));
                    header = true;
                    break;
                case FstBlock.WholeFileGzip:
                    if (at != 0) throw new FstFormatException("a compressed-file wrapper is only valid at the start");
                    throw new WrappedFile(body + 8, (long)ReadU64(ReadAt(body, 8), 0));
                case FstBlock.ValueChanges or FstBlock.ValueChangesAlias or FstBlock.ValueChangesAlias2:
                    _valueBlocks.Add((at, 1 + length, type));
                    break;
                case FstBlock.Geometry:
                {
                    byte[] b = ReadAt(body, bodyLength);
                    long unpacked = (long)ReadU64(b, 0);
                    geometryCount = (long)ReadU64(b, 8);
                    geometry = bodyLength - 16 == unpacked ? b[16..] : ZlibDecompress(b.AsSpan(16), unpacked);
                    break;
                }
                case FstBlock.HierarchyGzip or FstBlock.HierarchyLz4 or FstBlock.HierarchyLz4Twice:
                    hierarchy = (body, bodyLength, type);
                    break;
                case FstBlock.Blackout or FstBlock.Skip:
                    break;
                default:
                    if (at == 0) throw new FstFormatException("not an FST file");
                    break;                                             // a block type newer than this reader
            }
            at += 1 + length;
        }
        if (!header) throw new FstFormatException("not an FST file (no header block)");
        if (geometry is null || hierarchy is null) throw new FstFormatException("the file has no variable tables (it was not closed by its writer)");

        // widths, by handle
        int count = CheckLength(geometryCount);
        _widths = new int[count];
        _real = new bool[count];
        int pos = 0;
        for (int i = 0; i < count; i++)
        {
            ulong w = ReadVarint(geometry, ref pos);
            if (w == 0) { _real[i] = true; _widths[i] = 8; }
            else if (w == 0xFFFFFFFF) _widths[i] = 0;
            else _widths[i] = CheckLength((long)w);
        }

        var (hp, hl, ht) = hierarchy.Value;
        byte[] h = ReadAt(hp, hl);
        long hierLength = (long)ReadU64(h, 0);
        byte[] names;
        switch (ht)
        {
            case FstBlock.HierarchyGzip:
                names = GzipDecompress(new MemoryStream(h, 8, h.Length - 8), hierLength);
                break;
            case FstBlock.HierarchyLz4:
                names = Lz4Decompress(h.AsSpan(8), hierLength);
                break;
            default:
            {
                int p = 8;
                long once = (long)ReadVarint(h, ref p);
                names = Lz4Decompress(Lz4Decompress(h.AsSpan(p), once), hierLength);
                break;
            }
        }
        ReadHierarchy(names);
    }

    // Signals that the file is one gzip stream holding the real file.
    private sealed class WrappedFile(long position, long length) : Exception
    {
        public long Position => position;
        public long Length => length;
    }

    /// <summary>Opens a file, unwrapping one that was compressed as a whole.</summary>
    public static FstReader Open(string path)
    {
        FileStream file = File.OpenRead(path);
        try { return new FstReader(file); }
        catch (WrappedFile w)
        {
            using FileStream again = File.OpenRead(path);
            again.Position = w.Position;
            return new FstReader(new MemoryStream(GzipDecompress(again, w.Length)));
        }
    }

    private void ReadHeader(byte[] h)
    {
        StartTime = ReadU64(h, 9);
        EndTime = ReadU64(h, 17);
        double little = BinaryPrimitives.ReadDoubleLittleEndian(h.AsSpan(25));
        double big = BinaryPrimitives.ReadDoubleBigEndian(h.AsSpan(25));
        if (little == EndianTest) _doublesLittleEndian = true;
        else if (big == EndianTest) _doublesLittleEndian = false;
        else throw new FstFormatException("not an FST file (the byte-order marker is wrong)");
        TimescaleExponent = (sbyte)h[73];
        Version = Text(h.AsSpan(74, VersionSize));
        Date = Text(h.AsSpan(202, DateSize));
        TimeZero = (long)ReadU64(h, 322);

        static string Text(ReadOnlySpan<byte> field)
        {
            int end = field.IndexOf((byte)0);
            return Encoding.UTF8.GetString(end < 0 ? field : field[..end]).TrimEnd('\n', ' ');
        }
    }

    private void ReadHierarchy(byte[] h)
    {
        var signals = new List<FstSignal>();
        var scopes = new List<FstScope>();
        var path = new List<string>();
        int pos = 0, nextHandle = 0;
        while (pos < h.Length)
        {
            byte tag = h[pos++];
            switch (tag)
            {
                case HierScope:
                {
                    if (pos >= h.Length) throw Bad();
                    var type = (FstScopeType)h[pos++];
                    string name = Name(h, ref pos);
                    Name(h, ref pos);                                  // component name
                    path.Add(name);
                    scopes.Add(new FstScope(string.Join('.', path), type));
                    break;
                }
                case HierUpscope:
                    if (path.Count > 0) path.RemoveAt(path.Count - 1);
                    break;
                case HierAttrBegin:
                    pos += 2;                                          // attribute type and subtype
                    Name(h, ref pos);
                    ReadVarint(h, ref pos);
                    break;
                case HierAttrEnd:
                    break;
                default:
                {
                    if (tag > (byte)FstVarType.ShortReal) throw Bad();
                    if (pos >= h.Length) throw Bad();
                    var direction = (FstDirection)h[pos++];
                    string name = Name(h, ref pos);
                    int width = CheckLength((long)ReadVarint(h, ref pos));
                    int alias = CheckLength((long)ReadVarint(h, ref pos));
                    int handle = alias != 0 ? alias : ++nextHandle;
                    if (handle > _widths.Length) throw Bad();
                    var type = (FstVarType)tag;
                    string scope = string.Join('.', path);
                    signals.Add(new FstSignal(scope.Length == 0 ? name : scope + "." + name, scope, handle,
                        IsReal(type) ? 64 : width, type, direction));
                    break;
                }
            }
        }
        Signals = signals;
        Scopes = scopes;

        static string Name(byte[] h, ref int pos)
        {
            int end = Array.IndexOf(h, (byte)0, Math.Min(pos, h.Length));
            if (end < 0) throw Bad();
            string s = Encoding.UTF8.GetString(h, pos, end - pos);
            pos = end + 1;
            return s;
        }
        static FstFormatException Bad() => new("the table of names is damaged");
    }

    /// <summary>Finds a variable by its full name ("top.cpu.clk").</summary>
    public FstSignal? Find(string name)
    {
        foreach (FstSignal s in Signals)
            if (s.Name == name) return s;
        // names of vectors are often stored with their range: "data [7:0]"
        foreach (FstSignal s in Signals)
        {
            int space = s.Name.IndexOf(' ');
            if (space > 0 && s.Name.AsSpan(0, space).SequenceEqual(name)) return s;
        }
        return null;
    }

    /// <summary>Reads the value changes of some variables (by handle), in time order.</summary>
    public Dictionary<int, FstTrace> Read(IEnumerable<int> handles)
    {
        var traces = new Dictionary<int, FstTrace>();
        foreach (int h in handles)
        {
            if (h < 1 || h > _widths.Length) throw new ArgumentOutOfRangeException(nameof(handles), $"there is no variable {h}");
            traces[h] = new FstTrace();
        }
        bool first = true;
        foreach (var (position, length, type) in _valueBlocks)
        {
            ReadBlock(ReadAt(position, length), type, traces, first);
            first = false;
        }
        return traces;
    }

    private void ReadBlock(byte[] b, FstBlock type, Dictionary<int, FstTrace> traces, bool first)
    {
        // b[0] type, b[1..8] length, then:
        ulong begin = ReadU64(b, 9);
        int end = b.Length;

        // the times are at the end of the block
        long timesUnpacked = (long)ReadU64(b, end - 24), timesPacked = (long)ReadU64(b, end - 16), timeCount = (long)ReadU64(b, end - 8);
        long timesAt = end - 24 - timesPacked;
        if (timesPacked < 0 || timesAt < 33) throw Bad();
        ReadOnlySpan<byte> packed = b.AsSpan((int)timesAt, (int)timesPacked);
        byte[] timeData = timesPacked == timesUnpacked ? packed.ToArray() : ZlibDecompress(packed, timesUnpacked);
        var times = new ulong[CheckLength(timeCount)];
        int tp = 0;
        ulong t = 0;
        for (int i = 0; i < times.Length; i++) { t += ReadVarint(timeData, ref tp); times[i] = t; }

        // the frame: every variable's value when the block began
        int pos = 33;
        long frameUnpacked = (long)ReadVarint(b, ref pos), framePacked = (long)ReadVarint(b, ref pos);
        long frameHandles = (long)ReadVarint(b, ref pos);
        if (framePacked < 0 || pos + framePacked > end) throw Bad();
        // The first block's frame matters only when it is from before the first time; otherwise
        // every variable has a change at that time anyway (this is the reference reader's rule).
        if (first && times.Length > 0 && begin != times[0])
        {
            ReadOnlySpan<byte> fp = b.AsSpan(pos, (int)framePacked);
            byte[] frame = framePacked == frameUnpacked ? fp.ToArray() : ZlibDecompress(fp, frameUnpacked);
            int offset = 0;
            for (int h = 0; h < frameHandles && h < _widths.Length; h++)
            {
                int w = _widths[h];
                if (offset + w > frame.Length) throw Bad();
                if (w > 0 && traces.TryGetValue(h + 1, out FstTrace? trace))
                {
                    trace.Times.Add(begin);
                    if (_real[h]) trace.Reals.Add(ReadDouble(frame.AsSpan(offset)));
                    else trace.Values.Add(frame.AsSpan(offset, w).ToArray());
                }
                offset += w;
            }
        }
        pos += (int)framePacked;

        long chainHandles = (long)ReadVarint(b, ref pos);
        int chainsStart = pos;                              // points at the pack-type byte
        if (pos >= end) throw Bad();
        byte pack = b[pos];

        // the index of chains sits just before the times
        long indexLengthAt = timesAt - 8;
        long indexLength = (long)ReadU64(b, (int)indexLengthAt);
        long indexAt = indexLengthAt - indexLength;
        if (indexLength < 0 || indexAt < chainsStart) throw Bad();
        int n = CheckLength(chainHandles);
        var starts = new long[n + 1];
        var lengths = new long[n + 1];
        int count = 0, lastPhysical = -1;
        long at = 0, previousAlias = 0;
        ReadOnlySpan<byte> index = b.AsSpan((int)indexAt, (int)indexLength);
        int ip = 0;
        while (ip < index.Length)
        {
            if (type == FstBlock.ValueChangesAlias2)
            {
                if ((index[ip] & 1) != 0)
                {
                    long v = ReadSignedVarint(index, ref ip) >> 1;
                    if (count >= n) throw Bad();
                    if (v > 0)
                    {
                        at += v;
                        if (lastPhysical >= 0) lengths[lastPhysical] = at - starts[lastPhysical];
                        starts[count] = at;
                        lastPhysical = count++;
                    }
                    else
                    {
                        if (v < 0) previousAlias = v;       // 0 repeats the previous alias
                        starts[count] = 0;
                        lengths[count++] = previousAlias;
                    }
                }
                else
                {
                    long run = (long)(ReadVarint(index, ref ip) >> 1);
                    if (count + run > n) throw Bad();
                    count += (int)run;                      // variables with no change in this block
                }
            }
            else
            {
                ulong v = ReadVarint(index, ref ip);
                if (v == 0)
                {
                    if (count >= n) throw Bad();
                    starts[count] = 0;
                    lengths[count++] = -(long)ReadVarint(index, ref ip);
                }
                else if ((v & 1) != 0)
                {
                    if (count >= n) throw Bad();
                    at += (long)(v >> 1);
                    if (lastPhysical >= 0) lengths[lastPhysical] = at - starts[lastPhysical];
                    starts[count] = at;
                    lastPhysical = count++;
                }
                else
                {
                    long run = (long)(v >> 1);
                    if (count + run > n) throw Bad();
                    count += (int)run;
                }
            }
        }
        if (lastPhysical >= 0) lengths[lastPhysical] = indexAt - chainsStart - starts[lastPhysical];
        // an alias entry (negative length) means "the same chain as variable -length"
        for (int i = 0; i < count; i++)
        {
            if (starts[i] == 0 && lengths[i] < 0)
            {
                long target = -lengths[i] - 1;
                if (target < i) { starts[i] = starts[target]; lengths[i] = lengths[target]; }
                else { lengths[i] = 0; }
            }
        }

        foreach (var (handle, trace) in traces)
        {
            int i = handle - 1;
            if (i >= count || starts[i] == 0) continue;
            long chainAt = chainsStart + starts[i], chainLength = lengths[i];
            if (chainLength <= 0 || chainAt + chainLength > indexAt) throw Bad();
            int cp = (int)chainAt;
            long unpacked = (long)ReadVarint(b, ref cp);
            ReadOnlySpan<byte> body = b.AsSpan(cp, (int)(chainAt + chainLength - cp));
            ReadOnlySpan<byte> chain = unpacked == 0 ? body : pack switch
            {
                (byte)'4' => Lz4Decompress(body, unpacked),
                (byte)'F' => FastLzDecompress(body, unpacked),
                _ => ZlibDecompress(body, unpacked),
            };
            Decode(chain, _widths[i], _real[i], times, trace);
        }

        static FstFormatException Bad() => new("a block of value changes is damaged");
    }

    private double ReadDouble(ReadOnlySpan<byte> bytes) =>
        _doublesLittleEndian ? BinaryPrimitives.ReadDoubleLittleEndian(bytes) : BinaryPrimitives.ReadDoubleBigEndian(bytes);

    // One variable's changes within a block. Each begins with a number holding how many time
    // steps passed since the variable's previous change, and one or more flag bits.
    private void Decode(ReadOnlySpan<byte> chain, int width, bool real, ulong[] times, FstTrace trace)
    {
        int pos = 0;
        long index = 0;
        while (pos < chain.Length)
        {
            ulong v = ReadVarint(chain, ref pos);
            if (width == 1)
            {
                // even: 0 or 1 in bit 1; odd: one of the states "xzhuwl-?" in bits 1..3
                byte value;
                if ((v & 1) == 0) { index += (long)(v >> 2); value = (byte)('0' + (int)((v >> 1) & 1)); }
                else { index += (long)(v >> 4); value = (byte)OneBitStates[(int)((v >> 1) & 7)]; }
                Add(trace, times, index).Values.Add([value]);
                continue;
            }
            index += (long)(v >> 1);
            if (real)
            {
                if (pos + 8 > chain.Length) throw Bad();
                Add(trace, times, index).Reals.Add(ReadDouble(chain.Slice(pos, 8)));
                pos += 8;
            }
            else if (width == 0)
            {
                int length = CheckLength((long)ReadVarint(chain, ref pos));
                if (pos + length > chain.Length) throw Bad();
                Add(trace, times, index).Values.Add(chain.Slice(pos, length).ToArray());
                pos += length;
            }
            else if ((v & 1) == 0)
            {
                // all bits are 0 or 1: eight to a byte, first bit in the top position
                int bytes = (width + 7) / 8;
                if (pos + bytes > chain.Length) throw Bad();
                var bits = new byte[width];
                for (int j = 0; j < width; j++) bits[j] = (byte)('0' + ((chain[pos + j / 8] >> (7 - (j & 7))) & 1));
                Add(trace, times, index).Values.Add(bits);
                pos += bytes;
            }
            else
            {
                if (pos + width > chain.Length) throw Bad();
                Add(trace, times, index).Values.Add(chain.Slice(pos, width).ToArray());
                pos += width;
            }
        }

        static FstTrace Add(FstTrace trace, ulong[] times, long index)
        {
            if (index < 0 || index >= times.Length) throw Bad();
            trace.Times.Add(times[index]);
            return trace;
        }
        static FstFormatException Bad() => new("a chain of value changes is damaged");
    }
}

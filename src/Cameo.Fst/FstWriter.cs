using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using static Cameo.Fst.FstFormat;

namespace Cameo.Fst;

/// <summary>
/// Writes an FST waveform file. Declare the scopes and variables first, then for each point in
/// time call <see cref="SetTime"/> and emit the values that changed; <see cref="Close"/>
/// finishes the file. Times must not go backwards.
/// </summary>
public sealed class FstWriter : IDisposable
{
    private sealed class Var
    {
        public int Offset, Length;             // position and size in the value snapshot; Length 0 = variable length
        public bool Real;
        public List<byte>? Chain;              // this block's changes, encoded
        public int LastIndex;                  // time index of the last change in this block (0 if none)
        public bool Emitted;                   // has had a value since the file began
        public byte[]? Text;                   // current value of a variable-length variable
        // the change made at the current time, so that a second one at the same time can replace it
        public int RecordIndex = -1, RecordAt, IndexBefore;
        public bool EmittedBefore;
        public byte[]? ValueBefore;
    }

    private readonly FileStream _file;
    private readonly List<Var> _vars = new();
    private readonly MemoryStream _hierarchy = new(), _geometry = new();
    private readonly MemoryStream _times = new();
    private byte[] _current = [];              // every variable's current value, back to back
    private byte[] _frame = [];                // the same, as it was when the block began
    private int _valueBytes;
    private long _chainBytes;
    private int _timeCount, _timeIndex;
    private ulong _firstTime, _time, _blockBegin;
    private bool _started, _closed;
    private ulong _scopes, _hierVars, _sections;
    private int _depth;

    /// <summary>Changes are gathered into blocks of about this many bytes (before compression).</summary>
    public long BlockSize { get; set; } = 8 << 20;

    public ulong CurrentTime => _time;
    public int VariableCount => _vars.Count;

    /// <param name="timescaleExponent">The unit of time as a power of ten seconds: -9 is 1 ns.</param>
    public FstWriter(string path, int timescaleExponent = -9, string version = "Cameo.Runtime", string date = "")
    {
        if (timescaleExponent is < sbyte.MinValue or > sbyte.MaxValue) throw new ArgumentOutOfRangeException(nameof(timescaleExponent));
        _file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        WriteHeader((sbyte)timescaleExponent, version, date);
    }

    private void WriteHeader(sbyte timescale, string version, string date)
    {
        var h = new byte[1 + HeaderLength];
        h[0] = (byte)FstBlock.Header;
        BinaryPrimitives.WriteUInt64BigEndian(h.AsSpan(1), HeaderLength);
        // 9: start time, 17: end time (filled in on close)
        BinaryPrimitives.WriteDoubleLittleEndian(h.AsSpan(25), EndianTest);     // tells a reader how doubles are stored
        // 33: memory used by the writer; 41..72: counts (filled in on close)
        h[73] = (byte)timescale;
        Encoding.UTF8.GetBytes(Fit(version, VersionSize - 1)).CopyTo(h.AsSpan(74));
        Encoding.UTF8.GetBytes(Fit(date, DateSize - 1)).CopyTo(h.AsSpan(202));
        // 321: file type (0 = Verilog); 322: time zero
        _file.Write(h);

        static string Fit(string s, int max)
        {
            s = s.Replace('\n', ' ').Replace('\r', ' ').Replace('\0', ' ');
            while (Encoding.UTF8.GetByteCount(s) > max) s = s[..^1];
            return s;
        }
    }

    // ------------------------------------------------------------------ declarations

    private void CheckDeclaring()
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        if (_started) throw new InvalidOperationException("scopes and variables must be declared before the first time or value");
    }

    private static byte[] Name(string name)
    {
        if (name.Length == 0 || name.Contains('\0')) throw new ArgumentException("a name must not be empty or contain NUL", nameof(name));
        return Encoding.UTF8.GetBytes(name);
    }

    public void PushScope(string name, FstScopeType type = FstScopeType.Module)
    {
        CheckDeclaring();
        _hierarchy.WriteByte(HierScope);
        _hierarchy.WriteByte((byte)type);
        _hierarchy.Write(Name(name));
        _hierarchy.WriteByte(0);
        _hierarchy.WriteByte(0);                // component name: none
        _scopes++;
        _depth++;
    }

    public void PopScope()
    {
        CheckDeclaring();
        if (_depth == 0) throw new InvalidOperationException("there is no open scope");
        _hierarchy.WriteByte(HierUpscope);
        _depth--;
    }

    /// <summary>Declares a variable of <paramref name="width"/> bits and returns its handle (1, 2, ...).</summary>
    public int AddVariable(string name, int width, FstVarType type = FstVarType.Wire, FstDirection direction = FstDirection.Implicit)
    {
        CheckDeclaring();
        bool real = FstFormat.IsReal(type);
        if (type == FstVarType.String) width = 0;
        else if (real) width = 8;
        else if (width < 1 || width > 1 << 20) throw new ArgumentOutOfRangeException(nameof(width), "a variable is 1 to 1048576 bits wide");

        _hierarchy.WriteByte((byte)type);
        _hierarchy.WriteByte((byte)direction);
        _hierarchy.Write(Name(name));
        _hierarchy.WriteByte(0);
        WriteVarint(_hierarchy, (ulong)width);
        WriteVarint(_hierarchy, 0);             // not an alias of another variable
        _hierVars++;

        // the geometry block: a real is recorded as 0, a variable-length value as 2^32-1
        WriteVarint(_geometry, real ? 0u : width == 0 ? 0xFFFFFFFFu : (ulong)width);
        _vars.Add(new Var { Offset = _valueBytes, Length = width, Real = real });
        _valueBytes = checked(_valueBytes + width);
        return _vars.Count;
    }

    public int AddReal(string name) => AddVariable(name, 8, FstVarType.Real);
    public int AddString(string name) => AddVariable(name, 0, FstVarType.String);

    public bool IsReal(int handle) => handle >= 1 && handle <= _vars.Count && _vars[handle - 1].Real;
    public bool IsString(int handle) => handle >= 1 && handle <= _vars.Count && !_vars[handle - 1].Real && _vars[handle - 1].Length == 0;

    // ------------------------------------------------------------------ values

    private void Start(ulong time)
    {
        // before any value: bits are 'x', reals are NaN
        _current = new byte[_valueBytes];
        _current.AsSpan().Fill((byte)'x');
        foreach (Var v in _vars)
            if (v.Real) BinaryPrimitives.WriteDoubleLittleEndian(_current.AsSpan(v.Offset), double.NaN);
        _frame = (byte[])_current.Clone();
        _started = true;
        _firstTime = _blockBegin = _time = time;
        WriteVarint(_times, time);
        _timeCount = 1;
        _timeIndex = 0;
    }

    /// <summary>Moves to a later (or the same) time; values emitted next belong to it.</summary>
    public void SetTime(ulong time)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        if (!_started) { Start(time); return; }
        if (time < _time) throw new ArgumentOutOfRangeException(nameof(time), $"time went backwards ({time} after {_time})");
        if (time == _time) return;
        if (_chainBytes >= BlockSize)
        {
            FlushBlock();
            // the next block's time table begins with the time the last one ended at
            WriteVarint(_times, _time);
            _timeCount = 1;
            _timeIndex = 0;
            _blockBegin = _time;
            _frame = (byte[])_current.Clone();
        }
        WriteVarint(_times, time - _time);
        _timeCount++;
        _timeIndex++;
        _time = time;
    }

    private Var Get(int handle)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        if (handle < 1 || handle > _vars.Count) throw new ArgumentOutOfRangeException(nameof(handle), $"there is no variable {handle}");
        if (!_started) Start(0);
        return _vars[handle - 1];
    }

    // A variable changes at most once per time: a second value at the same time takes back the
    // first, so that the file holds only the last one (and nothing, if that is the old value).
    private void TakeBack(Var v)
    {
        if (v.RecordIndex != _timeIndex) return;
        List<byte> chain = v.Chain!;
        _chainBytes -= chain.Count - v.RecordAt;
        chain.RemoveRange(v.RecordAt, chain.Count - v.RecordAt);
        v.LastIndex = v.IndexBefore;
        v.Emitted = v.EmittedBefore;
        if (v.Length == 0) v.Text = v.ValueBefore;
        else v.ValueBefore!.CopyTo(_current.AsSpan(v.Offset));
        v.RecordIndex = -1;
    }

    // Begins a change record: returns the chain to append to and how many time steps have
    // passed since this variable's previous change in this block.
    private List<byte> Begin(Var v, out int since)
    {
        List<byte> chain = v.Chain ??= new List<byte>();
        v.RecordIndex = _timeIndex;
        v.RecordAt = chain.Count;
        v.IndexBefore = v.LastIndex;
        v.EmittedBefore = v.Emitted;
        if (v.Length == 0) v.ValueBefore = v.Text;
        else _current.AsSpan(v.Offset, v.Length).CopyTo(v.ValueBefore ??= new byte[v.Length]);
        since = _timeIndex - v.LastIndex;
        v.LastIndex = _timeIndex;
        v.Emitted = true;
        return chain;
    }

    private void End(Var v, List<byte> chain) => _chainBytes += chain.Count - v.RecordAt;

    /// <summary>
    /// Sets a bit variable from its characters, most significant first: '0', '1', 'x', 'z' (and
    /// the other VCD states h u w l -). A value shorter than the variable is extended on the
    /// left as Verilog does: with x or z if it starts with one, otherwise with 0.
    /// </summary>
    public void Emit(int handle, ReadOnlySpan<byte> bits)
    {
        Var v = Get(handle);
        if (v.Real || v.Length == 0) throw new ArgumentException("this variable takes a number or a string, not bits", nameof(handle));
        if (bits.Length == 0 || bits.Length > v.Length) throw new ArgumentException($"the value has {bits.Length} bits; the variable has {v.Length}", nameof(bits));
        Span<byte> value = v.Length <= 256 ? stackalloc byte[v.Length] : new byte[v.Length];
        int pad = v.Length - bits.Length;
        byte first = Normalize(bits[0]);
        value[..pad].Fill(first is (byte)'x' or (byte)'z' ? first : (byte)'0');
        bool binary = true;
        for (int i = 0; i < bits.Length; i++)
        {
            byte b = Normalize(bits[i]);
            if (b is not ((byte)'0' or (byte)'1')) binary = false;
            value[pad + i] = b;
        }
        TakeBack(v);
        Span<byte> current = _current.AsSpan(v.Offset, v.Length);
        if (v.Emitted && current.SequenceEqual(value)) return;      // not a change
        List<byte> c = Begin(v, out int since);
        value.CopyTo(current);
        if (v.Length == 1)
        {
            // a one-bit value is packed into the same number as the time step
            if (binary) WriteVarint(c, ((ulong)since << 2) | (uint)((value[0] & 1) << 1));
            else WriteVarint(c, ((ulong)since << 4) | (uint)(OneBitStates.IndexOf((char)value[0]) << 1) | 1);
        }
        else if (binary)
        {
            // eight bits to a byte, first bit in the top position
            WriteVarint(c, (ulong)since << 1);
            for (int i = 0; i < v.Length; i += 8)
            {
                int acc = 0;
                for (int j = 0; j < 8 && i + j < v.Length; j++) acc |= (value[i + j] & 1) << (7 - j);
                c.Add((byte)acc);
            }
        }
        else
        {
            WriteVarint(c, ((ulong)since << 1) | 1);
            foreach (byte b in value) c.Add(b);
        }
        End(v, c);
    }

    private static byte Normalize(byte b) => b switch
    {
        (byte)'0' or (byte)'1' or (byte)'x' or (byte)'z' or (byte)'h' or (byte)'u' or (byte)'w' or (byte)'l' or (byte)'-' => b,
        (byte)'X' or (byte)'Z' or (byte)'H' or (byte)'U' or (byte)'W' or (byte)'L' => (byte)(b | 0x20),
        _ => throw new ArgumentException($"'{(char)b}' is not a signal state (use 0 1 x z h u w l -)"),
    };

    /// <summary>Sets a bit variable from a number (its low bits; a negative number in two's complement).</summary>
    public void EmitInteger(int handle, long value)
    {
        Var v = Get(handle);
        if (v.Real) { EmitReal(handle, value); return; }
        if (v.Length == 0) throw new ArgumentException("this variable takes a string", nameof(handle));
        Span<byte> bits = v.Length <= 256 ? stackalloc byte[v.Length] : new byte[v.Length];
        for (int i = 0; i < v.Length; i++)
        {
            int bit = v.Length - 1 - i;                    // bits past 63 repeat the sign
            bits[i] = (byte)('0' + (int)((value >> Math.Min(bit, 63)) & 1));
        }
        Emit(handle, bits);
    }

    public void EmitReal(int handle, double value)
    {
        Var v = Get(handle);
        if (!v.Real) throw new ArgumentException("this variable is not a real", nameof(handle));
        TakeBack(v);
        Span<byte> current = _current.AsSpan(v.Offset, 8);
        if (v.Emitted && BinaryPrimitives.ReadInt64LittleEndian(current) == BitConverter.DoubleToInt64Bits(value)) return;
        List<byte> c = Begin(v, out int since);
        BinaryPrimitives.WriteDoubleLittleEndian(current, value);
        WriteVarint(c, ((ulong)since << 1) | 1);
        foreach (byte b in current) c.Add(b);
        End(v, c);
    }

    /// <summary>Sets a string variable (any bytes).</summary>
    public void EmitString(int handle, ReadOnlySpan<byte> text)
    {
        Var v = Get(handle);
        if (v.Real || v.Length != 0) throw new ArgumentException("this variable is not a string", nameof(handle));
        TakeBack(v);
        if (v.Emitted && v.Text is not null && text.SequenceEqual(v.Text)) return;
        List<byte> c = Begin(v, out int since);
        v.Text = text.ToArray();
        WriteVarint(c, (ulong)since << 1);
        WriteVarint(c, (ulong)text.Length);
        foreach (byte b in text) c.Add(b);
        End(v, c);
    }

    // ------------------------------------------------------------------ blocks

    // One block of value changes:
    //   type, length, begin time, end time, memory a reader needs for all chains
    //   the values of every variable when the block began ("frame"), zlib
    //   one chain of changes per variable that changed, each zlib-compressed on its own
    //   an index of where each chain starts
    //   the block's times, as differences, zlib
    private void FlushBlock()
    {
        if (_timeCount == 0) return;
        var block = new MemoryStream();
        block.WriteByte((byte)FstBlock.ValueChanges);
        WriteU64(block, 0);                             // length, filled in below
        WriteU64(block, _blockBegin);
        WriteU64(block, _time);
        WriteU64(block, 0);                             // memory needed, filled in below

        byte[] packedFrame = ZlibCompress(_frame, CompressionLevel.Fastest);
        bool framePacked = packedFrame.Length < _frame.Length;
        WriteVarint(block, (ulong)_frame.Length);
        WriteVarint(block, (ulong)(framePacked ? packedFrame.Length : _frame.Length));
        WriteVarint(block, (ulong)_vars.Count);
        block.Write(framePacked ? packedFrame : _frame);

        WriteVarint(block, (ulong)_vars.Count);
        long chainsStart = block.Position;              // chain positions are counted from here
        block.WriteByte((byte)'Z');                     // chains are packed with zlib

        var index = new MemoryStream();
        ulong memory = 0, unchanged = 0;
        long previous = 0;
        foreach (Var v in _vars)
        {
            if (v.Chain is not { Count: > 0 } chain) { unchanged++; continue; }
            if (unchanged > 0) { WriteVarint(index, unchanged << 1); unchanged = 0; }
            long at = block.Position - chainsStart;
            WriteVarint(index, ((ulong)(at - previous) << 1) | 1);
            previous = at;

            ReadOnlySpan<byte> raw = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(chain);
            memory += (ulong)raw.Length;
            byte[]? packed = raw.Length > 32 ? ZlibCompress(raw, CompressionLevel.Optimal) : null;
            if (packed is not null && packed.Length < raw.Length)
            {
                WriteVarint(block, (ulong)raw.Length);  // the unpacked length says "this chain is packed"
                block.Write(packed);
            }
            else
            {
                WriteVarint(block, 0);
                block.Write(raw);
            }
            v.Chain = null;
            v.LastIndex = 0;
        }
        foreach (Var v in _vars) v.RecordIndex = -1;
        if (unchanged > 0) WriteVarint(index, unchanged << 1);
        index.WriteTo(block);
        WriteU64(block, (ulong)index.Length);

        ReadOnlySpan<byte> times = _times.GetBuffer().AsSpan(0, (int)_times.Length);
        byte[] packedTimes = ZlibCompress(times, CompressionLevel.SmallestSize);
        bool timesPacked = packedTimes.Length < times.Length;
        block.Write(timesPacked ? packedTimes : times);
        WriteU64(block, (ulong)times.Length);
        WriteU64(block, (ulong)(timesPacked ? packedTimes.Length : times.Length));
        WriteU64(block, (ulong)_timeCount);

        byte[] bytes = block.GetBuffer();
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(1), (ulong)(block.Length - 1));
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(25), memory);
        _file.Write(bytes, 0, (int)block.Length);

        _sections++;
        _times.SetLength(0);
        _timeCount = 0;
        _chainBytes = 0;
    }

    /// <summary>Writes what is pending, the variable tables and the final header.</summary>
    public void Close()
    {
        if (_closed) return;
        _closed = true;
        try
        {
            while (_depth > 0) { _hierarchy.WriteByte(HierUpscope); _depth--; }
            if (_started) FlushBlock();

            // geometry: the width of every variable
            ReadOnlySpan<byte> geometry = _geometry.GetBuffer().AsSpan(0, (int)_geometry.Length);
            byte[] packedGeometry = ZlibCompress(geometry, CompressionLevel.SmallestSize);
            bool packed = packedGeometry.Length < geometry.Length;
            _file.WriteByte((byte)FstBlock.Geometry);
            WriteU64(_file, (ulong)((packed ? packedGeometry.Length : geometry.Length) + 24));
            WriteU64(_file, (ulong)geometry.Length);
            WriteU64(_file, (ulong)_vars.Count);
            _file.Write(packed ? packedGeometry : geometry);

            // hierarchy: scopes and variable names, gzip
            using var gz = new MemoryStream();
            using (var z = new GZipStream(gz, CompressionLevel.SmallestSize, leaveOpen: true))
                z.Write(_hierarchy.GetBuffer(), 0, (int)_hierarchy.Length);
            _file.WriteByte((byte)FstBlock.HierarchyGzip);
            WriteU64(_file, (ulong)(gz.Length + 16));
            WriteU64(_file, (ulong)_hierarchy.Length);
            gz.WriteTo(_file);

            _file.Position = 9;
            WriteU64(_file, _firstTime);
            WriteU64(_file, _time);
            _file.Position = 41;
            WriteU64(_file, _scopes);
            WriteU64(_file, _hierVars);
            WriteU64(_file, (ulong)_vars.Count);
            WriteU64(_file, _sections);
            _file.Flush();
        }
        finally
        {
            _file.Dispose();
        }
    }

    public void Dispose() => Close();
}

using System.Runtime.CompilerServices;
using System.Text;
using Cameo.Runtime.Vm;

namespace Cameo.Runtime.Stdlib;

/// <summary>
/// A file handle. Files are byte streams and VM strings are bytes, so data passes through
/// unchanged. The standard streams go through the state (<see cref="LuaState.WriteOut"/>,
/// <see cref="LuaState.WriteErr"/>, <see cref="LuaState.ReadIn"/>).
/// </summary>
public sealed class LuaFile : IUserdata
{

    private Stream? _stream;
    // standard streams: 1 = stdin, 2 = stdout, 3 = stderr
    private readonly LuaState? _state;
    private readonly int _std;
    internal bool DeleteOnClose;
    internal string? Path;

    public string TypeName => "FILE*";
    public Table? Metatable { get; set; }
    public bool IsClosed { get; private set; }
    public bool IsStandard => _stream == null;

    internal LuaFile(Stream stream, Table mt) { _stream = stream; Metatable = mt; }
    internal LuaFile(LuaState state, int std, Table mt) { _state = state; _std = std; Metatable = mt; }

    internal void CheckOpen()
    {
        if (IsClosed) throw new LuaException("attempt to use a closed file");
    }

    // ---- unit-level reading (bytes for streams, chars for text readers)

    // Read buffer for disk files. (Profiling the docs' file example showed a quarter of the
    // run time in per-byte FileStream.ReadByte/CanRead calls.)
    private byte[]? _buf;
    private int _bufPos, _bufLen;

    private bool Fill()
    {
        if (_stream == null || !_stream.CanRead) return false;
        _buf ??= new byte[64 * 1024];
        _bufPos = 0;
        _bufLen = _stream.Read(_buf, 0, _buf.Length);
        return _bufLen > 0;
    }

    internal int Peek()
    {
        if (_stream != null) return _bufPos < _bufLen || Fill() ? _buf![_bufPos] : -1;
        return _std == 1 ? _state!.PeekIn() : -1;
    }

    internal int Read()
    {
        if (_stream != null) return _bufPos < _bufLen || Fill() ? _buf![_bufPos++] : -1;
        return _std == 1 ? _state!.ReadIn() : -1;
    }

    /// <summary>Everything up to the end of the file.</summary>
    internal string ReadToEnd()
    {
        if (_stream == null)
        {
            var sb = new StringBuilder();
            int ch;
            while ((ch = Read()) >= 0) sb.Append((char)ch);
            return sb.ToString();
        }
        var ms = new MemoryStream();
        do
        {
            ms.Write(_buf ?? Array.Empty<byte>(), _bufPos, _bufLen - _bufPos);
            _bufPos = _bufLen;
        } while (Fill());
        return LuaText.FromBytes(ms.GetBuffer().AsSpan(0, (int)ms.Length));
    }

    /// <summary>The next line (null at end of file), found by scanning the buffer for '\n'.</summary>
    internal string? ReadLine(bool keepEol)
    {
        if (_stream == null)
        {
            var sb = new StringBuilder();
            bool any = false;
            int ch;
            while ((ch = Read()) >= 0)
            {
                any = true;
                if (ch == '\n') { if (keepEol) sb.Append('\n'); return sb.ToString(); }
                sb.Append((char)ch);
            }
            return any ? sb.ToString() : null;
        }
        MemoryStream? spill = null; // only for lines that straddle a buffer refill
        while (true)
        {
            if (_bufPos >= _bufLen && !Fill())
                return spill == null ? null : LuaText.FromBytes(spill.GetBuffer().AsSpan(0, (int)spill.Length));
            ReadOnlySpan<byte> rest = _buf.AsSpan(_bufPos, _bufLen - _bufPos);
            int nl = rest.IndexOf((byte)'\n');
            if (nl < 0)
            {
                (spill ??= new MemoryStream()).Write(rest);
                _bufPos = _bufLen;
                continue;
            }
            ReadOnlySpan<byte> line = rest.Slice(0, keepEol ? nl + 1 : nl);
            _bufPos += nl + 1;
            if (spill == null) return LuaText.FromBytes(line);
            spill.Write(line);
            return LuaText.FromBytes(spill.GetBuffer().AsSpan(0, (int)spill.Length));
        }
    }

    /// <summary>Accumulates units read from this file and converts them to a string.</summary>
    internal struct Accum
    {
        private readonly bool _bytes;
        private MemoryStream? _ms;
        private StringBuilder? _sb;
        public Accum(LuaFile f) { _bytes = f._stream != null; _ms = null; _sb = null; }
        public void Add(int unit)
        {
            if (_bytes) (_ms ??= new MemoryStream()).WriteByte((byte)unit);
            else (_sb ??= new StringBuilder()).Append((char)unit);
        }
        public override string ToString()
        {
            if (_bytes) return _ms == null ? "" : LuaText.FromBytes(_ms.GetBuffer().AsSpan(0, (int)_ms.Length));
            return _sb?.ToString() ?? "";
        }
    }

    internal bool CanWrite => _stream != null ? _stream.CanWrite : _std >= 2;
    internal bool CanRead => _stream != null ? _stream.CanRead : _std == 1;

    internal void Write(string s)
    {
        if (_stream != null)
        {
            DropPushback();
            // encode in chunks: no second full-size copy of a large string
            Span<byte> chunk = stackalloc byte[4096];
            for (int at = 0; at < s.Length; at += chunk.Length)
            {
                int n = Encoding.Latin1.GetBytes(s.AsSpan(at, Math.Min(chunk.Length, s.Length - at)), chunk);
                _stream.Write(chunk.Slice(0, n));
            }
        }
        else if (_std == 2) _state!.WriteOut(s);
        else _state!.WriteErr(s);
    }

    // Before writing or seeking: give back what was read ahead, so the stream position is
    // where the program thinks it is.
    private void DropPushback()
    {
        int ahead = _bufLen - _bufPos;
        if (ahead > 0 && _stream != null && _stream.CanSeek) _stream.Seek(-ahead, SeekOrigin.Current);
        _bufPos = _bufLen = 0;
    }

    internal void Flush()
    {
        if (_stream != null) _stream.Flush();
        else if (_std == 2) _state!.FlushOut();
        else if (_std == 3) _state!.FlushErr();
    }

    internal long Seek(SeekOrigin origin, long offset)
    {
        if (_stream == null || !_stream.CanSeek) throw new IOException("Illegal seek");
        DropPushback();
        return _stream.Seek(offset, origin);
    }

    internal void Close()
    {
        if (IsClosed) return;
        if (_stream != null)
        {
            _stream.Dispose();
            _stream = null!;
            IsClosed = true;
            if (DeleteOnClose && Path != null)
            {
                try { File.Delete(Path); } catch (IOException) { }
            }
        }
        else
        {
            Flush(); // standard streams are flushed but stay usable by the host
        }
    }

    internal string Describe() =>
        IsClosed ? "file (closed)" : "file (0x" + RuntimeHelpers.GetHashCode(this).ToString("x8") + ")";
}

public static class IoLib
{
    private sealed class IoState
    {
        public Table Meta = null!;
        public LuaFile Stdin = null!, Stdout = null!, Stderr = null!;
        public LuaFile DefaultInput = null!, DefaultOutput = null!;
    }

    public static void Open(LuaState L)
    {
        var st = new IoState();
        var meta = new Table();
        var methods = new Table();
        st.Meta = meta;
        meta.SetStr("__index", methods);
        meta.SetStr("__name", "FILE*");
        L.Register(meta, "__tostring", static (ref CallContext c) => c.Return(ToFile(ref c, 0).Describe()));

        st.Stdin = new LuaFile(L, 1, meta);
        st.Stdout = new LuaFile(L, 2, meta);
        st.Stderr = new LuaFile(L, 3, meta);
        st.DefaultInput = st.Stdin;
        st.DefaultOutput = st.Stdout;

        // ---- file methods
        L.RegisterInstance<LuaFile>(methods, "read", (ref CallContext c) => ReadFormats(ref c, CheckFile(ref c, 0), 1));
        L.RegisterInstance<LuaFile>(methods, "write", (ref CallContext c) =>
        {
            LuaFile f = CheckFile(ref c, 0);
            return WriteArgs(ref c, f, 1, c[0]);
        });
        L.RegisterInstance<LuaFile>(methods, "lines", (ref CallContext c) =>
        {
            LuaFile f = CheckFile(ref c, 0);
            return c.Return(MakeLinesIter(f, c.ArgsFrom(1), false));
        });
        L.RegisterInstance<LuaFile>(methods, "close", (ref CallContext c) => CloseFile(ref c, CheckFile(ref c, 0)));
        meta.SetStr("__close", methods.GetStr("close")); // local f <close> = io.open(...)
        L.RegisterInstance<LuaFile>(methods, "flush", (ref CallContext c) =>
        {
            CheckFile(ref c, 0).Flush();
            return c.Return(c[0]);
        });
        L.RegisterInstance<LuaFile>(methods, "seek", Seek);
        L.RegisterInstance<LuaFile>(methods, "setvbuf", (ref CallContext c) =>
        {
            CheckFile(ref c, 0);
            return c.Return(Value.True);
        });

        // ---- io functions
        var io = new Table();
        L.Register(io, "open", (ref CallContext c) =>
        {
            string name = c.CheckString(0);
            string mode = c.OptString(1, "r");
            if (!ValidMode(mode)) throw c.ArgError(1, "invalid mode");
            bool write = mode[0] is 'w' or 'a' || mode.Contains('+');
            if (!c.L.AuthorizeFile(LuaText.Decode(name), write, out string canonical))
                return IoFailure(ref c, name, new UnauthorizedAccessException("permission denied by host file policy"));
            try
            {
                return c.Return(Value.FromUserdata(new LuaFile(OpenStream(LuaText.Encode(canonical), mode), st.Meta) { Path = name }));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return IoFailure(ref c, name, e);
            }
        });
        L.Register(io, "close", (ref CallContext c) =>
        {
            LuaFile f = c.IsNoneOrNil(0) ? st.DefaultOutput : CheckFile(ref c, 0);
            return CloseFile(ref c, f);
        });
        L.Register(io, "read", (ref CallContext c) => ReadFormats(ref c, OpenDefault(st.DefaultInput), 0));
        L.Register(io, "write", (ref CallContext c) =>
        {
            LuaFile f = OpenDefault(st.DefaultOutput);
            return WriteArgs(ref c, f, 0, Value.FromUserdata(f));
        });
        L.Register(io, "lines", (ref CallContext c) =>
        {
            if (c.IsNoneOrNil(0))
                return c.Return(MakeLinesIter(OpenDefault(st.DefaultInput), Array.Empty<Value>(), false));
            string name = c.CheckString(0);
            if (!c.L.AuthorizeFile(LuaText.Decode(name), write: false, out string canonical))
                throw new LuaException(name + ": permission denied by host file policy");
            LuaFile f;
            try
            {
                f = new LuaFile(OpenStream(LuaText.Encode(canonical), "r"), st.Meta) { Path = name };
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                throw new LuaException(name + ": " + Reason(e));
            }
            return c.Return(MakeLinesIter(f, c.ArgsFrom(1), true), Value.Nil, Value.Nil);
        });
        L.Register(io, "input", (ref CallContext c) =>
        {
            if (!c.IsNoneOrNil(0))
            {
                if (c[0].IsString)
                {
                    string name = c[0].AsString;
                    if (!c.L.AuthorizeFile(LuaText.Decode(name), write: false, out string canonical))
                        throw new LuaException(name + ": permission denied by host file policy");
                    try { st.DefaultInput = new LuaFile(OpenStream(LuaText.Encode(canonical), "r"), st.Meta) { Path = name }; }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                    {
                        throw new LuaException(name + ": " + Reason(e));
                    }
                }
                else st.DefaultInput = ToFile(ref c, 0);
            }
            return c.Return(Value.FromUserdata(st.DefaultInput));
        });
        L.Register(io, "output", (ref CallContext c) =>
        {
            if (!c.IsNoneOrNil(0))
            {
                if (c[0].IsString)
                {
                    string name = c[0].AsString;
                    if (!c.L.AuthorizeFile(LuaText.Decode(name), write: true, out string canonical))
                        throw new LuaException(name + ": permission denied by host file policy");
                    try { st.DefaultOutput = new LuaFile(OpenStream(LuaText.Encode(canonical), "w"), st.Meta) { Path = name }; }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                    {
                        throw new LuaException(name + ": " + Reason(e));
                    }
                }
                else st.DefaultOutput = ToFile(ref c, 0);
            }
            return c.Return(Value.FromUserdata(st.DefaultOutput));
        });
        L.Register(io, "type", static (ref CallContext c) =>
        {
            c.CheckAny(0);
            if (c[0].Kind == ValueKind.Userdata && c[0].O is LuaFile f) return c.Return(f.IsClosed ? "closed file" : "file");
            return c.Return(Value.Nil);
        });
        L.Register(io, "tmpfile", (ref CallContext c) =>
        {
            if (c.L.FilePolicy != null && !c.L.AuthorizeFile(Path.GetTempPath(), write: true, out _))
                return IoFailure(ref c, Path.GetTempPath(), new UnauthorizedAccessException("permission denied by host file policy"));
            string path = Path.GetTempFileName();
            try
            {
                var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite);
                return c.Return(Value.FromUserdata(new LuaFile(fs, st.Meta) { Path = path, DeleteOnClose = true }));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return IoFailure(ref c, path, e);
            }
        });
        L.Register(io, "popen", static (ref CallContext c) =>
        {
            c.CheckString(0);
            return c.Return(Value.Nil, "'popen' not supported");
        });
        io.SetStr("stdin", Value.FromUserdata(st.Stdin));
        io.SetStr("stdout", Value.FromUserdata(st.Stdout));
        io.SetStr("stderr", Value.FromUserdata(st.Stderr));
        L.Globals.SetStr("io", io);
        L.LoadedModules.SetStr("io", io);
    }

    // ------------------------------------------------------------------ helpers

    private static bool ValidMode(string mode)
    {
        int i = 0;
        if (mode.Length == 0 || "rwa".IndexOf(mode[i++]) < 0) return false;
        if (i < mode.Length && mode[i] == '+') i++;
        if (i < mode.Length && mode[i] == 'b') i++;
        return i == mode.Length;
    }

    private static Stream OpenStream(string name, string mode)
    {
        name = LuaText.Decode(name); // file names are text for the OS
        bool plus = mode.Contains('+');
        switch (mode[0])
        {
            case 'r':
                return new FileStream(name, FileMode.Open, plus ? FileAccess.ReadWrite : FileAccess.Read, FileShare.ReadWrite);
            case 'w':
                return new FileStream(name, FileMode.Create, plus ? FileAccess.ReadWrite : FileAccess.Write, FileShare.ReadWrite);
            default:
                {
                    var fs = new FileStream(name, FileMode.OpenOrCreate, plus ? FileAccess.ReadWrite : FileAccess.Write, FileShare.ReadWrite);
                    fs.Seek(0, SeekOrigin.End);
                    return fs;
                }
        }
    }

    private static string Reason(Exception e) => e switch
    {
        FileNotFoundException or DirectoryNotFoundException => "No such file or directory",
        UnauthorizedAccessException => "Permission denied",
        _ => e.Message,
    };

    private static int IoFailure(ref CallContext c, string? name, Exception e)
    {
        int errno = e switch
        {
            FileNotFoundException or DirectoryNotFoundException => 2,
            UnauthorizedAccessException => 13,
            _ => 5,
        };
        string msg = name != null ? name + ": " + Reason(e) : Reason(e);
        return c.Return(Value.Nil, msg, Value.FromInt(errno));
    }

    private static LuaFile ToFile(ref CallContext c, int i)
    {
        if (c[i].Kind == ValueKind.Userdata && c[i].O is LuaFile f) return f;
        throw c.TypeError(i, "FILE*");
    }

    private static LuaFile CheckFile(ref CallContext c, int i)
    {
        LuaFile f = ToFile(ref c, i);
        f.CheckOpen();
        return f;
    }

    private static LuaFile OpenDefault(LuaFile f)
    {
        if (f.IsClosed) throw new LuaException("default file is closed");
        return f;
    }

    private static int CloseFile(ref CallContext c, LuaFile f)
    {
        f.CheckOpen();
        try
        {
            f.Close();
            return c.Return(Value.True);
        }
        catch (IOException e)
        {
            return IoFailure(ref c, null, e);
        }
    }

    private static int WriteArgs(ref CallContext c, LuaFile f, int first, Value fileValue)
    {
        if (!f.CanWrite) return c.Return(Value.Nil, "Bad file descriptor", Value.FromInt(9));
        try
        {
            for (int i = first; i < c.Count; i++)
            {
                Value v = c[i];
                if (v.IsString) f.Write(v.AsString);
                else if (v.IsNumber) f.Write(v.IsFloat ? LuaNumber.FormatG(v.N, 14, false) : v.ToString()); // io.write prints floats with %.14g
                else throw c.TypeError(i, "string");
            }
        }
        catch (IOException e)
        {
            return IoFailure(ref c, null, e);
        }
        return c.Return(fileValue);
    }

    private static int Seek(ref CallContext c)
    {
        LuaFile f = CheckFile(ref c, 0);
        string whence = c.OptString(1, "cur");
        long offset = c.OptInt(2, 0);
        SeekOrigin origin = whence switch
        {
            "set" => SeekOrigin.Begin,
            "cur" => SeekOrigin.Current,
            "end" => SeekOrigin.End,
            _ => throw c.ArgError(1, $"invalid option '{whence}'"),
        };
        try
        {
            return c.Return(Value.FromInt(f.Seek(origin, offset)));
        }
        catch (IOException e)
        {
            return c.Return(Value.Nil, e.Message, Value.FromInt(29));
        }
    }

    // ------------------------------------------------------------------ reading

    private static int ReadFormats(ref CallContext c, LuaFile f, int first)
    {
        if (!f.CanRead) return c.Return(Value.Nil, "Bad file descriptor", Value.FromInt(9));
        int n = c.Count - first;
        try
        {
            if (n <= 0) return c.Return(ReadLine(f, false));
            var results = new Value[n];
            int count = 0;
            for (int i = 0; i < n; i++)
            {
                Value r = ReadOne(ref c, f, c[first + i], first + i);
                results[count++] = r;
                if (r.IsNil) break;
            }
            return c.Return(results.AsSpan(0, count));
        }
        catch (IOException e)
        {
            return IoFailure(ref c, null, e);
        }
    }

    private static Value ReadOne(ref CallContext c, LuaFile f, Value fmt, int argIndex)
    {
        if (fmt.IsNumber)
        {
            long l = c.CheckInt(argIndex);
            return ReadChars(f, l);
        }
        if (!fmt.IsString) throw c.TypeError(argIndex, "string");
        string s = fmt.AsString;
        int p = s.Length > 0 && s[0] == '*' ? 1 : 0;
        char k = p < s.Length ? s[p] : '\0';
        switch (k)
        {
            case 'n': return ReadNumber(f);
            case 'l': return ReadLine(f, false);
            case 'L': return ReadLine(f, true);
            case 'a': return ReadAll(f);
            default: throw c.ArgError(argIndex, "invalid format");
        }
    }

    private static Value ReadLine(LuaFile f, bool keepEol) => f.ReadLine(keepEol) is { } line ? Value.FromString(line) : Value.Nil;

    private static Value ReadAll(LuaFile f) => Value.FromString(f.ReadToEnd());

    private static Value ReadChars(LuaFile f, long n)
    {
        if (n <= 0) return f.Peek() < 0 ? Value.Nil : Value.FromString("");
        var acc = new LuaFile.Accum(f);
        long got = 0;
        while (got < n)
        {
            int ch = f.Read();
            if (ch < 0) break;
            acc.Add(ch);
            got++;
        }
        return got == 0 ? Value.Nil : Value.FromString(acc.ToString());
    }

    // Reads a numeral the way liolib's l_getn does (up to 200 characters).
    private static Value ReadNumber(LuaFile f)
    {
        var sb = new StringBuilder();
        int ch;
        while ((ch = f.Peek()) >= 0 && (ch == ' ' || (ch >= '\t' && ch <= '\r'))) f.Read();
        bool Accept(string set)
        {
            int p = f.Peek();
            if (p >= 0 && set.IndexOf((char)p) >= 0 && sb.Length < 200)
            {
                sb.Append((char)f.Read());
                return true;
            }
            return false;
        }
        int Digits(bool hex)
        {
            int cnt = 0;
            while (true)
            {
                int p = f.Peek();
                if (p < 0 || sb.Length >= 200) break;
                char cc = (char)p;
                if (!(hex ? char.IsAsciiHexDigit(cc) : char.IsAsciiDigit(cc))) break;
                sb.Append((char)f.Read());
                cnt++;
            }
            return cnt;
        }
        Accept("-+");
        bool hex = false;
        int count = 0;
        if (Accept("0"))
        {
            if (Accept("xX")) hex = true;
            else count = 1;
        }
        count += Digits(hex);
        if (Accept(".")) count += Digits(hex);
        if (count > 0 && Accept(hex ? "pP" : "eE"))
        {
            Accept("-+");
            Digits(false);
        }
        return LuaNumber.TryParse(sb.ToString(), out Value v) ? v : Value.Nil;
    }

    private static Value MakeLinesIter(LuaFile f, Value[] formats, bool closeAtEof)
    {
        return Value.FromFunction(new NativeFunction("lines_iter", (ref CallContext c) =>
        {
            if (f.IsClosed) throw new LuaException("file is already closed");
            Value first;
            Value[]? all = null;
            if (formats.Length == 0) first = ReadLine(f, false);
            else
            {
                all = new Value[formats.Length];
                int count = 0;
                for (int i = 0; i < formats.Length; i++)
                {
                    Value fmt = formats[i];
                    Value r;
                    if (fmt.IsNumber && fmt.TryToInteger(out long l)) r = ReadChars(f, l);
                    else if (fmt.IsString)
                    {
                        string s = fmt.AsString;
                        int p = s.Length > 0 && s[0] == '*' ? 1 : 0;
                        char k = p < s.Length ? s[p] : '\0';
                        r = k switch
                        {
                            'n' => ReadNumber(f),
                            'l' => ReadLine(f, false),
                            'L' => ReadLine(f, true),
                            'a' => ReadAll(f),
                            _ => throw new LuaException($"bad argument #{i + 1} to 'lines' (invalid format)"),
                        };
                    }
                    else throw new LuaException($"bad argument #{i + 1} to 'lines' (invalid format)");
                    all[count++] = r;
                    if (r.IsNil) break;
                }
                first = all[0];
                if (count < all.Length) Array.Resize(ref all, count);
            }
            if (first.IsNil && closeAtEof) f.Close();
            return all == null ? c.Return(first) : c.Return(all);
        }));
    }
}

using System.Buffers.Binary;
using System.IO.Compression;

namespace Cameo.Fst;

// The FST ("Fast Signal Trace") container, as written by GTKWave's fstapi.c (MIT licence),
// which is the format's definition. A file is a sequence of blocks:
//
//     1 byte   block type
//     8 bytes  block length, big-endian, counted from this field (so without the type byte)
//     ...      contents
//
// Integers in block headers are big-endian 64-bit; inside the data they are "varints"
// (7 bits per byte, least significant first, top bit set on all but the last byte).

public enum FstBlock : byte
{
    Header = 0,
    ValueChanges = 1,
    Blackout = 2,
    Geometry = 3,
    HierarchyGzip = 4,
    ValueChangesAlias = 5,
    HierarchyLz4 = 6,
    HierarchyLz4Twice = 7,
    ValueChangesAlias2 = 8,
    WholeFileGzip = 254,
    Skip = 255,
}

/// <summary>Variable types (the numbering is the format's).</summary>
public enum FstVarType : byte
{
    Event = 0, Integer = 1, Parameter = 2, Real = 3, RealParameter = 4, Reg = 5, Supply0 = 6, Supply1 = 7,
    Time = 8, Tri = 9, TriAnd = 10, TriOr = 11, TriReg = 12, Tri0 = 13, Tri1 = 14, Wand = 15, Wire = 16,
    Wor = 17, Port = 18, SparseArray = 19, RealTime = 20, String = 21, Bit = 22, Logic = 23, Int = 24,
    ShortInt = 25, LongInt = 26, Byte = 27, Enum = 28, ShortReal = 29,
}

public enum FstScopeType : byte
{
    Module = 0, Task = 1, Function = 2, Begin = 3, Fork = 4, Generate = 5, Struct = 6, Union = 7,
    Class = 8, Interface = 9, Package = 10, Program = 11,
}

public enum FstDirection : byte { Implicit = 0, Input = 1, Output = 2, InOut = 3, Buffer = 4, Linkage = 5 }

/// <summary>The file is not FST, is damaged, or uses a part of the format this library does not read.</summary>
public sealed class FstFormatException(string message) : Exception(message);

internal static class FstFormat
{
    public const int HeaderLength = 329;          // without the type byte
    public const int VersionSize = 128, DateSize = 119;
    public const double EndianTest = 2.7182818284590452354;
    public const byte HierScope = 254, HierUpscope = 255, HierAttrBegin = 252, HierAttrEnd = 253;
    public const string OneBitStates = "xzhuwl-?"; // the non-0/1 values of a one-bit signal, by code

    public static readonly string[] VarTypeNames =
    [
        "event", "integer", "parameter", "real", "real_parameter", "reg", "supply0", "supply1", "time", "tri",
        "triand", "trior", "trireg", "tri0", "tri1", "wand", "wire", "wor", "port", "sparray", "realtime",
        "string", "bit", "logic", "int", "shortint", "longint", "byte", "enum", "shortreal",
    ];
    public static readonly string[] ScopeTypeNames =
    [
        "module", "task", "function", "begin", "fork", "generate", "struct", "union", "class", "interface",
        "package", "program",
    ];
    public static readonly string[] DirectionNames = ["implicit", "input", "output", "inout", "buffer", "linkage"];

    public static bool IsReal(FstVarType t) =>
        t is FstVarType.Real or FstVarType.RealParameter or FstVarType.RealTime or FstVarType.ShortReal;

    // ------------------------------------------------------------------ varints

    public static void WriteVarint(Stream s, ulong v)
    {
        while (v >= 0x80) { s.WriteByte((byte)(v | 0x80)); v >>= 7; }
        s.WriteByte((byte)v);
    }

    public static void WriteVarint(List<byte> s, ulong v)
    {
        while (v >= 0x80) { s.Add((byte)(v | 0x80)); v >>= 7; }
        s.Add((byte)v);
    }

    public static ulong ReadVarint(ReadOnlySpan<byte> data, ref int pos)
    {
        ulong v = 0;
        for (int shift = 0; ; shift += 7)
        {
            if (pos >= data.Length || shift > 63) throw new FstFormatException("a number runs past the end of its block");
            byte b = data[pos++];
            v |= (ulong)(b & 0x7F) << shift;
            if (b < 0x80) return v;
        }
    }

    /// <summary>A signed varint (sign-extended from the last byte's bit 6).</summary>
    public static long ReadSignedVarint(ReadOnlySpan<byte> data, ref int pos)
    {
        long v = 0;
        for (int shift = 0; ; shift += 7)
        {
            if (pos >= data.Length || shift > 63) throw new FstFormatException("a number runs past the end of its block");
            byte b = data[pos++];
            v |= (long)(b & 0x7F) << shift;
            if (b < 0x80)
            {
                if ((b & 0x40) != 0 && shift + 7 < 64) v |= -1L << (shift + 7);
                return v;
            }
        }
    }

    public static void WriteU64(Stream s, ulong v)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(b, v);
        s.Write(b);
    }

    public static ulong ReadU64(ReadOnlySpan<byte> data, int pos)
    {
        if (pos < 0 || pos + 8 > data.Length) throw new FstFormatException("the file is cut short");
        return BinaryPrimitives.ReadUInt64BigEndian(data.Slice(pos, 8));
    }

    // ------------------------------------------------------------------ compression

    public static byte[] ZlibCompress(ReadOnlySpan<byte> data, CompressionLevel level)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, level, leaveOpen: true)) z.Write(data);
        return ms.ToArray();
    }

    public static byte[] ZlibDecompress(ReadOnlySpan<byte> data, long length)
    {
        var result = new byte[CheckLength(length)];
        using var z = new ZLibStream(new MemoryStream(data.ToArray()), CompressionMode.Decompress);
        try { z.ReadExactly(result); }
        catch (Exception e) when (e is InvalidDataException or EndOfStreamException) { throw new FstFormatException("compressed data is damaged"); }
        return result;
    }

    public static byte[] GzipDecompress(Stream source, long length)
    {
        var result = new byte[CheckLength(length)];
        using var z = new GZipStream(source, CompressionMode.Decompress, leaveOpen: true);
        try { z.ReadExactly(result); }
        catch (Exception e) when (e is InvalidDataException or EndOfStreamException) { throw new FstFormatException("compressed data is damaged"); }
        return result;
    }

    public static int CheckLength(long length)
    {
        if (length < 0 || length > Array.MaxLength) throw new FstFormatException("a block is larger than this library can hold in memory");
        return (int)length;
    }

    /// <summary>LZ4 block format.</summary>
    public static byte[] Lz4Decompress(ReadOnlySpan<byte> src, long length)
    {
        var dst = new byte[CheckLength(length)];
        int ip = 0, op = 0;
        while (ip < src.Length && op < dst.Length)
        {
            int token = src[ip++];
            int literals = token >> 4;
            if (literals == 15) literals += Lz4Extra(src, ref ip);
            if (ip + literals > src.Length || op + literals > dst.Length) throw Damaged();
            src.Slice(ip, literals).CopyTo(dst.AsSpan(op));
            ip += literals; op += literals;
            if (ip >= src.Length || op >= dst.Length) break;
            if (ip + 2 > src.Length) throw Damaged();
            int offset = src[ip] | src[ip + 1] << 8;
            ip += 2;
            int match = token & 15;
            if (match == 15) match += Lz4Extra(src, ref ip);
            match += 4;
            if (offset == 0 || offset > op) throw Damaged();
            if (op + match > dst.Length) match = dst.Length - op;      // the reference reads "partial" blocks
            for (int i = 0; i < match; i++, op++) dst[op] = dst[op - offset];
        }
        if (op != dst.Length) throw Damaged();
        return dst;

        static int Lz4Extra(ReadOnlySpan<byte> src, ref int ip)
        {
            int total = 0;
            byte b;
            do
            {
                if (ip >= src.Length) throw Damaged();
                b = src[ip++];
                total += b;
            } while (b == 255);
            return total;
        }
    }

    /// <summary>FastLZ, levels 1 and 2 (the level is in the first byte).</summary>
    public static byte[] FastLzDecompress(ReadOnlySpan<byte> src, long length)
    {
        var dst = new byte[CheckLength(length)];
        if (src.Length == 0) { if (dst.Length != 0) throw Damaged(); return dst; }
        int level = (src[0] >> 5) + 1;
        if (level > 2) throw Damaged();
        int ip = 0, op = 0;
        int ctrl = src[ip++] & 31;
        while (true)
        {
            if (ctrl >= 32)
            {
                int len = (ctrl >> 5) - 1;
                int offset = (ctrl & 31) << 8;
                if (len == 6)
                {
                    if (level == 1) { if (ip >= src.Length) throw Damaged(); len += src[ip++]; }
                    else
                    {
                        byte code;
                        do { if (ip >= src.Length) throw Damaged(); code = src[ip++]; len += code; } while (code == 255);
                    }
                }
                if (ip >= src.Length) throw Damaged();
                int low = src[ip++];
                offset += low;
                if (level == 2 && low == 255 && offset == (31 << 8) + 255)
                {
                    if (ip + 2 > src.Length) throw Damaged();
                    offset = (src[ip] << 8) + src[ip + 1] + 8191;
                    ip += 2;
                }
                int from = op - offset - 1;
                len += 3;
                if (from < 0 || op + len > dst.Length) throw Damaged();
                for (int i = 0; i < len; i++, op++, from++) dst[op] = dst[from];
            }
            else
            {
                int run = ctrl + 1;
                if (ip + run > src.Length || op + run > dst.Length) throw Damaged();
                src.Slice(ip, run).CopyTo(dst.AsSpan(op));
                ip += run; op += run;
            }
            if (ip >= src.Length) break;
            ctrl = src[ip++];
        }
        if (op != dst.Length) throw Damaged();
        return dst;
    }

    private static FstFormatException Damaged() => new("compressed data is damaged");

    /// <summary>"1ns" for -9, "100ps" for -10, ...</summary>
    public static string TimescaleText(int exponent)
    {
        string[] units = ["s", "ms", "us", "ns", "ps", "fs", "as", "zs"];
        if (exponent > 2 || exponent < -21) return "1e" + exponent + "s";
        int unit = exponent > 0 ? 0 : (-exponent + 2) / 3;
        int digits = exponent + unit * 3;                   // 0, 1 or 2
        return (digits == 0 ? "1" : digits == 1 ? "10" : "100") + units[unit];
    }

    /// <summary>Parses "1ns", "10 ps", "100us", ... into a power of ten; false if it is not one.</summary>
    public static bool TryParseTimescale(string text, out int exponent)
    {
        exponent = 0;
        text = text.Replace(" ", "").ToLowerInvariant();
        int digits = 0;
        while (digits < text.Length && char.IsAsciiDigit(text[digits])) digits++;
        int scale = text[..digits] switch { "1" or "" => 0, "10" => 1, "100" => 2, _ => -1 };
        int unit = text[digits..] switch { "s" => 0, "ms" => -3, "us" => -6, "ns" => -9, "ps" => -12, "fs" => -15, "as" => -18, "zs" => -21, _ => 1 };
        if (scale < 0 || unit > 0) return false;
        exponent = unit + scale;
        return true;
    }
}

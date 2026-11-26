using System.Text;

namespace Cameo.Runtime.Vm;

/// <summary>
/// Conversions between .NET text and VM strings.
///
/// A VM <c>string</c> is a sequence of bytes, as in Lua; text is UTF-8 by convention. Inside the
/// VM a byte string is carried in a <see cref="string"/> that holds exactly one byte (0..255)
/// per char, so every string operation (length, indexing, patterns, comparison) works on bytes.
/// These helpers convert at the boundary with the host, the file system and the console.
/// </summary>
public static class LuaText
{
    private static readonly UTF8Encoding Utf8 = new(false);

    private static bool IsAscii(string s)
    {
        foreach (char c in s) if (c >= 0x80) return false;
        return true;
    }

    /// <summary>.NET text -> VM byte string (UTF-8).</summary>
    public static string Encode(string text)
    {
        if (IsAscii(text)) return text;
        return FromBytes(Utf8.GetBytes(text));
    }

    /// <summary>VM byte string -> .NET text (UTF-8; invalid sequences become U+FFFD).</summary>
    public static string Decode(string bytes)
    {
        if (IsAscii(bytes)) return bytes;
        return Utf8.GetString(ToBytes(bytes));
    }

    /// <summary>The raw bytes of a VM string.</summary>
    public static byte[] ToBytes(string bytes) => Encoding.Latin1.GetBytes(bytes); // vectorised; VM strings only hold chars 0..255

    /// <summary>Builds a VM string from raw bytes.</summary>
    public static string FromBytes(ReadOnlySpan<byte> bytes) => Encoding.Latin1.GetString(bytes);

    /// <summary>Reads a whole file as a VM byte string.</summary>
    public static string ReadFile(string path) => FromBytes(File.ReadAllBytes(path));

    /// <summary>Appends the UTF-8 encoding of a code point (Lua's extended range up to 0x7FFFFFFF).</summary>
    // Verified: verification/Utf8.dfy, lemma EncodeIsSpec (and RoundTrip with Utf8Lib.Decode)
    public static void AppendUtf8(StringBuilder sb, long cp)
    {
        if (cp < 0x80) { sb.Append((char)cp); return; }
        // Same algorithm as luaO_utf8esc: fill continuation bytes from the end.
        Span<char> buf = stackalloc char[8];
        int n = 0;
        long first = 0x3f;
        do
        {
            buf[n++] = (char)(0x80 | (cp & 0x3f));
            cp >>= 6;
            first >>= 1;
        } while (cp > first);
        buf[n++] = (char)(((~first << 1) | cp) & 0xFF);
        for (int i = n - 1; i >= 0; i--) sb.Append(buf[i]);
    }
}

/// <summary>
/// The VM's <c>utf16</c> type: an immutable UTF-16 string, i.e. a native .NET <see cref="string"/>.
/// It is a distinct type from the (byte/UTF-8) <c>string</c>; convert with <c>utf16.new(s)</c>
/// and <c>tostring(u)</c>.
/// </summary>
public sealed class Utf16String : IUserdata
{
    public readonly string Text;
    private readonly LuaState _state;
    public Utf16String(LuaState state, string text) { _state = state; Text = text; }
    public string TypeName => "utf16";
    public Table? Metatable { get => _state.Utf16Meta; set { } }
    public override string ToString() => Text;
}

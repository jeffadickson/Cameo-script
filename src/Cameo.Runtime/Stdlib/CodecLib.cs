using System.Security.Cryptography;
using Cameo.Runtime.Vm;

namespace Cameo.Runtime.Stdlib;

/// <summary>
/// <c>base64</c>, <c>hex</c> and <c>crypto</c>: encodings, hashes and secure random data, on
/// byte strings. Decoding returns the result, or nil and a typed <c>DecodeError</c>.
/// </summary>
public static class CodecLib
{
    public static void Open(LuaState L, bool hermetic = false)
    {
        var errorMeta = new Table();
        errorMeta.SetStr("__name", "DecodeError");
        L.Register(errorMeta, "__tostring", static (ref CallContext c) =>
        {
            Table t = c.CheckTable(0);
            return c.Return($"DecodeError({t.GetStr("kind")}): {t.GetStr("message")}");
        });
        Value Error(string kind, string message)
        {
            var t = new Table(0, 2);
            t.SetStr("kind", kind);
            t.SetStr("message", message);
            t.Metatable = errorMeta;
            return Value.FromTable(t);
        }

        var base64 = new Table();
        // encode(s [, url]): url = true gives the URL-safe alphabet without padding
        L.Register(base64, "encode", static (ref CallContext c) =>
        {
            byte[] data = LuaText.ToBytes(c.CheckString(0));
            c.L.CheckStringLength((long)data.Length * 4 / 3 + 4);
            string text = Convert.ToBase64String(data);
            if (c[1].IsTruthy) text = text.TrimEnd('=').Replace('+', '-').Replace('/', '_');
            return c.Return(text);
        });
        // decode accepts both alphabets, with or without padding
        L.Register(base64, "decode", (ref CallContext c) =>
        {
            string text = c.CheckString(0).Replace('-', '+').Replace('_', '/').TrimEnd('=');
            foreach (char ch in text)
                if (!(char.IsAsciiLetterOrDigit(ch) || ch == '+' || ch == '/'))
                    return c.Return(Value.Nil, Error("invalid_base64", "invalid character in base64 text"));
            if (text.Length % 4 == 1) return c.Return(Value.Nil, Error("invalid_base64", "base64 text has an impossible length"));
            text = text.PadRight((text.Length + 3) / 4 * 4, '=');
            var buffer = new byte[text.Length / 4 * 3];
            if (!Convert.TryFromBase64String(text, buffer, out int n))
                return c.Return(Value.Nil, Error("invalid_base64", "invalid base64 text"));
            return c.Return(LuaText.FromBytes(buffer.AsSpan(0, n)), Value.Nil);
        });
        L.Globals.SetStr("base64", base64);
        L.LoadedModules.SetStr("base64", base64);

        var hex = new Table();
        L.Register(hex, "encode", static (ref CallContext c) =>
        {
            byte[] data = LuaText.ToBytes(c.CheckString(0));
            c.L.CheckStringLength((long)data.Length * 2);
            return c.Return(Convert.ToHexStringLower(data));
        });
        L.Register(hex, "decode", (ref CallContext c) =>
        {
            string text = c.CheckString(0);
            if (text.Length % 2 != 0) return c.Return(Value.Nil, Error("invalid_hex", "hex text has an odd length"));
            var buffer = new byte[text.Length / 2];
            if (Convert.FromHexString(text, buffer, out _, out _) != System.Buffers.OperationStatus.Done)
                return c.Return(Value.Nil, Error("invalid_hex", "invalid character in hex text"));
            return c.Return(LuaText.FromBytes(buffer), Value.Nil);
        });
        L.Globals.SetStr("hex", hex);
        L.LoadedModules.SetStr("hex", hex);

        var crypto = new Table();
        // hashes return lower-case hex, or the raw bytes when the second argument is true
        L.Register(crypto, "sha256", static (ref CallContext c) => Digest(ref c, SHA256.HashData(LuaText.ToBytes(c.CheckString(0)))));
        L.Register(crypto, "sha512", static (ref CallContext c) => Digest(ref c, SHA512.HashData(LuaText.ToBytes(c.CheckString(0)))));
        L.Register(crypto, "sha1", static (ref CallContext c) => Digest(ref c, SHA1.HashData(LuaText.ToBytes(c.CheckString(0)))));
        L.Register(crypto, "md5", static (ref CallContext c) => Digest(ref c, MD5.HashData(LuaText.ToBytes(c.CheckString(0)))));
        // hmac(algorithm, key, message [, raw])
        L.Register(crypto, "hmac", static (ref CallContext c) =>
        {
            string algorithm = c.CheckString(0);
            byte[] key = LuaText.ToBytes(c.CheckString(1)), message = LuaText.ToBytes(c.CheckString(2));
            byte[] mac = algorithm switch
            {
                "sha256" => HMACSHA256.HashData(key, message),
                "sha512" => HMACSHA512.HashData(key, message),
                "sha1" => HMACSHA1.HashData(key, message),
                "md5" => HMACMD5.HashData(key, message),
                _ => throw c.ArgError(0, "algorithm must be 'sha256', 'sha512', 'sha1' or 'md5'"),
            };
            return c.Return(c[3].IsTruthy ? LuaText.FromBytes(mac) : Convert.ToHexStringLower(mac));
        });
        if (!hermetic)
        {
            L.Register(crypto, "randomBytes", static (ref CallContext c) =>
            {
                long n = c.CheckInt(0);
                if (n < 0) throw c.ArgError(0, "count must not be negative");
                c.L.CheckStringLength(n);
                return c.Return(LuaText.FromBytes(RandomNumberGenerator.GetBytes((int)n)));
            });
            // randomInt(m, n): a cryptographically secure integer in [m, n]
            L.Register(crypto, "randomInt", static (ref CallContext c) =>
            {
                long m = c.CheckInt(0), n = c.CheckInt(1);
                if (m > n) throw c.ArgError(1, "interval is empty");
                UInt128 span = (UInt128)((Int128)n - m) + 1;
                Span<byte> buf = stackalloc byte[16];
                // rejection sampling: no modulo bias
                UInt128 limit = UInt128.MaxValue - UInt128.MaxValue % span;
                UInt128 r;
                do
                {
                    RandomNumberGenerator.Fill(buf);
                    r = System.Buffers.Binary.BinaryPrimitives.ReadUInt128LittleEndian(buf);
                } while (r >= limit);
                return c.Return((long)((Int128)m + (Int128)(r % span)));
            });
            L.Register(crypto, "uuid", static (ref CallContext c) => c.Return(Guid.NewGuid().ToString()));
        }
        L.Globals.SetStr("crypto", crypto);
        L.LoadedModules.SetStr("crypto", crypto);
    }

    private static int Digest(ref CallContext c, byte[] hash) =>
        c.Return(c[1].IsTruthy ? LuaText.FromBytes(hash) : Convert.ToHexStringLower(hash));
}

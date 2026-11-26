using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace Cameo.Embed;

/// <summary>Line-oriented transport for talking to NexRT's <c>MonitorBridge</c> over UART, TCP, or QEMU stdio.</summary>
public interface IEmbedLineTransport : IDisposable
{
    /// <summary>Sends a single command line (newline appended automatically).</summary>
    void SendLine(string line);

    /// <summary>Reads the next response line within <paramref name="timeout"/>, or returns <see langword="null"/> on EOF/timeout.</summary>
    string? ReadLine(TimeSpan timeout);
}

/// <summary>Stream-backed <see cref="IEmbedLineTransport"/> suitable for serial ports, pipes, and in-memory tests.</summary>
public sealed class StreamLineTransport : IEmbedLineTransport
{
    private readonly TextWriter _writer;
    private readonly TextReader _reader;
    private readonly bool _leaveOpen;

    /// <summary>Creates a line transport over an existing reader/writer pair.</summary>
    public StreamLineTransport(TextReader reader, TextWriter writer, bool leaveOpen = false)
    {
        _reader = reader;
        _writer = writer;
        _leaveOpen = leaveOpen;
    }

    /// <inheritdoc />
    public void SendLine(string line)
    {
        _writer.Write(line);
        _writer.Write('\n');
        _writer.Flush();
    }

    /// <inheritdoc />
    public string? ReadLine(TimeSpan timeout)
    {
        var task = _reader.ReadLineAsync();
        if (!task.Wait(timeout))
            return null;
        return task.Result;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!_leaveOpen)
        {
            _writer.Dispose();
            _reader.Dispose();
        }
    }
}

/// <summary>TCP socket transport (<c>tcp://host:port</c>) for talking to QEMU chardev or networked NexRT monitors.</summary>
public sealed class TcpLineTransport : IEmbedLineTransport
{
    private readonly TcpClient _client;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;

    /// <summary>Connects to a TCP monitor endpoint.</summary>
    public TcpLineTransport(string host, int port)
    {
        _client = new TcpClient();
        _client.Connect(host, port);
        NetworkStream stream = _client.GetStream();
        _reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        _writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { NewLine = "\n" };
    }

    /// <inheritdoc />
    public void SendLine(string line)
    {
        _writer.WriteLine(line);
        _writer.Flush();
    }

    /// <inheritdoc />
    public string? ReadLine(TimeSpan timeout)
    {
        _client.ReceiveTimeout = Math.Max(1, (int)timeout.TotalMilliseconds);
        try
        {
            return _reader.ReadLine();
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _writer.Dispose();
        _reader.Dispose();
        _client.Dispose();
    }
}

/// <summary>Subprocess stdio transport (<c>exec://...</c>) for driving QEMU or target simulators directly.</summary>
public sealed class ProcessLineTransport : IEmbedLineTransport
{
    private readonly Process _process;

    /// <summary>Launches <paramref name="fileName"/> with <paramref name="arguments"/> and binds stdin/stdout.</summary>
    public ProcessLineTransport(string fileName, IReadOnlyList<string> arguments)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string arg in arguments)
            psi.ArgumentList.Add(arg);
        _process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to launch '{fileName}'.");
    }

    /// <inheritdoc />
    public void SendLine(string line)
    {
        _process.StandardInput.Write(line);
        _process.StandardInput.Write('\n');
        _process.StandardInput.Flush();
    }

    /// <inheritdoc />
    public string? ReadLine(TimeSpan timeout)
    {
        var task = _process.StandardOutput.ReadLineAsync();
        if (!task.Wait(timeout))
            return null;
        return task.Result;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.StandardInput.Close();
                if (!_process.WaitForExit(1000))
                    _process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort cleanup
        }
        _process.Dispose();
    }
}

/// <summary>Result of uploading (and optionally saving/running) a <c>.cemb</c> image over a monitor transport.</summary>
public sealed record EmbedUploadResult(
    bool Success,
    int BytesUploaded,
    uint? TargetCrc32C,
    string? RunOutput,
    string? Error);

/// <summary>
/// Compiles/streams verified <c>.cemb</c> packages to NexRT's <c>MonitorBridge</c> using bounded
/// hex frames (<c>script reset</c> → <c>script hex ...</c> → <c>script load</c> → optional
/// <c>script save</c> / <c>script run</c>).
/// </summary>
public static class EmbedUploader
{
    /// <summary>Opens a transport from an endpoint URI (<c>tcp://host:port</c>, <c>exec://cmd args</c>, or device path).</summary>
    public static IEmbedLineTransport OpenEndpoint(string endpoint)
    {
        if (endpoint.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase))
        {
            var uri = new Uri(endpoint);
            return new TcpLineTransport(uri.Host, uri.Port);
        }
        if (endpoint.StartsWith("exec://", StringComparison.OrdinalIgnoreCase))
        {
            string cmd = endpoint["exec://".Length..].Trim();
            string[] parts = SplitCommandLine(cmd);
            if (parts.Length == 0)
                throw new ArgumentException("Empty exec:// command.");
            return new ProcessLineTransport(parts[0], parts.Skip(1).ToArray());
        }
        var fs = new FileStream(endpoint, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        return new StreamLineTransport(
            new StreamReader(fs, Encoding.ASCII, false, leaveOpen: true),
            new StreamWriter(fs, new UTF8Encoding(false), leaveOpen: false));
    }

    /// <summary>
    /// Streams <paramref name="image"/> to the target monitor in chunks (default 32 bytes = 64 hex chars per line,
    /// safely fitting inside NexRT's 128-byte monitor command buffer).
    /// </summary>
    public static EmbedUploadResult Upload(
        IEmbedLineTransport transport,
        EmbedCompiledImage image,
        int? saveSlot = null,
        string? runExport = null,
        IReadOnlyList<long>? runArgs = null,
        int chunkBytes = 32,
        TimeSpan? lineTimeout = null)
    {
        TimeSpan timeout = lineTimeout ?? TimeSpan.FromSeconds(5);
        int step = Math.Clamp(chunkBytes, 4, 48);

        string? resetResp = SendAndAwaitPrefix(transport, "script reset", ["ok ", "err "], timeout);
        if (resetResp is null || !resetResp.StartsWith("ok ", StringComparison.Ordinal))
            return new(false, 0, null, null, $"script reset failed: {resetResp ?? "<timeout>"}");

        byte[] bytes = image.Bytes;
        for (int off = 0; off < bytes.Length; off += step)
        {
            int count = Math.Min(step, bytes.Length - off);
            string hex = Convert.ToHexString(bytes.AsSpan(off, count));
            string? hexResp = SendAndAwaitPrefix(transport, $"script hex {hex}", ["ok ", "err "], timeout);
            if (hexResp is null || !hexResp.StartsWith("ok ", StringComparison.Ordinal))
                return new(false, off, null, null, $"script hex failed at offset {off}: {hexResp ?? "<timeout>"}");
        }

        string? loadResp = SendAndAwaitPrefix(transport, "script load", ["ok ", "err "], timeout);
        if (loadResp is null || !loadResp.StartsWith("ok loaded", StringComparison.Ordinal))
            return new(false, bytes.Length, null, null, $"script load failed: {loadResp ?? "<timeout>"}");

        uint? targetCrc = ParseCrc(loadResp);
        if (targetCrc.HasValue && targetCrc.Value != image.Report.Crc32C)
            return new(false, bytes.Length, targetCrc, null,
                $"CRC mismatch: host=0x{image.Report.Crc32C:X8}, target=0x{targetCrc.Value:X8}");

        if (saveSlot.HasValue)
        {
            string? saveResp = SendAndAwaitPrefix(transport, $"script save {saveSlot.Value}", ["ok ", "err "], timeout);
            if (saveResp is null || !saveResp.StartsWith("ok saved", StringComparison.Ordinal))
                return new(false, bytes.Length, targetCrc, null, $"script save failed: {saveResp ?? "<timeout>"}");
        }

        string? runOutput = null;
        if (!string.IsNullOrEmpty(runExport))
        {
            var sb = new StringBuilder("script run ").Append(runExport);
            if (runArgs is not null)
            {
                foreach (long arg in runArgs)
                    sb.Append(' ').Append(arg.ToString(CultureInfo.InvariantCulture));
            }
            string? runResp = SendAndAwaitPrefix(transport, sb.ToString(), ["ok", "err "], timeout);
            if (runResp is null || !runResp.StartsWith("ok", StringComparison.Ordinal))
                return new(false, bytes.Length, targetCrc, runResp, $"script run failed: {runResp ?? "<timeout>"}");
            runOutput = runResp;
        }

        return new(true, bytes.Length, targetCrc, runOutput, null);
    }

    internal static string? SendAndAwaitPrefix(
        IEmbedLineTransport transport,
        string command,
        string[] prefixes,
        TimeSpan timeout,
        List<string>? collected = null)
    {
        transport.SendLine(command);
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            TimeSpan rem = deadline - DateTime.UtcNow;
            if (rem <= TimeSpan.Zero) break;
            string? raw = transport.ReadLine(rem);
            if (raw is null) return null;
            string line = raw.Trim();
            if (line.Length == 0) continue;
            collected?.Add(line);
            foreach (string p in prefixes)
            {
                if (line.StartsWith(p, StringComparison.Ordinal))
                    return line;
            }
        }
        return null;
    }

    private static uint? ParseCrc(string line)
    {
        int idx = line.IndexOf("crc=0x", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        string rest = line[(idx + 6)..];
        int end = rest.IndexOf(' ');
        if (end >= 0) rest = rest[..end];
        return uint.TryParse(rest, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint crc) ? crc : null;
    }

    private static string[] SplitCommandLine(string input)
    {
        var args = new List<string>();
        var cur = new StringBuilder();
        bool inQuotes = false;
        foreach (char ch in input)
        {
            if (ch == '"') { inQuotes = !inQuotes; continue; }
            if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (cur.Length > 0) { args.Add(cur.ToString()); cur.Clear(); }
                continue;
            }
            cur.Append(ch);
        }
        if (cur.Length > 0) args.Add(cur.ToString());
        return args.ToArray();
    }
}

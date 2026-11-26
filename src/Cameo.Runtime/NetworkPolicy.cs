using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace Cameo.Runtime;

/// <summary>An immutable host/scheme rule for managed-host network egress control.</summary>
public sealed class NetworkRule
{
    /// <summary>Normalized host pattern: an exact hostname or <c>*.domain.tld</c>.</summary>
    public string HostPattern { get; }

    /// <summary>Whether plain HTTP is allowed in addition to HTTPS. Defaults to false (HTTPS only).</summary>
    public bool AllowHttp { get; }

    /// <summary>Optional exact port requirement; null allows any port.</summary>
    public int? Port { get; }

    private readonly bool _wildcard;
    private readonly string _suffix;

    /// <summary>Creates a network egress rule.</summary>
    /// <param name="hostPattern">An exact hostname (for example <c>api.example.com</c>) or a subdomain wildcard (<c>*.example.com</c>).</param>
    /// <param name="allowHttp">True to permit unencrypted <c>http://</c>; false allows <c>https://</c> only.</param>
    /// <param name="port">Optional exact TCP port.</param>
    /// <exception cref="ArgumentException">The host pattern or port is invalid.</exception>
    public NetworkRule(string hostPattern, bool allowHttp = false, int? port = null)
    {
        if (string.IsNullOrWhiteSpace(hostPattern) || hostPattern.Contains('\0') || hostPattern.Any(char.IsWhiteSpace))
            throw new ArgumentException("A valid hostname or '*.domain' pattern is required", nameof(hostPattern));
        if (port is <= 0 or > 65535)
            throw new ArgumentException("Port must be between 1 and 65535", nameof(port));
        string trimmed = hostPattern.Trim().TrimEnd('.').ToLowerInvariant();
        if (trimmed == "*" || trimmed == "*." || trimmed.Length == 0)
            throw new ArgumentException("Blanket wildcard '*' is not allowed; specify a domain", nameof(hostPattern));
        if (trimmed.StartsWith("*.", StringComparison.Ordinal))
        {
            string rest = trimmed[2..];
            if (rest.Length == 0 || rest.Contains('*') || rest.Contains('/') || rest.Contains(':'))
                throw new ArgumentException("Wildcard pattern must have the form '*.example.com'", nameof(hostPattern));
            _wildcard = true;
            _suffix = "." + rest;
            HostPattern = trimmed;
        }
        else
        {
            if (trimmed.Contains('*') || trimmed.Contains('/'))
                throw new ArgumentException("Host pattern may only contain a leading '*.' wildcard", nameof(hostPattern));
            _wildcard = false;
            _suffix = trimmed;
            HostPattern = trimmed;
        }
        AllowHttp = allowHttp;
        Port = port;
    }

    internal bool Matches(Uri uri)
    {
        bool schemeOk = uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)
            || (AllowHttp && uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase));
        if (!schemeOk) return false;
        if (Port is { } expectedPort && uri.Port != expectedPort) return false;
        string host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        if (string.IsNullOrEmpty(host)) return false;
        if (_wildcard)
            return host.Length > _suffix.Length && host.EndsWith(_suffix, StringComparison.Ordinal);
        return string.Equals(host, _suffix, StringComparison.Ordinal);
    }
}

internal sealed class NetworkPolicyBlockedException(string message) : Exception(message);

/// <summary>An immutable allowlist that restricts HTTP/HTTPS egress in addition to <see cref="CameoRuntimeOptions.AllowNetwork"/>.</summary>
/// <remarks>Inherited automatically by parallel workers and child sandboxes. Never grants
/// <see cref="CameoRuntimeOptions.AllowNetwork"/> on its own.</remarks>
public sealed class NetworkPolicy
{
    private readonly NetworkRule[] _rules;
    private readonly Lazy<HttpClient> _sharedClient;

    /// <summary>Whether loopback, private (RFC1918/ULA), link-local and cloud-metadata addresses are blocked.</summary>
    public bool BlockPrivateAddresses { get; }

    /// <summary>Optional maximum HTTP response body size in bytes; null disables the cap.</summary>
    public long? MaxResponseBytes { get; }

    /// <summary>Maximum number of HTTP redirects followed per request (0 disables redirects). Defaults to 10.</summary>
    public int MaxRedirects { get; }

    /// <summary>Creates an immutable network allowlist.</summary>
    public NetworkPolicy(
        IEnumerable<NetworkRule> rules,
        bool blockPrivateAddresses = true,
        long? maxResponseBytes = null,
        int maxRedirects = 10)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (maxResponseBytes is <= 0) throw new ArgumentOutOfRangeException(nameof(maxResponseBytes), "MaxResponseBytes must be positive");
        if (maxRedirects < 0) throw new ArgumentOutOfRangeException(nameof(maxRedirects), "MaxRedirects must be non-negative");
        _rules = rules.ToArray();
        if (_rules.Any(rule => rule is null)) throw new ArgumentException("Rules must be non-null", nameof(rules));
        BlockPrivateAddresses = blockPrivateAddresses;
        MaxResponseBytes = maxResponseBytes;
        MaxRedirects = maxRedirects;
        _sharedClient = new Lazy<HttpClient>(() => CreateHttpClient(cookies: null));
    }

    internal HttpClient GetSharedClient() => _sharedClient.Value;

    /// <summary>Creates an <see cref="HttpClient"/> that disables automatic redirects and validates every
    /// resolved IP address inside <see cref="SocketsHttpHandler.ConnectCallback"/> before connecting,
    /// preventing redirect bypasses and DNS-rebinding TOCTOU attacks.</summary>
    internal HttpClient CreateHttpClient(CookieContainer? cookies)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = cookies != null,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = async (context, ct) =>
            {
                string host = context.DnsEndPoint.Host;
                IPAddress[] addresses = IPAddress.TryParse(host, out IPAddress? literal)
                    ? [literal]
                    : await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
                if (addresses.Length == 0)
                    throw new SocketException((int)SocketError.HostNotFound);
                if (BlockPrivateAddresses)
                {
                    foreach (IPAddress ip in addresses)
                        if (IsPrivateOrLoopback(ip))
                            throw new NetworkPolicyBlockedException($"resolved address '{ip}' is blocked by host network policy");
                }
                Exception? lastError = null;
                foreach (IPAddress ip in addresses)
                {
                    var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        await socket.ConnectAsync(ip, context.DnsEndPoint.Port, ct).ConfigureAwait(false);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        socket.Dispose();
                        lastError = ex;
                    }
                }
                throw lastError ?? new SocketException((int)SocketError.HostUnreachable);
            },
        };
        if (cookies != null) handler.CookieContainer = cookies;
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>Tests whether a request to <paramref name="url"/> is permitted by this policy.</summary>
    public bool Allows(string? url, Uri? baseUrl = null) => TryAuthorize(url, baseUrl, out _, out _);

    internal bool TryAuthorize(string? url, Uri? baseUrl, out Uri? resolved, out string reason)
    {
        resolved = null;
        if (string.IsNullOrWhiteSpace(url) || url.Contains('\0'))
        {
            reason = "invalid URL";
            return false;
        }
        Uri? uri = baseUrl != null && Uri.TryCreate(baseUrl, url, out Uri? rel) ? rel
            : Uri.TryCreate(url, UriKind.Absolute, out Uri? abs) ? abs : null;
        if (uri == null || !uri.IsAbsoluteUri)
        {
            reason = "URL must be absolute";
            return false;
        }
        if (!uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) && !uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase))
        {
            reason = $"scheme '{uri.Scheme}' is not permitted by network policy";
            return false;
        }
        if (BlockPrivateAddresses && IsBlockedHost(uri.IdnHost))
        {
            reason = $"private or loopback address '{uri.IdnHost}' is blocked by network policy";
            return false;
        }
        foreach (NetworkRule rule in _rules)
        {
            if (rule.Matches(uri))
            {
                resolved = uri;
                reason = "";
                return true;
            }
        }
        reason = $"URL '{uri.Host}' is not permitted by the host's network policy";
        return false;
    }

    internal static bool IsBlockedHost(string host)
    {
        string normalized = host.Trim().Trim('[', ']').TrimEnd('.');
        if (normalized.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            return true;
        if (IPAddress.TryParse(normalized, out IPAddress? ip))
            return IsPrivateOrLoopback(ip);
        return false;
    }

    /// <summary>True when <paramref name="address"/> is loopback, unspecified, RFC1918 private,
    /// CGNAT, link-local/metadata, or IPv6 unique-local/link-local.</summary>
    public static bool IsPrivateOrLoopback(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] b = address.GetAddressBytes();
            if (b[0] == 0) return true;                                  // 0.0.0.0/8
            if (b[0] == 10) return true;                                 // 10.0.0.0/8
            if (b[0] == 127) return true;                                // 127.0.0.0/8
            if (b[0] == 169 && b[1] == 254) return true;                 // 169.254.0.0/16 (link-local / cloud metadata)
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;    // 172.16.0.0/12
            if (b[0] == 192 && b[1] == 168) return true;                 // 192.168.0.0/16
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true;   // 100.64.0.0/10 (CGNAT)
            return false;
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.Equals(IPAddress.IPv6None) || address.Equals(IPAddress.IPv6Any)) return true;
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal) return true;
            byte[] b = address.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return true;                      // fc00::/7 (ULA)
        }
        return false;
    }
}

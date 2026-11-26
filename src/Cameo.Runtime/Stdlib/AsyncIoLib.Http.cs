using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Cameo.Runtime.Vm;

namespace Cameo.Runtime.Stdlib;

/// <summary>An HTTP session (userdata): keeps cookies and default headers between requests.</summary>
public sealed class LuaHttpSession : IUserdata, IDisposable
{
    internal readonly HttpClient Client;
    internal readonly List<KeyValuePair<string, string>> Headers;
    internal readonly Uri? BaseUrl;
    private readonly Table _meta;

    internal LuaHttpSession(Table meta, List<KeyValuePair<string, string>> headers, Uri? baseUrl, NetworkPolicy? policy)
    {
        _meta = meta;
        Headers = headers;
        BaseUrl = baseUrl;
        Client = policy != null
            ? policy.CreateHttpClient(new CookieContainer())
            : new HttpClient(new SocketsHttpHandler { UseCookies = true, CookieContainer = new CookieContainer() })
            {
                Timeout = System.Threading.Timeout.InfiniteTimeSpan,
            };
    }

    public string TypeName => "HttpSession";
    public Table? Metatable { get => _meta; set { } }
    public void Dispose() => Client.Dispose();
}

// HTTP: requests, JSON helpers, uploads, downloads, sessions with cookies.
public static partial class AsyncIoLib
{
    /// <summary>Everything about one request, gathered on the VM thread before it starts.</summary>
    private sealed class HttpSpec
    {
        public string Method = "GET";
        public string Url = "";
        public List<KeyValuePair<string, string>> Headers = new();
        public byte[]? Body;
        public string? BodyFile;
        public List<KeyValuePair<string, string>>? Form;
        public List<KeyValuePair<string, string>>? Files;
        public double Timeout = DefaultHttpTimeout;
        public HttpClient Client = Http;
        public bool FromSession;
        public Uri? BaseUrl;
    }

    private enum HttpResult { Response, Json, Download, Stream }

    private static void OpenHttp(LuaState L, Table aio)
    {
        // Each function exists twice: on `aio` (no cookies, no defaults) and as a method of a session.
        foreach (bool onSession in new[] { false, true })
        {
            Table target = onSession ? new Table() : aio;
            int a = onSession ? 1 : 0; // index of the first real argument
            void Reg(string name, NativeFn fn)
            {
                if (onSession) L.RegisterInstance<LuaHttpSession>(target, name, fn);
                else L.Register(target, name, fn);
            }
            Reg(onSession ? "get" : "httpGet", (ref CallContext c) =>
            {
                HttpSpec spec = Base(ref c, onSession);
                spec.Url = c.CheckText(a);
                AddHeaders(spec, c.IsNoneOrNil(a + 1) ? null : c.CheckTable(a + 1));
                return Send(ref c, spec, HttpResult.Response, null);
            });
            Reg(onSession ? "request" : "httpRequest", (ref CallContext c) =>
            {
                HttpSpec spec = Base(ref c, onSession);
                Request(ref c, a, spec);
                return Send(ref c, spec, HttpResult.Response, null);
            });
            // getJson(url [, headers]): the decoded body; an error status or a body that is not JSON fails the future
            Reg("getJson", (ref CallContext c) =>
            {
                HttpSpec spec = Base(ref c, onSession);
                spec.Url = c.CheckText(a);
                spec.Headers.Add(new("Accept", "application/json"));
                AddHeaders(spec, c.IsNoneOrNil(a + 1) ? null : c.CheckTable(a + 1));
                return Send(ref c, spec, HttpResult.Json, null);
            });
            // postJson(url, value [, headers]): sends the value as JSON, returns the decoded answer (nil if the body is empty)
            Reg("postJson", (ref CallContext c) =>
            {
                HttpSpec spec = Base(ref c, onSession);
                spec.Method = "POST";
                spec.Url = c.CheckText(a);
                c.CheckAny(a + 1);
                if (!JsonLib.TryEncode(c.L, c[a + 1], out string text, out string? problem)) throw c.ArgError(a + 1, problem!);
                spec.Body = LuaText.ToBytes(text);
                spec.Headers.Add(new("Content-Type", "application/json"));
                spec.Headers.Add(new("Accept", "application/json"));
                AddHeaders(spec, c.IsNoneOrNil(a + 2) ? null : c.CheckTable(a + 2));
                return Send(ref c, spec, HttpResult.Json, null);
            });
            // download(url, path [, headers]): writes the body to a file as it arrives; the response has an empty body
            Reg("download", (ref CallContext c) =>
            {
                HttpSpec spec = Base(ref c, onSession);
                spec.Url = c.CheckText(a);
                string path = c.CheckText(a + 1);
                AddHeaders(spec, c.IsNoneOrNil(a + 2) ? null : c.CheckTable(a + 2));
                return Send(ref c, spec, HttpResult.Download, path);
            });
            // httpStream(url or request): the response as soon as its headers are in; body is a Stream
            Reg(onSession ? "stream" : "httpStream", (ref CallContext c) =>
            {
                HttpSpec spec = Base(ref c, onSession);
                if (c[a].IsString) spec.Url = c.CheckText(a); else Request(ref c, a, spec);
                return Send(ref c, spec, HttpResult.Stream, null);
            });
            if (!onSession) continue;
            L.RegisterInstance<LuaHttpSession>(target, "close", static (ref CallContext c) =>
            {
                (c[0].O as LuaHttpSession ?? throw c.TypeError(0, "HttpSession")).Dispose();
                return c.Return();
            });
            L.MetaFor("HttpSession").SetStr("__index", target);
        }

        // httpSession([{headers =, baseUrl =}]): requests made through it share cookies and defaults
        L.Register(aio, "httpSession", static (ref CallContext c) =>
        {
            var headers = new List<KeyValuePair<string, string>>();
            Uri? baseUrl = null;
            if (!c.IsNoneOrNil(0))
            {
                Table o = c.CheckTable(0);
                headers = HeaderList(o.GetStr("headers").O as Table);
                if (o.GetStr("baseUrl").IsString && !Uri.TryCreate(LuaText.Decode(o.GetStr("baseUrl").AsString), UriKind.Absolute, out baseUrl))
                    throw c.ArgError(0, "field 'baseUrl' is not an absolute URL");
            }
            return c.Return(Value.FromUserdata(new LuaHttpSession(c.L.MetaFor("HttpSession"), headers, baseUrl, c.L.NetworkPolicy)));
        });
    }

    private static HttpSpec Base(ref CallContext c, bool onSession)
    {
        var spec = new HttpSpec();
        if (!onSession) return spec;
        LuaHttpSession s = c[0].O as LuaHttpSession ?? throw c.TypeError(0, "HttpSession");
        spec.Client = s.Client;
        spec.FromSession = true;
        spec.BaseUrl = s.BaseUrl;
        spec.Headers.AddRange(s.Headers);
        return spec;
    }

    private static List<KeyValuePair<string, string>> HeaderList(Table? t)
    {
        List<KeyValuePair<string, string>> list = [with(capacity: 4)];
        if (t == null) return list;
        foreach (KeyValuePair<Value, Value> kv in t.Pairs())
            if (kv.Key.IsString && (kv.Value.IsString || kv.Value.IsNumber))
                list.Add(new(LuaText.Decode(kv.Key.AsString), LuaText.Decode(kv.Value.ToString())));
        return list;
    }

    private static void AddHeaders(HttpSpec spec, Table? t) => spec.Headers.AddRange(HeaderList(t));

    // Reads a request table: url, method, headers, timeout, and one kind of body:
    // body (bytes), json (a value), bodyFile (a path), form (fields), files (name -> path, sent
    // as multipart together with form).
    private static void Request(ref CallContext c, int arg, HttpSpec spec)
    {
        Table req = c.CheckTable(arg);
        Value url = req.GetStr("url");
        if (!url.IsString) throw c.ArgError(arg, "field 'url' must be a string");
        spec.Url = LuaText.Decode(url.AsString);
        if (req.GetStr("method").IsString) spec.Method = LuaText.Decode(req.GetStr("method").AsString).ToUpperInvariant();
        if (req.GetStr("timeout").IsNumber) spec.Timeout = req.GetStr("timeout").AsDouble;
        int bodies = 0;
        if (req.GetStr("body").IsString) { spec.Body = LuaText.ToBytes(req.GetStr("body").AsString); bodies++; }
        if (!req.GetStr("json").IsNil)
        {
            if (!JsonLib.TryEncode(c.L, req.GetStr("json"), out string text, out string? problem)) throw c.ArgError(arg, "field 'json': " + problem);
            spec.Body = LuaText.ToBytes(text);
            spec.Headers.Add(new("Content-Type", "application/json"));
            bodies++;
        }
        if (req.GetStr("bodyFile").IsString) { spec.BodyFile = LuaText.Decode(req.GetStr("bodyFile").AsString); bodies++; }
        if (req.GetStr("form").O is Table form) { spec.Form = HeaderList(form); bodies++; }
        if (req.GetStr("files").O is Table files) { spec.Files = HeaderList(files); if (spec.Form == null) bodies++; }
        if (bodies > 1) throw c.ArgError(arg, "give only one of 'body', 'json', 'bodyFile' and 'form'/'files'");
        if (bodies > 0 && req.GetStr("method").IsNil) spec.Method = "POST";
        AddHeaders(spec, req.GetStr("headers").O as Table); // the caller's headers win over the defaults above
    }

    private static HttpRequestMessage Build(HttpSpec spec, List<IDisposable> owned)
    {
        Uri uri = spec.BaseUrl != null ? new Uri(spec.BaseUrl, spec.Url) : new Uri(spec.Url, UriKind.Absolute);
        var req = new HttpRequestMessage(new HttpMethod(spec.Method), uri);
        if (spec.Body != null) req.Content = new ByteArrayContent(spec.Body);
        else if (spec.BodyFile != null)
        {
            var file = File.OpenRead(spec.BodyFile);
            owned.Add(file);
            req.Content = new StreamContent(file);
        }
        else if (spec.Files != null)
        {
            var multi = new MultipartFormDataContent();
            foreach (var (name, value) in spec.Form ?? []) multi.Add(new StringContent(value), name);
            foreach (var (name, path) in spec.Files)
            {
                var file = File.OpenRead(path);
                owned.Add(file);
                var part = new StreamContent(file);
                part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                multi.Add(part, name, Path.GetFileName(path));
            }
            req.Content = multi;
        }
        else if (spec.Form != null) req.Content = new FormUrlEncodedContent(spec.Form);

        foreach (var (name, value) in spec.Headers)
        {
            // later entries replace earlier ones; content headers (Content-Type, ...) live on the body
            if (req.Headers.TryAddWithoutValidation(name, value)) { req.Headers.Remove(name); req.Headers.TryAddWithoutValidation(name, value); }
            else if (req.Content != null)
            {
                req.Content.Headers.Remove(name);
                req.Content.Headers.TryAddWithoutValidation(name, value);
            }
        }
        return req;
    }

    private static Table ResponseTable(int status, (string Name, string Value)[] headers, Value body)
    {
        var t = new Table(0, 3);
        t.SetStr("status", status);
        t.SetStr("body", body);
        var ht = new Table(0, headers.Length);
        foreach (var (name, value) in headers) ht.Set(Value.FromText(name), Value.FromText(value));
        t.SetStr("headers", ht);
        return t;
    }

    private static (string, string)[] ResponseHeaders(HttpResponseMessage resp) =>
        resp.Headers.Concat(resp.Content.Headers).Select(h => (h.Key.ToLowerInvariant(), string.Join(", ", h.Value))).ToArray();

    private static int Send(ref CallContext c, HttpSpec spec, HttpResult kind, string? savePath)
    {
        string url = spec.Url;
        if (!c.L.AllowNetwork) return Denied(ref c, url, "network access is not enabled for this VM");
        NetworkPolicy? netPolicy = c.L.NetworkPolicy;
        Uri? authorizedUri = null;
        if (netPolicy != null)
        {
            if (!netPolicy.TryAuthorize(url, spec.BaseUrl, out authorizedUri, out string reason))
                return Denied(ref c, url, reason);
        }
        if (spec.BodyFile != null)
        {
            if (!c.L.AuthorizeFile(spec.BodyFile, write: false, out string canonicalBody))
                return Denied(ref c, spec.BodyFile, "file access is not permitted by host file policy");
            spec.BodyFile = canonicalBody;
        }
        if (spec.Files != null)
        {
            for (int i = 0; i < spec.Files.Count; i++)
            {
                var (field, path) = spec.Files[i];
                if (!c.L.AuthorizeFile(path, write: false, out string canonicalFile))
                    return Denied(ref c, path, "file access is not permitted by host file policy");
                spec.Files[i] = new(field, canonicalFile);
            }
        }
        if (kind == HttpResult.Download && savePath != null)
        {
            if (!c.L.AuthorizeFile(savePath, write: true, out string canonicalSave))
                return Denied(ref c, savePath, "file access is not permitted by host file policy");
            savePath = canonicalSave;
        }
        // a streamed body can outlive any sensible request timeout: the limit then covers the headers only
        double timeout = spec.Timeout;
        return Start(ref c, url, async ct =>
        {
            var owned = new List<IDisposable>();
            HttpResponseMessage? resp = null;
            bool keepOpen = false;
            try
            {
                HttpClient client = netPolicy != null && !spec.FromSession
                    ? netPolicy.GetSharedClient()
                    : spec.Client;
                HttpCompletionOption completion = kind is HttpResult.Download or HttpResult.Stream || netPolicy?.MaxResponseBytes != null
                    ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead;

                if (netPolicy == null)
                {
                    using HttpRequestMessage req = Build(spec, owned);
                    resp = await client.SendAsync(req, completion, ct).ConfigureAwait(false);
                }
                else
                {
                    resp = await SendWithPolicyRedirectsAsync(client, spec, netPolicy, authorizedUri!, completion, owned, ct).ConfigureAwait(false);
                }

                int status = (int)resp.StatusCode;
                var headers = ResponseHeaders(resp);
                long? maxBytes = netPolicy?.MaxResponseBytes;
                switch (kind)
                {
                    case HttpResult.Response:
                        {
                            string text = LuaText.FromBytes(await ReadBytesBoundedAsync(resp.Content, maxBytes, url, ct).ConfigureAwait(false));
                            return _ => Value.FromTable(ResponseTable(status, headers, Value.FromString(text)));
                        }
                    case HttpResult.Json:
                        {
                            string text = LuaText.FromBytes(await ReadBytesBoundedAsync(resp.Content, maxBytes, url, ct).ConfigureAwait(false));
                            if (status < 200 || status > 299)
                                throw new IoFailure(new IoError(IoErrorKind.Http, $"HTTP {status}", url));
                            return L =>
                            {
                                if (text.Length == 0) return Value.Nil;
                                if (!JsonLib.TryDecode(L, text, out Value v, out string? problem))
                                    throw new IoFailure(new IoError(IoErrorKind.Invalid, "the response is not JSON: " + problem, url));
                                return v;
                            };
                        }
                    case HttpResult.Download:
                        {
                            if (status >= 200 && status <= 299)
                            {
                                await using FileStream file = File.Create(savePath!);
                                await CopyBoundedAsync(resp.Content, file, maxBytes, url, ct).ConfigureAwait(false);
                            }
                            return _ => Value.FromTable(ResponseTable(status, headers, Value.FromString("")));
                        }
                    default:
                        {
                            Stream body = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                            HttpResponseMessage open = resp;
                            keepOpen = true;
                            return PendingResource.Transfer(open, ct,
                                (L, owner) => Value.FromTable(ResponseTable(status, headers, Value.FromUserdata(new LuaStream(L, body, url, owner)))),
                                owner => { owner.Dispose(); return ValueTask.CompletedTask; });
                        }
                }
            }
            catch (IoFailure f)
            {
                // surfaces through the normal error path with its own kind
                throw new IoFailureCarrier(f.Error);
            }
            catch (Exception ex) when (FindPolicyBlocked(ex) is { } blocked)
            {
                throw new IoFailureCarrier(new IoError(IoErrorKind.PermissionDenied, blocked.Message, url));
            }
            finally
            {
                foreach (IDisposable d in owned) d.Dispose();
                if (!keepOpen) resp?.Dispose();
            }
        }, timeout);
    }

    private static NetworkPolicyBlockedException? FindPolicyBlocked(Exception ex)
    {
        for (Exception? cur = ex; cur != null; cur = cur.InnerException)
            if (cur is NetworkPolicyBlockedException blocked) return blocked;
        return null;
    }

    private static bool IsRedirect(HttpStatusCode code) => code is
        HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
        or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static bool SameOrigin(Uri a, Uri b) =>
        string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.IdnHost, b.IdnHost, StringComparison.OrdinalIgnoreCase)
        && a.Port == b.Port;

    private static async Task<HttpResponseMessage> SendWithPolicyRedirectsAsync(
        HttpClient client, HttpSpec initialSpec, NetworkPolicy policy, Uri initialUri,
        HttpCompletionOption completion, List<IDisposable> owned, CancellationToken ct)
    {
        HttpSpec current = initialSpec;
        Uri currentUri = initialUri;
        for (int hop = 0; ; hop++)
        {
            using HttpRequestMessage req = Build(current, owned);
            HttpResponseMessage resp = await client.SendAsync(req, completion, ct).ConfigureAwait(false);
            if (!IsRedirect(resp.StatusCode) || resp.Headers.Location == null)
                return resp;

            Uri? nextUri = Uri.TryCreate(currentUri, resp.Headers.Location, out Uri? resolved) ? resolved : null;
            resp.Dispose();
            if (hop >= policy.MaxRedirects)
                throw new IoFailure(new IoError(IoErrorKind.Http, $"too many redirects (limit {policy.MaxRedirects})", initialSpec.Url));
            if (nextUri == null)
                throw new IoFailure(new IoError(IoErrorKind.PermissionDenied, "invalid redirect URL", initialSpec.Url));
            if (!policy.TryAuthorize(nextUri.AbsoluteUri, null, out Uri? authorizedNext, out string reason))
                throw new IoFailure(new IoError(IoErrorKind.PermissionDenied, reason, nextUri.AbsoluteUri));

            bool dropBody = resp.StatusCode == HttpStatusCode.SeeOther
                || ((resp.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found)
                    && string.Equals(current.Method, "POST", StringComparison.OrdinalIgnoreCase));
            bool crossOrigin = !SameOrigin(currentUri, authorizedNext!);
            var nextHeaders = new List<KeyValuePair<string, string>>(current.Headers.Count);
            foreach (var h in current.Headers)
            {
                if (crossOrigin && (h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                    || h.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (dropBody && h.Key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
                    continue;
                nextHeaders.Add(h);
            }
            current = new HttpSpec
            {
                Method = dropBody ? "GET" : current.Method,
                Url = authorizedNext!.AbsoluteUri,
                Headers = nextHeaders,
                Body = dropBody ? null : current.Body,
                BodyFile = dropBody ? null : current.BodyFile,
                Form = dropBody ? null : current.Form,
                Files = dropBody ? null : current.Files,
                Timeout = current.Timeout,
                Client = current.Client,
                FromSession = current.FromSession,
                BaseUrl = null,
            };
            currentUri = authorizedNext;
        }
    }

    private static async Task<byte[]> ReadBytesBoundedAsync(HttpContent content, long? maxBytes, string url, CancellationToken ct)
    {
        if (maxBytes == null)
            return await content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        if (content.Headers.ContentLength is { } len && len > maxBytes.Value)
            throw new IoFailure(new IoError(IoErrorKind.PermissionDenied, $"response size ({len} bytes) exceeds host limit ({maxBytes.Value} bytes)", url));
        await using Stream stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        byte[] buffer = new byte[16384];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > maxBytes.Value)
                throw new IoFailure(new IoError(IoErrorKind.PermissionDenied, $"response exceeded host size limit ({maxBytes.Value} bytes)", url));
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }

    private static async Task CopyBoundedAsync(HttpContent content, Stream target, long? maxBytes, string url, CancellationToken ct)
    {
        if (maxBytes == null)
        {
            await content.CopyToAsync(target, ct).ConfigureAwait(false);
            return;
        }
        if (content.Headers.ContentLength is { } len && len > maxBytes.Value)
            throw new IoFailure(new IoError(IoErrorKind.PermissionDenied, $"response size ({len} bytes) exceeds host limit ({maxBytes.Value} bytes)", url));
        await using Stream stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        byte[] buffer = new byte[16384];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > maxBytes.Value)
                throw new IoFailure(new IoError(IoErrorKind.PermissionDenied, $"response exceeded host size limit ({maxBytes.Value} bytes)", url));
            await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
    }
}

using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MiniDrop.Tests;

/// <summary>
/// 进程内假 WebDAV 服务器（TcpListener + 最小 HTTP/1.1 解析，Connection: close）。
/// 支持 MKCOL/PUT/GET/DELETE/PROPFIND(分页)、故障注入（status==0 表示中断连接模拟网络错误）、请求顺序记录。
/// 路径键为绝对路径（如 /MiniDrop/items/2026-09/&lt;ulid&gt;.json）。
/// </summary>
public sealed class FakeWebDavServer : IDisposable
{
    private sealed record Entry(byte[]? Content, bool IsCollection, string Etag, DateTimeOffset Modified);

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly object _gate = new();
    private readonly List<(string Method, int Status, int Remaining)> _failures = [];

    private readonly Dictionary<string, Entry> _store = new(StringComparer.Ordinal);

    public string RootUrl { get; }
    public List<string> RequestLog { get; } = [];
    public int? ForcePageSize { get; set; }

    public FakeWebDavServer()
    {
        var port = GetFreePort();
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        RootUrl = $"http://127.0.0.1:{port}/MiniDrop/";
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    private static int GetFreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    // ---------- 测试辅助 ----------

    public void PutFile(string path, byte[] content)
    {
        lock (_gate)
            _store[path] = new Entry(content, false, Guid.NewGuid().ToString("D"), DateTimeOffset.UtcNow);
    }

    public byte[]? GetFile(string path)
    {
        lock (_gate)
            return _store.TryGetValue(path, out var e) && !e.IsCollection ? e.Content : null;
    }

    public bool Exists(string path)
    {
        lock (_gate) return _store.ContainsKey(path);
    }

    public void MkCol(string path)
    {
        lock (_gate)
        {
            var key = path.EndsWith('/') ? path : path + "/";
            if (!_store.ContainsKey(key))
                _store[key] = new Entry(null, true, Guid.NewGuid().ToString("D"), DateTimeOffset.UtcNow);
        }
    }

    public void FailNext(string method, int status, int times, string pathPrefix = "")
    {
        // status == 0 表示直接中断连接（模拟网络错误）；pathPrefix 限定注入范围
        lock (_failures) _failures.Add((method + "\u0001" + pathPrefix, status, times));
    }

    public List<string> SnapshotRequests()
    {
        lock (RequestLog) return [.. RequestLog];
    }

    public int CountRequests(string methodAndPrefix)
    {
        lock (RequestLog) return RequestLog.Count(r => r.StartsWith(methodAndPrefix, StringComparison.Ordinal));
    }

    // ---------- HTTP 循环 ----------

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch { return; }
            using (client)
            {
                try { await HandleAsync(client, ct).ConfigureAwait(false); }
                catch { /* 连接中断等价于客户端取消 */ }
            }
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        using var stream = client.GetStream();
        var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        readCts.CancelAfter(TimeSpan.FromSeconds(15));

        var (ok, method, rawPath, body) = await ReadRequestAsync(stream, readCts.Token).ConfigureAwait(false);
        if (!ok) return;

        var path = Uri.UnescapeDataString(rawPath.Split('?')[0]);
        var query = rawPath.Contains('?') ? rawPath[(rawPath.IndexOf('?') + 1)..] : "";
        lock (RequestLog) RequestLog.Add($"{method} {path}");

        var injected = TakeInjection(method, path);

        if (injected == 0)
        {
            client.Client.Close();
            return;
        }

        switch (method)
        {
            case "MKCOL":
            {
                var status = injected ?? HandleMkCol(path);
                await RespondAsync(stream, status, "").ConfigureAwait(false);
                break;
            }
            case "PUT":
            {
                var status = injected ?? HandlePut(path, body);
                await RespondAsync(stream, status, "").ConfigureAwait(false);
                break;
            }
            case "DELETE":
            {
                var status = injected ?? HandleDelete(path);
                await RespondAsync(stream, status, "").ConfigureAwait(false);
                break;
            }
            case "GET":
            {
                if (injected is int s)
                {
                    await RespondAsync(stream, s, "", retryAfter: s == 429 ? 1 : null).ConfigureAwait(false);
                }
                else
                {
                    var (found, content) = HandleGet(path);
                    if (found) await RespondAsync(stream, 200, "", rawBytes: content).ConfigureAwait(false);
                    else await RespondAsync(stream, 404, "").ConfigureAwait(false);
                }
                break;
            }
            case "PROPFIND":
            {
                var xml = HandlePropfind(path, query);
                int status;
                if (injected is int s2) status = s2;
                else status = xml is null ? 404 : 207;
                var payload = xml ?? "";
                await RespondAsync(stream, status, payload, contentType: "application/xml;charset=utf-8",
                    retryAfter: injected == 429 ? 1 : null).ConfigureAwait(false);
                break;
            }
            default:
                await RespondAsync(stream, 405, "").ConfigureAwait(false);
                break;
        }
    }

    private int? TakeInjection(string method, string path)
    {
        lock (_failures)
        {
            var idx = _failures.FindIndex(f => f.Remaining > 0
                && f.Method.Split('\u0001')[0] == method
                && path.StartsWith(f.Method.Split('\u0001')[1], StringComparison.Ordinal));
            if (idx < 0) return null;
            var f = _failures[idx];
            _failures[idx] = (f.Method, f.Status, f.Remaining - 1);
            return f.Status;
        }
    }

    private static async Task<(bool Ok, string Method, string RawPath, byte[] Body)> ReadRequestAsync(
        NetworkStream stream, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        var buf = new byte[8192];
        int headerEnd;
        while (true)
        {
            headerEnd = IndexOfHeaderEnd(buffer);
            if (headerEnd >= 0) break;
            var n = await stream.ReadAsync(buf.AsMemory(0, buf.Length), ct).ConfigureAwait(false);
            if (n == 0) return default;
            buffer.Write(buf, 0, n);
            if (buffer.Length > 1024 * 1024) return default;
        }

        var bytes = buffer.ToArray();
        var headerText = Encoding.ASCII.GetString(bytes, 0, headerEnd);
        var lines = headerText.Split("\r\n");
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2) return default;
        var method = requestLine[0];
        var rawPath = requestLine[1];

        long contentLength = 0;
        foreach (var line in lines.Skip(1))
        {
            var kv = line.Split(':', 2);
            if (kv.Length == 2 && kv[0].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                long.TryParse(kv[1].Trim(), out contentLength);
        }

        var body = new byte[contentLength];
        var already = (int)Math.Min(bytes.Length - headerEnd - 4, contentLength);
        Array.Copy(bytes, headerEnd + 4, body, 0, already);
        var total = already;
        while (total < contentLength)
        {
            var n = await stream.ReadAsync(body.AsMemory(total, (int)contentLength - total), ct).ConfigureAwait(false);
            if (n == 0) return default;
            total += n;
        }
        return (true, method, rawPath, body);
    }

    private static int IndexOfHeaderEnd(MemoryStream buffer)
    {
        var bytes = buffer.GetBuffer();
        var len = (int)buffer.Length;
        if (len < 4) return -1;
        for (var i = 0; i <= len - 4; i++)
        {
            if (bytes[i] == 13 && bytes[i + 1] == 10 && bytes[i + 2] == 13 && bytes[i + 3] == 10)
                return i;
        }
        return -1;
    }

    // ---------- 存储 ----------

    private int HandlePut(string path, byte[] body)
    {
        lock (_gate)
        {
            var parent = path[..path.LastIndexOf('/')].TrimEnd('/') + "/";
            if (path.EndsWith('/') || !_store.ContainsKey(parent)) return 409;
            _store[path] = new Entry(body, false, Guid.NewGuid().ToString("D"), DateTimeOffset.UtcNow);
            return 201;
        }
    }

    private (bool, byte[]?) HandleGet(string path)
    {
        lock (_gate)
            return _store.TryGetValue(path, out var e) && !e.IsCollection ? (true, e.Content) : (false, null);
    }

    private int HandleDelete(string path)
    {
        lock (_gate)
            return _store.Remove(path) || _store.Remove(path.TrimEnd('/') + "/") ? 204 : 404;
    }

    private int HandleMkCol(string path)
    {
        lock (_gate)
        {
            var key = path.EndsWith('/') ? path : path + "/";
            if (_store.ContainsKey(key)) return 405;
            var parent = key[..key.LastIndexOf('/', key.Length - 2)].TrimEnd('/');
            if (parent.Length > "/MiniDrop".Length && !_store.ContainsKey(parent + "/")) return 409;
            _store[key] = new Entry(null, true, Guid.NewGuid().ToString("D"), DateTimeOffset.UtcNow);
            return 201;
        }
    }

    private string? HandlePropfind(string dirPath, string query)
    {
        lock (_gate)
        {
            var dirKey = dirPath.EndsWith('/') ? dirPath : dirPath + "/";
            if (!_store.TryGetValue(dirKey, out var dirEntry) || !dirEntry.IsCollection)
                return null;

            var children = _store
                .Where(kv => kv.Key.StartsWith(dirKey, StringComparison.Ordinal) && kv.Key != dirKey)
                .Select(kv => (Path: kv.Key, Entry: kv.Value))
                .OrderBy(x => x.Path, StringComparer.Ordinal)
                .ToList();

            var pageParam = query.Split('&').FirstOrDefault(p => p.StartsWith("page=", StringComparison.Ordinal));
            var page = pageParam is not null ? int.Parse(pageParam[5..]) : 1;
            var pageSize = ForcePageSize ?? Math.Max(children.Count, 1);

            var slice = children.Skip((page - 1) * pageSize).Take(pageSize).ToList();
            var hasMore = page * pageSize < children.Count;

            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            sb.Append("<D:multistatus xmlns:D=\"DAV:\">");
            foreach (var child in slice)
            {
                var isCol = child.Entry.IsCollection;
                sb.Append("<D:response><D:href>").Append(child.Path).Append("</D:href><D:propstat><D:prop>");
                sb.Append(isCol ? "<D:resourcetype><D:collection/></D:resourcetype>" : "<D:resourcetype/>");
                if (!isCol)
                {
                    sb.Append("<D:getetag>&quot;").Append(child.Entry.Etag).Append("&quot;</D:getetag>");
                    sb.Append("<D:getlastmodified>").Append(child.Entry.Modified.ToString("R")).Append("</D:getlastmodified>");
                    sb.Append("<D:getcontentlength>").Append(child.Entry.Content?.Length ?? 0).Append("</D:getcontentlength>");
                }
                sb.Append("</D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>");
            }
            if (hasMore)
            {
                // 下一页约定：同一目录 + ?page=N；M0 确认真实字段后只改 PropfindPager/此处
                var relative = dirKey.StartsWith("/MiniDrop/", StringComparison.Ordinal)
                    ? dirKey["/MiniDrop/".Length..]
                    : dirKey.TrimStart('/');
                var next = $"{RootUrl}{relative}?page={page + 1}";
                sb.Append("<D:responsedescription><a href=\"").Append(next).Append("\">next</a></D:responsedescription>");
            }
            sb.Append("</D:multistatus>");
            return sb.ToString();
        }
    }

    private static async Task RespondAsync(NetworkStream stream, int status, string body,
        string contentType = "text/plain", long? retryAfter = null, byte[]? rawBytes = null)
    {
        var payload = rawBytes ?? Encoding.UTF8.GetBytes(body);
        var reason = status switch
        {
            200 => "OK", 201 => "Created", 204 => "No Content", 207 => "Multi-Status",
            401 => "Unauthorized", 404 => "Not Found", 405 => "Method Not Allowed",
            409 => "Conflict", 429 => "Too Many Requests", 500 => "Internal Server Error",
            503 => "Service Unavailable", 507 => "Insufficient Storage",
            _ => "Error",
        };
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n");
        sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
        sb.Append("Content-Length: ").Append(payload.Length).Append("\r\n");
        if (retryAfter is { } ra) sb.Append("Retry-After: ").Append(ra).Append("\r\n");
        sb.Append("Connection: close\r\n\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString())).ConfigureAwait(false);
        if (status != 204 && payload.Length > 0)
            await stream.WriteAsync(payload).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        try { _loop.Wait(TimeSpan.FromSeconds(3)); } catch { }
    }
}

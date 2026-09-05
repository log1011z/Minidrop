using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml;
using MiniDrop.Domain;

namespace MiniDrop.WebDav;

/// <summary>
/// WebDAV 适配器。路径段一律百分号编码；PUT 携带 Content-Length。
/// PROPFIND 只请求 resourcetype/getetag/getlastmodified/getcontentlength。
/// </summary>
public sealed class WebDavClient : IDisposable
{
    public const string PropfindBody =
        """
        <?xml version="1.0" encoding="utf-8"?>
        <D:propfind xmlns:D="DAV:">
          <D:prop>
            <D:resourcetype/>
            <D:getetag/>
            <D:getlastmodified/>
            <D:getcontentlength/>
          </D:prop>
        </D:propfind>
        """;

    private readonly WebDavOptions _options;
    private readonly HttpClient _http;
    private readonly PropfindPager _pager = new();

    public RequestGate Gate { get; }

    public WebDavClient(WebDavOptions options, RequestGate? gate = null)
    {
        _options = options;
        Gate = gate ?? new RequestGate();
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            // WebDAV 只连坚果云（国内直连必通），绕过系统代理，避免 VPN/代理接管导致连接失败
            UseProxy = false,
        };
        _http = new HttpClient(handler) { Timeout = options.Timeout };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);
    }

    /// <summary>
    /// 预授权：每个请求直接携带 Basic 凭据。
    /// 若依赖 HttpClient 的 401 挑战重放，PUT 的不可回读内容流会在重放时抛
    /// "The stream was already consumed"（§5.3 边读边算哈希）。
    /// </summary>
    private void AddAuth(HttpRequestMessage req)
    {
        var password = _options.PasswordProvider() ?? "";
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_options.Account}:{password}"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
    }

    public static string NormalizeRootUrl(string url)
    {
        url = url.Trim();
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("只允许 HTTPS 根 URL", nameof(url));
        return url.EndsWith('/') ? url : url + "/";
    }

    public Uri AbsoluteUri(string relativePath)
    {
        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var encoded = string.Join("/", segments.Select(Uri.EscapeDataString));
        return new Uri(_options.RootUrl + encoded + (segments.Length > 0 && relativePath.EndsWith('/') ? "/" : ""));
    }

    // ---------- 操作 ----------

    public Task<DavResult> MkColAsync(string relativePath) => Gate.RunAsync(RequestKind.Mkcol, async () =>
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Parse("MKCOL"), AbsoluteUri(relativePath));
            AddAuth(req);
            using var resp = await _http.SendAsync(req).ConfigureAwait(false);
            return Classify(resp, expectBody: false);
        }
        catch (Exception e) { return FromException(e); }
    });

    /// <summary>确保目录存在；409（父目录缺失）时逐级创建后重试一次。</summary>
    public async Task<DavResult> EnsureDirectoryAsync(string relativeDir)
    {
        var result = await MkColAsync(relativeDir).ConfigureAwait(false);
        if (result.Ok || result.Status == DavStatus.NotFound)
            return result;
        if (result.HttpStatusCode == 409)
        {
            var segments = relativeDir.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var acc = "";
            foreach (var seg in segments)
            {
                acc = acc.Length == 0 ? seg : acc + "/" + seg;
                await MkColAsync(acc).ConfigureAwait(false);
            }
            result = await MkColAsync(relativeDir).ConfigureAwait(false);
        }
        return result;
    }

    public Task<DavResult> PutAsync(string relativePath, byte[] content, string contentType) =>
        PutAsync(relativePath, new MemoryStream(content), content.Length, contentType, progress: null, CancellationToken.None);

    public async Task<DavResult> PutAsync(string relativePath, Stream content, long length, string contentType,
        IProgress<long>? progress, CancellationToken ct) => await Gate.RunAsync(RequestKind.Put, async () =>
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Put, AbsoluteUri(relativePath));
            AddAuth(req);
            var httpContent = new StreamContent(content, 64 * 1024);
            httpContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            httpContent.Headers.ContentLength = length;
            req.Content = httpContent;
            if (progress is not null)
                req.Options.Set(new HttpRequestOptionsKey<IProgress<long>>("progress"), progress);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            return Classify(resp, expectBody: false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) { return FromException(e); }
    }, ct).ConfigureAwait(false);

    /// <summary>读取已知对象，上限 maxBytes+1：超出即停止并拒绝。</summary>
    public async Task<(DavResult Result, byte[]? Content)> GetBytesAsync(string relativePath, long maxBytes) =>
        await Gate.RunAsync(RequestKind.GetMeta, async () =>
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, AbsoluteUri(relativePath));
            AddAuth(req);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            var classified = Classify(resp, expectBody: true);
            if (!classified.Ok)
                return (classified, null);
            var length = resp.Content.Headers.ContentLength;
            if (length.HasValue && length.Value > maxBytes)
                return (DavResult.NewOk((int)resp.StatusCode), null); // 超限：结果为 Ok 但内容 null，由调用方判 TOO_LARGE

            var buffer = new MemoryStream();
            await using (var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
            {
                var chunk = new byte[64 * 1024];
                long total = 0;
                int n;
                while ((n = await stream.ReadAsync(chunk).ConfigureAwait(false)) > 0)
                {
                    total += n;
                    if (total > maxBytes)
                        return (DavResult.NewOk((int)resp.StatusCode), null);
                    buffer.Write(chunk, 0, n);
                }
            }
            return (DavResult.NewOk((int)resp.StatusCode), buffer.ToArray());
        }
        catch (Exception e) { return (FromException(e), null); }
    }).ConfigureAwait(false);

    /// <summary>流式下载到目标流（下载 .part 用），带进度与取消。</summary>
    public async Task<DavResult> GetStreamAsync(string relativePath, Stream destination,
        IProgress<long>? progress, CancellationToken ct) => await Gate.RunAsync(RequestKind.GetFile, async () =>
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, AbsoluteUri(relativePath));
            AddAuth(req);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var classified = Classify(resp, expectBody: true);
            if (!classified.Ok)
                return classified;

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var chunk = new byte[64 * 1024];
            long total = 0;
            int n;
            while ((n = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(chunk.AsMemory(0, n), ct).ConfigureAwait(false);
                total += n;
                progress?.Report(total);
            }
            return DavResult.NewOk((int)resp.StatusCode);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) { return FromException(e); }
    }, ct).ConfigureAwait(false);

    public Task<DavResult> DeleteAsync(string relativePath) => Gate.RunAsync(RequestKind.Delete, async () =>
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Delete, AbsoluteUri(relativePath));
            AddAuth(req);
            using var resp = await _http.SendAsync(req).ConfigureAwait(false);
            var result = Classify(resp, expectBody: false);
            return result.Status == DavStatus.NotFound ? DavResult.NewOk((int)resp.StatusCode) : result;
        }
        catch (Exception e) { return FromException(e); }
    });

    /// <summary>分页 PROPFIND Depth:1；目录不存在返回空集合；分页聚合后返回。</summary>
    public async Task<(DavResult Result, IReadOnlyList<DavItem> Items)> PropfindDirAsync(string relativeDir)
    {
        var items = new List<DavItem>();
        string? next = null;
        var first = true;
        var pages = 0;
        do
        {
            var (result, page) = await PropfindPageAsync(relativeDir, next, first).ConfigureAwait(false);
            if (!result.Ok)
                return (result, Array.Empty<DavItem>());
            first = false;
            pages++;
            items.AddRange(page.Items);
            next = _pager.ExtractNextLink(page.NextLinkDescription);
            if (next is null && page.Items.Count >= PropfindPager.ServerPageSize && !_pager.WarnedIncomplete)
            {
                _pager.WarnedIncomplete = true;
            }
        } while (next is not null && pages < 64);

        return (DavResult.NewOk(), items);
    }

    private async Task<(DavResult, MultistatusParser.Page)> PropfindPageAsync(string relativeDir, string? nextUrl, bool first)
    {
        return await Gate.RunAsync(RequestKind.Propfind, async () =>
        {
            try
            {
                Uri uri = first ? AbsoluteUri(relativeDir + "/") : new Uri(nextUrl!, UriKind.Absolute);
                using var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), uri);
                req.Headers.TryAddWithoutValidation("Depth", "1");
                AddAuth(req);
                req.Content = new StringContent(PropfindBody, Encoding.UTF8, "application/xml");
                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                var classified = Classify(resp, expectBody: true);
                if (classified.Status == DavStatus.NotFound)
                    return (classified, new MultistatusParser.Page([], null));
                if (!classified.Ok)
                    return (classified, new MultistatusParser.Page([], null));
                await using var body = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
                return (classified, MultistatusParser.Parse(body));
            }
            catch (XmlException)
            {
                return (DavResult.NewOk(null) with { Status = DavStatus.ProtocolError }, new MultistatusParser.Page([], null));
            }
            catch (Exception e)
            {
                return (FromException(e), new MultistatusParser.Page([], null));
            }
        }).ConfigureAwait(false);
    }

    // ---------- 分类 ----------

    private static DavResult Classify(HttpResponseMessage resp, bool expectBody)
    {
        var code = (int)resp.StatusCode;
        if (code is 200 or 201 or 204 or 207)
            return DavResult.NewOk(code);
        if (code == 404)
            return new DavResult(DavStatus.NotFound, code);
        if (code == 405)
            return DavResult.NewExists(code); // MKCOL 已存在
        if (code is 401 or 403)
            return new DavResult(DavStatus.AuthError, code);
        if (code == 507)
            return new DavResult(DavStatus.QuotaError, code);
        if (code == 429 || code == 503)
        {
            TimeSpan? retryAfter = null;
            if (resp.Headers.RetryAfter is { } ra)
            {
                retryAfter = ra.Delta ?? (ra.Date is { } d ? d - DateTimeOffset.UtcNow : null);
                if (retryAfter < TimeSpan.Zero) retryAfter = TimeSpan.Zero;
            }
            return new DavResult(DavStatus.RateLimited, code, retryAfter);
        }
        if (code >= 500)
            return new DavResult(DavStatus.ServerError, code);
        return new DavResult(DavStatus.ClientError, code);
    }

    private static DavResult FromException(Exception e)
    {
        // 递归取最内层异常：真实原因（DNS/代理/连接重置/TLS）在内层
        var root = e;
        while (root.InnerException is { } inner)
            root = inner;
        var kind = e is OperationCanceledException
            ? DavStatus.NetworkError
            : e is HttpRequestException ? DavStatus.NetworkError : DavStatus.ProtocolError;
        var detail = e is OperationCanceledException
            ? "timeout/cancelled"
            : $"{root.GetType().Name}: {root.Message}";
        return new DavResult(kind, null, Detail: detail);
    }

    public void Dispose() => _http.Dispose();
}

public enum RequestKind
{
    Propfind, GetMeta, GetFile, Put, Mkcol, Delete,
}

/// <summary>
/// 进程级请求闸门：元数据 GET 最大并发 2，上传串行；按操作类型计数（诊断用）。
/// </summary>
public sealed class RequestGate
{
    private readonly SemaphoreSlim _metadata = new(2, 2);
    private readonly SemaphoreSlim _upload = new(1, 1);
    private readonly ConcurrentDictionary<string, long> _counts = new();

    public async Task<T> RunAsync<T>(RequestKind kind, Func<Task<T>> op, CancellationToken ct = default)
    {
        var gate = kind == RequestKind.Put ? _upload : _metadata;
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _counts.AddOrUpdate(kind.ToString(), 1, (_, v) => v + 1);
            return await op().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public IReadOnlyDictionary<string, long> SnapshotCounts() => new Dictionary<string, long>(_counts);

    public void ResetCounts() => _counts.Clear();
}

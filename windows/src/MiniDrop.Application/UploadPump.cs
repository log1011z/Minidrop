using System.Security.Cryptography;
using MiniDrop.Domain;
using MiniDrop.Storage;
using MiniDrop.WebDav;

namespace MiniDrop.Application;

/// <summary>
/// 上传泵（§5.2–5.4）：同一进程最多一个；SQLite 是唯一队列真相源。
/// 顺序：claim → ensure 月份目录 → 逐文件上传（跳过 uploaded）→ 结构化行序列化 JSON → PUT 消息 JSON → 删除 job。
/// </summary>
public sealed class UploadPump(
    Database db,
    Func<WebDavClient> dav,
    Func<AppOptions> options,
    TransferRegistry transfers,
    IDiagLog log) : IUploadTrigger
{
    private readonly SemaphoreSlim _single = new(1, 1);
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly MetaDao _meta = new(db);
    private readonly MessageDao _messages = new(db);
    private readonly FileDao _files = new(db);
    private readonly JobDao _jobs = new(db);

    public void Wake() { _signal.Release(); WakeRequested?.Invoke(); }

    /// <summary>UI/宿主可挂接该事件做进度刷新等副作用。</summary>
    public event Action? WakeRequested;

    /// <summary>启动宿主后台循环：唯一消费者，Wake 唤醒，退出前先清空队列。</summary>
    public void StartBackgroundLoop(CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await DrainAsync(ct).ConfigureAwait(false);
                while (!ct.IsCancellationRequested)
                {
                    await _signal.WaitAsync(ct).ConfigureAwait(false);
                    await DrainAsync(ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception)
            {
                log.Error("pump", "background loop crashed");
            }
        }, ct);
    }

    /// <summary>消费所有有执行资格的 job；测试与后台循环共用。返回处理的任务数。</summary>
    public async Task<int> DrainAsync(CancellationToken ct)
    {
        var processed = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            await _single.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var job = _jobs.ClaimNext(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                if (job is null)
                    return processed;
                processed++;
                await ProcessClaimedAsync(job.MessageId, ct).ConfigureAwait(false);
            }
            finally
            {
                _single.Release();
            }
        }
    }

    // ---------- 单个任务 ----------

    private async Task ProcessClaimedAsync(string messageId, CancellationToken ct)
    {
        var msg = _messages.Get(messageId);
        if (msg is null)
        {
            _jobs.Delete(messageId);
            return;
        }
        var files = _files.GetByMessage(messageId);
        var job = _jobs.Get(messageId);
        if (job is null)
            return;

        var regCt = transfers.RegisterUpload(messageId);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, regCt);
        try
        {
            await UploadJobAsync(msg, files, job, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (regCt.IsCancellationRequested)
        {
            // 用户删除消息：job 可能已被删除流程处理；这里兜底复位
            _files.SetStateBulk(null!, messageId, FileStates.Uploading, FileStates.Pending);
            _jobs.SetState(messageId, JobState.Queued, job.Attempts, null);
        }
        catch (OperationCanceledException)
        {
            // 应用停止：保持 uploading 交给启动恢复，或直接复位为 queued
            _files.SetStateBulk(null!, messageId, FileStates.Uploading, FileStates.Pending);
            _jobs.SetState(messageId, JobState.Queued, job.Attempts, null);
        }
        catch (Exception e)
        {
            log.Error("pump", $"unexpected {e.GetType().Name}");
            _jobs.SetState(messageId, JobState.Failed, job.Attempts + 1, null,
                errorCode: ErrorCodes.Protocol, errorMessage: "内部错误");
        }
        finally
        {
            transfers.CompleteUpload(messageId);
        }
    }

    private async Task UploadJobAsync(MessageRow msg, IReadOnlyList<FileRow> files, JobRow job, CancellationToken ct)
    {
        var client = dav();

        // ensure 月份目录（惰性 MKCOL；已存在/已创建都放行）
        var mk = await client.EnsureDirectoryAsync(RemotePaths.ItemsMonthDir(msg.RemoteMonth)).ConfigureAwait(false);
        if (!mk.Ok)
        {
            if (ClassifyTransient(mk, out var code0, out var after0))
                ToRetryWait(job, code0!, after0);
            else
                FailJob(job, code0!, "月份目录创建失败", null);
            return;
        }

        long completed = job.BytesDone;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            if (file.State == FileStates.Uploaded)
                continue;

            // 源文件核对（上传前）
            if (file.SourcePath is null || !File.Exists(file.SourcePath))
            {
                FailJob(job, ErrorCodes.SourceMissing, "原文件不存在或已移动", null);
                return;
            }
            var fi = new FileInfo(file.SourcePath);
            if (fi.Length != file.Size || new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds() != file.SourceModifiedAt)
            {
                FailJob(job, ErrorCodes.SourceChanged, "文件在发送后发生变化，请重新发送", null);
                return;
            }

            _files.SetState(file.FileId, FileStates.Uploading);
            var throttled = new ProgressThrottle(job.MessageId, completed, _jobs);
            await using var stream = fi.Open(FileMode.Open, FileAccess.Read, FileShare.Read);
            using var hashing = new HashingReadStream(stream, throttled);
            var put = await client.PutAsync(RemotePaths.FilePath(file.FileId), hashing, file.Size,
                "application/octet-stream", throttled, ct).ConfigureAwait(false);

            if (put.Ok)
            {
                var sha = Convert.ToHexString(hashing.FlushHash()).ToLowerInvariant();
                _files.SetUploaded(file.FileId, sha);
                completed += file.Size;
                _jobs.SetProgress(job.MessageId, completed);
            }
            else
            {
                _files.SetState(file.FileId, FileStates.Pending);
                if (ClassifyTransient(put, out var code, out var retryAfter))
                {
                    ToRetryWait(job, code!, retryAfter);
                    return;
                }
                FailJob(job, code!, Describe(put), null);
                return;
            }
        }

        // 发布前再核对源文件（前后核对，Windows）
        foreach (var file in files)
        {
            if (file.SourcePath is null || !File.Exists(file.SourcePath))
            {
                FailJob(job, ErrorCodes.SourceChanged, "文件在发送后发生变化，请重新发送", null);
                return;
            }
            var fi = new FileInfo(file.SourcePath);
            if (fi.Length != file.Size || new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds() != file.SourceModifiedAt)
            {
                FailJob(job, ErrorCodes.SourceChanged, "文件在发送后发生变化，请重新发送", null);
                return;
            }
        }

        // 结构化行 → JSON → PUT（commit）
        var draft = new MessageJson.Draft(
            msg.Id,
            msg.DeviceId,
            msg.DeviceName,
            DateTimeOffset.Parse(msg.CreatedAt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal),
            msg.Text,
            files.Select(f => new MessageJson.DraftFile(f.FileId, f.Name, f.Size, f.Mime, f.Sha256)).ToList());
        var json = MessageJson.Serialize(draft);

        var putJson = await client.PutAsync(RemotePaths.MessagePath(msg.RemoteMonth, msg.Id), json, "application/json").ConfigureAwait(false);
        if (putJson.Ok)
        {
            _jobs.Delete(job.MessageId);
            log.Info("pump", "published message");
            return;
        }

        if (ClassifyTransient(putJson, out var code2, out var retryAfter2))
        {
            ToRetryWait(job, code2!, retryAfter2);
            return;
        }
        FailJob(job, code2!, Describe(putJson), null);
    }

    // ---------- 状态转换 ----------

    private static bool ClassifyTransient(DavResult result, out string? errorCode, out TimeSpan? retryAfter)
    {
        errorCode = null;
        retryAfter = null;
        switch (result.Status)
        {
            case DavStatus.NetworkError:
                errorCode = ErrorCodes.Network;
                retryAfter = null;
                return true;
            case DavStatus.RateLimited:
                errorCode = ErrorCodes.RateLimit;
                retryAfter = result.RetryAfter;
                return true;
            case DavStatus.ServerError:
                errorCode = ErrorCodes.Server;
                return true;
            case DavStatus.QuotaError:
                errorCode = ErrorCodes.Quota;
                return false;
            case DavStatus.AuthError:
                errorCode = ErrorCodes.Auth;
                return false;
            default:
                errorCode = ErrorCodes.Protocol;
                return false;
        }
    }

    private void ToRetryWait(JobRow job, string code, TimeSpan? retryAfter)
    {
        var attempts = job.Attempts + 1;
        if (attempts >= ErrorCodes.MaxTransientAttempts)
        {
            FailJob(job, code, "连续失败达到上限", null);
            return;
        }
        var local = ErrorCodes.Backoff[Math.Min(attempts - 1, ErrorCodes.Backoff.Length - 1)];
        var delay = retryAfter is { } ra && ra > local ? ra : local;
        var next = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (long)delay.TotalMilliseconds;
        _jobs.SetState(job.MessageId, JobState.RetryWait, attempts, next, errorCode: code);
        log.Warn("pump", $"retry_wait code={code} attempts={attempts}");
    }

    private void FailJob(JobRow job, string code, string detail, TimeSpan? _)
    {
        _jobs.SetState(job.MessageId, JobState.Failed, job.Attempts + 1, null, errorCode: code, errorMessage: detail);
        log.Error("pump", $"failed code={code}");
    }

    private static string Describe(DavResult r) => r.Status switch
    {
        DavStatus.AuthError => "账号或应用密码不可用",
        DavStatus.QuotaError => "账户空间或流量不足",
        _ => "上传失败，请稍后重试",
    };
}

// ---------- 流工具 ----------

/// <summary>读取源文件时同时计算 SHA-256 并回报进度（避免二次读盘）。</summary>
public sealed class HashingReadStream : Stream
{
    private readonly Stream _inner;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly IProgress<long>? _progress;
    private long _position;

    public HashingReadStream(Stream inner, IProgress<long>? progress)
    {
        _inner = inner;
        _progress = progress;
    }

    public byte[] FlushHash() => _hash.GetHashAndReset();

    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = _inner.Read(buffer, offset, count);
        if (n > 0)
        {
            _hash.AppendData(buffer, offset, n);
            _position += n;
            _progress?.Report(_position);
        }
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var n = await _inner.ReadAsync(buffer, ct).ConfigureAwait(false);
        if (n > 0)
        {
            _hash.AppendData(buffer.Span[..n]);
            _position += n;
            _progress?.Report(_position);
        }
        return n;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;
    public override long Position { get => _position; set => throw new NotSupportedException(); }

    // 注意：不 Dispose _hash —— HttpClient 发送完成后会 Dispose 请求内容流（即本流），
    // 而 FlushHash() 在 PUT 返回后才调用，提前释放会抛 ObjectDisposedException。
}

/// <summary>进度入库节流：每 250ms 或每 1 MiB 一次，满足任一即可。</summary>
public sealed class ProgressThrottle : IProgress<long>
{
    private const long Chunk = 1024 * 1024;
    private readonly JobDao _jobs;
    private readonly string _messageId;
    private readonly long _baseDone;
    private long _lastReportedAt;
    private long _lastReportedBytes;
    private long _current;

    public ProgressThrottle(string messageId, long baseDone, JobDao jobs)
    {
        _messageId = messageId;
        _baseDone = baseDone;
        _jobs = jobs;
        _lastReportedAt = Environment.TickCount64;
    }

    public void Report(long streamPosition)
    {
        Interlocked.Exchange(ref _current, _baseDone + streamPosition);
        var now = Environment.TickCount64;
        var bytesMoved = Interlocked.Read(ref _current) - _lastReportedBytes;
        if (now - _lastReportedAt >= 250 || bytesMoved >= Chunk)
        {
            _lastReportedAt = now;
            _lastReportedBytes = Interlocked.Read(ref _current);
            _jobs.SetProgress(_messageId, _lastReportedBytes);
        }
    }
}

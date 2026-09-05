using MiniDrop.Domain;
using MiniDrop.Storage;
using MiniDrop.WebDav;

namespace MiniDrop.Application;

public sealed record MaintainOutcome(int Processed, bool ScanError);

/// <summary>
/// 远端维护（§12）：无后台定时器；设置页手动触发，或刷新完成后 > 7 天受限执行。
/// 只扫 90 天边界相关及更老月份；单次最多 100 条；与刷新互斥（复用 SyncMutex）。
/// </summary>
public sealed class MaintenanceService(
    Database db,
    Func<WebDavClient> dav,
    Func<AppOptions> options,
    SyncMutex mutex,
    SyncCoordinator coordinator,
    IDiagLog log)
{
    private const int MaxProcessPerRun = 100;
    private const int MonthsPerRun = 12; // 从 90 天边界月份向深回溯的月数上限（§12.3 "及更老"）

    private readonly MetaDao _meta = new(db);
    private readonly RejectedDao _rejected = new(db);

    /// <summary>刷新完成后的受限维护（已在刷新互斥作用域内调用，不再取锁）。</summary>
    public async Task MaybeRunAfterRefreshAsync(CancellationToken ct)
    {
        var last = _meta.Get(MetaDao.LastRemoteMaintenanceAt);
        if (last is not null &&
            DateTimeOffset.TryParse(last, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var t) &&
            DateTimeOffset.UtcNow - t < TimeSpan.FromDays(7))
        {
            return;
        }
        await RunCoreAsync(ct).ConfigureAwait(false);
    }

    /// <summary>设置页手动触发（与刷新互斥）。</summary>
    public Task<MaintainOutcome> MaintainAsync(CancellationToken ct) =>
        mutex.RunAsync("maintenance", async ct => await RunCoreAsync(ct).ConfigureAwait(false), ct);

    /// <summary>核心算法：调用方必须已持有互斥锁。</summary>
    private async Task<MaintainOutcome> RunCoreAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var cutoffMonth = UlidClock.ExpiryMonth(now);
        var budget = MaxProcessPerRun;
        var processed = 0;
        var scanError = false;
        var client = dav();

        var month = cutoffMonth;
        for (var i = 0; i < MonthsPerRun && budget > 0; i++)
        {
            MonthScan scan;
            try
            {
                scan = await coordinator.ScanMonthAsync(month, ct).ConfigureAwait(false);
            }
            catch (SyncException e)
            {
                log.Warn("maintain", $"scan failed month={month} code={e.ErrorCode}");
                scanError = true;
                break;
            }

            var tombIds = scan.TombstoneIds;

            // 1) R∩T 收敛
            foreach (var item in scan.Items.Where(x => tombIds.Contains(x.Id)))
            {
                if (budget <= 0) break;
                ct.ThrowIfCancellationRequested();
                if (await coordinator.ConvergeAsync(month, item.Id, ct).ConfigureAwait(false))
                    processed++;
                budget--;
            }

            // 2) R−T 且 ULID 已过期：GET item → 删文件 → 删 item；损坏 JSON 只删 item（可能孤儿）
            foreach (var item in scan.Items.Where(x => !tombIds.Contains(x.Id)))
            {
                if (budget <= 0) break;
                ct.ThrowIfCancellationRequested();
                budget--;
                var itemPath = RemotePaths.MessagePath(month, item.Id);
                var (res, content) = await client.GetBytesAsync(itemPath, Limits.MaxJsonBytes).ConfigureAwait(false);
                if (res.Status == DavStatus.NotFound)
                {
                    // item 已消失：过期墓碑可删除
                    await client.DeleteAsync(RemotePaths.TombstonePath(month, item.Id)).ConfigureAwait(false);
                    processed++;
                    continue;
                }
                if (!res.Ok || content is null)
                    continue;

                var parsed = MessageJson.Parse(content, item.Id, options().MaxFileBytes);
                if (parsed.Ok && parsed.Message is not null)
                {
                    var allDeleted = true;
                    foreach (var f in parsed.Message.Files)
                    {
                        var del = await client.DeleteAsync(RemotePaths.FilePath(f.Id)).ConfigureAwait(false);
                        if (!del.Ok) allDeleted = false;
                    }
                    if (allDeleted)
                    {
                        await client.DeleteAsync(itemPath).ConfigureAwait(false);
                        processed++;
                    }
                }
                else
                {
                    // 损坏 JSON：无法得知文件引用，只删过期 item
                    await client.DeleteAsync(itemPath).ConfigureAwait(false);
                    processed++;
                    log.Warn("maintain", "possible orphan file (corrupt item json)");
                }
            }

            // 3) 孤儿墓碑（R 中无对应 item）：item 不存在才可删
            foreach (var t in scan.Tombstones.Where(x => !scan.Items.Any(it => it.Id == x.Id)))
            {
                if (budget <= 0) break;
                ct.ThrowIfCancellationRequested();
                budget--;
                var (res, _) = await client.GetBytesAsync(RemotePaths.MessagePath(month, t.Id), Limits.MaxJsonBytes).ConfigureAwait(false);
                if (res.Status == DavStatus.NotFound)
                {
                    await client.DeleteAsync(RemotePaths.TombstonePath(month, t.Id)).ConfigureAwait(false);
                    processed++;
                }
            }

            month = UlidClock.PreviousMonth(month);
        }

        if (!scanError)
            _meta.Set(MetaDao.LastRemoteMaintenanceAt, now.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"));
        return new MaintainOutcome(processed, scanError);
    }
}

/// <summary>删除整条消息（§7.1）：墓碑 commit → 本地删 → 文件 → 最后 item。</summary>
public sealed class DeleteService(
    Database db,
    Func<WebDavClient> dav,
    Func<AppOptions> options,
    SyncCoordinator coordinator,
    TransferRegistry transfers,
    IDiagLog log)
{
    private readonly MessageDao _messages = new(db);
    private readonly FileDao _files = new(db);
    private readonly JobDao _jobs = new(db);

    public sealed record DeleteResult(bool Ok, string? ErrorText);

    public async Task<DeleteResult> DeleteAsync(string messageId, CancellationToken ct)
    {
        var msg = _messages.Get(messageId);
        if (msg is null)
            return new DeleteResult(true, null);

        var opt = options();

        // 1) 取消该消息正在进行的上传/下载
        transfers.CancelMessage(messageId);
        foreach (var f in _files.GetByMessage(messageId))
            transfers.CancelDownload(f.FileId);

        // 2) 未发布的消息（上传失败/等待中/上传中）：远端没有消息对象，无需墓碑，本地删除即可。
        //    半截文件对象按设计 §3.5 接受为孤儿。
        var job = _jobs.Get(messageId);
        if (job is not null)
        {
            if (job.State == JobState.Uploading)
            {
                // 等待泵释放 claim（最多 2 秒）
                for (var i = 0; i < 20; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var current = _jobs.Get(messageId);
                    if (current is null || current.State != JobState.Uploading)
                        break;
                    await Task.Delay(100, ct).ConfigureAwait(false);
                }
            }
            coordinator.DeleteLocalMessage(messageId);
            log.Info("delete", "deleted unpublished message locally");
            return new DeleteResult(true, null);
        }

        // 3) PUT 墓碑 = 删除 commit（墓碑月份目录惰性创建）
        var mkTomb = await dav().EnsureDirectoryAsync(RemotePaths.TombstonesMonthDir(msg.RemoteMonth)).ConfigureAwait(false);
        if (!mkTomb.Ok)
        {
            log.Warn("delete", $"tombstone dir failed status={mkTomb.Status}");
            return new DeleteResult(false, "删除失败，请检查网络");
        }
        var tombstone = MessageJson.SerializeTombstone(
            msg.Id, DateTimeOffset.UtcNow, opt.DeviceId.ToString("D"));
        var put = await dav().PutAsync(RemotePaths.TombstonePath(msg.RemoteMonth, msg.Id),
            tombstone, "application/json").ConfigureAwait(false);
        if (!put.Ok)
        {
            log.Warn("delete", $"tombstone failed status={put.Status}");
            return new DeleteResult(false, "删除失败，请检查网络");
        }

        var fileRows = _files.GetByMessage(messageId);
        coordinator.DeleteLocalMessage(messageId);

        // 4) DELETE 文件（404 视为成功）
        var client = dav();
        var allFilesOk = true;
        foreach (var f in fileRows)
        {
            ct.ThrowIfCancellationRequested();
            var del = await client.DeleteAsync(RemotePaths.FilePath(f.FileId)).ConfigureAwait(false);
            if (!del.Ok)
                allFilesOk = false;
        }

        // 5) 全部文件成功（或 404）后最后 DELETE item；失败留给下次扫描收敛
        if (allFilesOk)
            await client.DeleteAsync(RemotePaths.MessagePath(msg.RemoteMonth, msg.Id)).ConfigureAwait(false);

        log.Info("delete", "deleted message");
        return new DeleteResult(true, null);
    }
}

/// <summary>文件下载与打开（§8）：用户动作触发；.part + SHA-256 校验 + 原子改名。</summary>
public sealed class DownloadService(
    Database db,
    Func<WebDavClient> dav,
    Func<AppOptions> options,
    TransferRegistry transfers,
    IDiagLog log)
{
    private readonly FileDao _files = new(db);

    public enum DownloadStep { AlreadyCached, Completed, Failed }

    public async Task<(DownloadStep Step, string? ErrorText)> DownloadAsync(string fileId, CancellationToken ct)
    {
        var file = _files.Get(fileId);
        if (file is null)
            return (DownloadStep.Failed, "文件不存在");

        if (file.State == FileStates.Cached && file.CachePath is not null && File.Exists(file.CachePath))
            return (DownloadStep.AlreadyCached, null);

        var dir = options().DownloadDir;
        if (string.IsNullOrEmpty(dir))
            return (DownloadStep.Failed, "未设置下载目录");
        Directory.CreateDirectory(dir);

        var partPath = Path.Combine(dir, file.FileId + ".part");
        transfers.CancelDownload(fileId);
        var regCt = transfers.RegisterDownload(fileId);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, regCt);
        try
        {
            _files.SetState(fileId, FileStates.Downloading);
            await using var part = File.Create(partPath);
            var result = await dav().GetStreamAsync(RemotePaths.FilePath(file.FileId), part, null, linked.Token).ConfigureAwait(false);
            await part.FlushAsync(linked.Token).ConfigureAwait(false);
            part.Close();

            if (!result.Ok)
            {
                Cleanup(partPath);
                _files.SetState(fileId, FileStates.Failed);
                return (DownloadStep.Failed, "下载失败，请重试");
            }

            // 校验 size 与 sha256
            var fi = new FileInfo(partPath);
            if (fi.Length != file.Size)
            {
                Cleanup(partPath);
                _files.SetState(fileId, FileStates.Failed);
                return (DownloadStep.Failed, "文件校验失败，请重试");
            }
            if (file.Sha256 is not null)
            {
                var actual = Convert.ToHexString(await ComputeShaAsync(partPath, linked.Token).ConfigureAwait(false)).ToLowerInvariant();
                if (actual != file.Sha256)
                {
                    Cleanup(partPath);
                    _files.SetState(fileId, FileStates.Failed);
                    return (DownloadStep.Failed, "文件校验失败，请重试");
                }
            }

            var finalPath = Path.Combine(dir, BuildCacheName(file.FileId, file.Name));
            File.Move(partPath, finalPath, overwrite: false);
            _files.SetCachePath(fileId, finalPath, FileStates.Cached);
            log.Info("download", "cached");
            return (DownloadStep.Completed, null);
        }
        catch (OperationCanceledException)
        {
            Cleanup(partPath);
            _files.SetState(fileId, FileStates.Remote);
            return (DownloadStep.Failed, null);
        }
        catch (Exception)
        {
            Cleanup(partPath);
            _files.SetState(fileId, FileStates.Failed);
            return (DownloadStep.Failed, "下载失败，请重试");
        }
        finally
        {
            transfers.CompleteDownload(fileId);
        }
    }

    /// <summary>cached 但文件丢失 → 回到 remote。</summary>
    public void EnsureCacheConsistency(FileRow file)
    {
        if (file.State == FileStates.Cached && (file.CachePath is null || !File.Exists(file.CachePath)))
            _files.SetState(file.FileId, FileStates.Remote);
    }

    /// <summary>缓存名：&lt;uuid&gt;_&lt;安全扩展名&gt;；展示名始终来自数据库 name。</summary>
    public static string BuildCacheName(string fileId, string displayName)
    {
        var ext = Path.GetExtension(displayName);
        var safe = ext.Length is > 0 and <= 11 && ext[1..].All(c => char.IsAsciiLetterOrDigit(c)) ? ext : ".bin";
        return fileId + "_" + safe;
    }

    private static void Cleanup(string partPath)
    {
        try { if (File.Exists(partPath)) File.Delete(partPath); } catch { }
    }

    private static async Task<byte[]> ComputeShaAsync(string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        return await System.Security.Cryptography.SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
    }
}

/// <summary>启动恢复与本地清理（§4.3 末、§12.1）：零网络。</summary>
public sealed class StartupRecovery(Database db, Func<AppOptions> options, IDiagLog log)
{
    private readonly JobDao _jobs = new(db);
    private readonly FileDao _files = new(db);
    private readonly MessageDao _messages = new(db);

    /// <summary>uploading → queued；文件 uploading → pending。</summary>
    public int Recover()
    {
        var n = _jobs.RecoverUploading();
        foreach (var id in AllMessageIds())
        {
            foreach (var f in _files.GetByMessage(id))
            {
                if (f.State == FileStates.Uploading)
                    _files.SetState(f.FileId, FileStates.Pending);
            }
        }
        return n;
    }

    /// <summary>本地清理：90 天生命周期（按 ULID 时间戳）、孤儿 .part（48h）。</summary>
    public int LocalCleanup(DateTimeOffset now)
    {
        var removed = 0;
        var cutoffMonth = UlidClock.ExpiryMonth(now);
        var cutoffMs = now.ToUnixTimeMilliseconds() - UlidClock.RetentionDays * 24L * 3600 * 1000;

        foreach (var id in _messages.IdsInMonthsUpTo(cutoffMonth))
        {
            if (Ulid.TryGetTimestampMs(id, out var ts) && ts < cutoffMs)
            {
                var files = _files.GetByMessage(id);
                db.Write(tx => _messages.DeleteCascade(tx, id));
                foreach (var f in files)
                {
                    if (f.Dir == Direction.In && f.CachePath is not null)
                        TryDelete(f.CachePath);
                    // 不删除 Windows source_path
                }
                removed++;
            }
        }

        var dir = options().DownloadDir;
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
        {
            foreach (var part in Directory.EnumerateFiles(dir, "*.part"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(part) < now.UtcDateTime - TimeSpan.FromHours(48))
                        File.Delete(part);
                }
                catch { }
            }
        }
        return removed;
    }

    private IReadOnlyList<string> AllMessageIds()
    {
        var list = new List<string>();
        for (var offset = 0; ; offset += 500)
        {
            var page = _messages.TimelinePage(500, offset);
            if (page.Count == 0) break;
            list.AddRange(page.Select(p => p.Message.Id));
            if (page.Count < 500) break;
        }
        return list;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}

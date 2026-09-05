using System.Security.Cryptography;
using System.Text;
using MiniDrop.Domain;
using MiniDrop.Storage;
using MiniDrop.WebDav;

namespace MiniDrop.Application;

public sealed record RefreshOutcome(int Added, int Failed, bool ScanError)
{
    public bool Success => !ScanError && Failed == 0;
}

public sealed record LoadOlderOutcome(int Added, int Failed, bool NoMore, bool ScanError);

/// <summary>月份扫描中的远端条目（文件名已校验为合法 ULID）。</summary>
public sealed record RemoteRef(string Id, string Signature);

public sealed record MonthScan(IReadOnlyList<RemoteRef> Items, IReadOnlyList<RemoteRef> Tombstones)
{
    public HashSet<string> TombstoneIds => new(Tombstones.Select(t => t.Id));
}

/// <summary>刷新 / 加载更早 / 远端维护三者互斥。</summary>
public sealed class SyncMutex
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public volatile string? CurrentOp;

    public async Task<T> RunAsync<T>(string op, Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        CurrentOp = op;
        try
        {
            return await action(ct).ConfigureAwait(false);
        }
        finally
        {
            CurrentOp = null;
            _gate.Release();
        }
    }

    public bool IsBusy => _gate.CurrentCount == 0;
}

/// <summary>
/// 手动同步（§6）：scanMonth 原语、最近 20 条刷新、历史游标与加载更早、拒绝与 quarantine。
/// 无任何自动触发：只由用户刷新 / 加载更早 / 维护入口调用。
/// </summary>
public sealed class SyncCoordinator(
    Database db,
    Func<WebDavClient> dav,
    Func<AppOptions> options,
    SyncMutex mutex,
    IDiagLog log)
{
    public const int WindowSize = 20;
    public const int MaxMonthsBack = 3;
    public const int ConvergeBatch = 20;

    /// <summary>刷新完成后可触发的维护（由组装层注入，测试可不设）。</summary>
    public MaintenanceService? Maintenance { get; set; }

    private readonly MetaDao _meta = new(db);
    private readonly MessageDao _messages = new(db);
    private readonly FileDao _files = new(db);
    private readonly RejectedDao _rejected = new(db);
    private readonly SyncMonthDao _syncMonths = new(db);

    public SyncMutex Mutex => mutex;

    public Task<RefreshOutcome> RefreshAsync(CancellationToken ct) =>
        mutex.RunAsync("refresh", async ct =>
        {
            var now = DateTimeOffset.UtcNow;
            var initialized = _meta.Get(MetaDao.HistoryInitialized) == "1";
            var localCount = _messages.Count();
            var localMonths = initialized && localCount >= WindowSize
                ? new HashSet<string>(_messages.MonthsOfNewest(WindowSize))
                : null;

            var selected = new List<RemoteRef>();
            var scanError = false;
            var month = UlidClock.CurrentUtcMonth(now);
            var scannedMonths = new List<string>();

            for (var back = 0; back < MaxMonthsBack && selected.Count < WindowSize; back++)
            {
                MonthScan scan;
                try
                {
                    scan = await ScanMonthAsync(month, ct).ConfigureAwait(false);
                    scannedMonths.Add(month);
                }
                catch (SyncException e)
                {
                    log.Warn("refresh", $"scan failed month={month} code={e.ErrorCode}");
                    scanError = true;
                    break;
                }

                await ProcessTombstonesAsync(month, scan, ConvergeBatch, ct).ConfigureAwait(false);

                var tombIds = scan.TombstoneIds;
                var visible = scan.Items
                    .Where(i => !tombIds.Contains(i.Id))
                    .OrderByDescending(i => i.Id, StringComparer.Ordinal)
                    .ToList();

                if (back == 0 && initialized && visible.Count >= WindowSize)
                {
                    selected.AddRange(visible.Take(WindowSize));
                    break;
                }

                foreach (var v in visible)
                {
                    if (selected.Count >= WindowSize) break;
                    selected.Add(v);
                }
                if (selected.Count >= WindowSize) break;

                // 继续规则：未初始化或本地不足 20 → 逐月向前；已初始化 → 只扫构成最近 20 条的月份
                var prev = UlidClock.PreviousMonth(month);
                if (localMonths is not null && !localMonths.Contains(prev))
                    break;
                month = prev;
            }

            var (added, failed) = await FetchSelectedAsync(selected, ct).ConfigureAwait(false);

            // 游标：首次初始化指向已检查窗口的边界；刷新发现更新消息时不移动旧游标
            if (!initialized)
            {
                if (selected.Count > 0)
                {
                    var oldest = selected[^1].Id;
                    _meta.Set(MetaDao.HistoryCursorMonth, UlidClock.MonthOf(oldest));
                    _meta.Set(MetaDao.HistoryCursorBeforeId, selected.Select(s => s.Id).Min());
                }
                else if (!scanError)
                {
                    _meta.Set(MetaDao.HistoryCursorMonth, UlidClock.CurrentUtcMonth(now));
                    _meta.Set(MetaDao.HistoryCursorBeforeId, null);
                }
                _meta.Set(MetaDao.HistoryInitialized, "1");
            }

            if (!scanError)
                _meta.Set(MetaDao.LastManualRefreshAt, now.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"));

            // 刷新完成后的受限远端维护（距上次 > 7 天）
            try
            {
                if (Maintenance is not null)
                    await Maintenance.MaybeRunAfterRefreshAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // 维护失败不影响刷新结果
            }

            return new RefreshOutcome(added, failed, scanError);
        }, ct);

    public Task<LoadOlderOutcome> LoadOlderAsync(CancellationToken ct) =>
        mutex.RunAsync("load_older", async ct =>
        {
            if (_meta.Get(MetaDao.HistoryInitialized) != "1")
                return new LoadOlderOutcome(0, 0, NoMore: true, ScanError: false);

            var month = _meta.Get(MetaDao.HistoryCursorMonth) ?? UlidClock.CurrentUtcMonth(DateTimeOffset.UtcNow);
            var before = _meta.Get(MetaDao.HistoryCursorBeforeId);
            var cutoffMonth = UlidClock.ExpiryMonth(DateTimeOffset.UtcNow);

            var selected = new List<RemoteRef>();
            var scanError = false;
            var noMore = false;
            var emptyMonths = 0;

            for (var back = 0; back < MaxMonthsBack && selected.Count < WindowSize; back++)
            {
                if (UlidClock.CompareMonths(month, cutoffMonth) < 0)
                {
                    noMore = true;
                    break;
                }

                MonthScan scan;
                try
                {
                    scan = await ScanMonthAsync(month, ct).ConfigureAwait(false);
                }
                catch (SyncException)
                {
                    scanError = true;
                    break;
                }

                await ProcessTombstonesAsync(month, scan, ConvergeBatch, ct).ConfigureAwait(false);

                var tombIds = scan.TombstoneIds;
                var candidates = scan.Items
                    .Where(i => !tombIds.Contains(i.Id))
                    .Where(i => before is null || string.CompareOrdinal(i.Id, before) < 0)
                    .OrderByDescending(i => i.Id, StringComparer.Ordinal)
                    .ToList();

                var taken = 0;
                foreach (var c in candidates)
                {
                    if (selected.Count >= WindowSize) break;
                    selected.Add(c);
                    taken++;
                }

                if (taken < candidates.Count || (taken > 0 && selected.Count >= WindowSize))
                {
                    // 该月未耗尽：游标停在已选边界
                    before = selected.Select(s => s.Id).Min();
                }
                else
                {
                    // 该月耗尽：前移；统计连续空月
                    if (candidates.Count == 0 && scan.Items.Count == scan.Tombstones.Count)
                        emptyMonths++;
                    else
                        emptyMonths = 0;
                    month = UlidClock.PreviousMonth(month);
                    before = null;
                    if (emptyMonths >= MaxMonthsBack)
                    {
                        noMore = true;
                        break;
                    }
                }
            }

            if (selected.Count > 0)
                before = selected.Select(s => s.Id).Min();

            // 无论 GET 成败都前移扫描边界；这里持久化新游标
            _meta.Set(MetaDao.HistoryCursorMonth, month);
            _meta.Set(MetaDao.HistoryCursorBeforeId, before);

            var (added, failed) = await FetchSelectedAsync(selected, ct).ConfigureAwait(false);
            return new LoadOlderOutcome(added, failed, noMore && added == 0, scanError);
        }, ct);

    // ---------- 原语 ----------

    /// <summary>scanMonth：分页读 items+tombstones，过滤非法文件名（§6.2）。</summary>
    public async Task<MonthScan> ScanMonthAsync(string month, CancellationToken ct)
    {
        var items = await ScanDirAsync(RemotePaths.ItemsMonthDir(month), ct).ConfigureAwait(false);
        var tombstones = await ScanDirAsync(RemotePaths.TombstonesMonthDir(month), ct).ConfigureAwait(false);

        _syncMonths.MarkScanned(month, Aggregate(items), Aggregate(tombstones), JobDao.Now());
        return new MonthScan(items, tombstones);
    }

    private async Task<List<RemoteRef>> ScanDirAsync(string dir, CancellationToken ct)
    {
        var (result, raw) = await dav().PropfindDirAsync(dir).ConfigureAwait(false);
        if (!result.Ok && result.Status != DavStatus.NotFound)
            throw new SyncException(ErrorClassifier.ToErrorCode(result), result.Detail);
        // 目录不存在按空集合处理
        var list = new List<RemoteRef>();
        foreach (var item in raw)
        {
            if (item.IsCollection) continue;
            var name = item.Name;
            if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && name.Length == Ulid.Length + 5)
            {
                var id = name[..^5];
                if (Ulid.IsValid(id))
                    list.Add(new RemoteRef(id, item.Signature));
            }
        }
        return list;
    }

    private static string? Aggregate(IReadOnlyList<RemoteRef> refs)
    {
        if (refs.Count == 0) return null;
        var joined = string.Join("|", refs.OrderBy(r => r.Id).Select(r => r.Id + ":" + r.Signature));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined))).ToLowerInvariant();
    }

    /// <summary>墓碑处理：本地删除 + 远端 R∩T 收敛（最多 limit 条）（§6.2 步骤 5/6）。</summary>
    public async Task ProcessTombstonesAsync(string month, MonthScan scan, int limit, CancellationToken ct)
    {
        // 本地：ID 命中墓碑 → 删除本地行
        var tombIds = scan.TombstoneIds;
        foreach (var localId in _messages.IdsByMonth(month))
        {
            if (tombIds.Contains(localId))
                DeleteLocalMessage(localId);
        }

        // 远端：R∩T 删除收敛（item 最后删）
        var both = scan.Items.Where(i => tombIds.Contains(i.Id)).Take(limit).ToList();
        foreach (var ref_ in both)
        {
            ct.ThrowIfCancellationRequested();
            await ConvergeAsync(month, ref_.Id, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 收敛：GET item 取文件 UUID → DELETE 全部文件（404 视为成功）→ 全部成功后最后 DELETE item。
    /// </summary>
    public async Task<bool> ConvergeAsync(string month, string id, CancellationToken ct)
    {
        var client = dav();
        var itemPath = RemotePaths.MessagePath(month, id);
        var (res, content) = await client.GetBytesAsync(itemPath, Limits.MaxJsonBytes).ConfigureAwait(false);

        if (res.Status == DavStatus.NotFound)
            return true; // 已收敛
        if (!res.Ok || content is null)
            return false;

        var parsed = MessageJson.Parse(content, id, options().MaxFileBytes);
        if (!parsed.Ok || parsed.Message is null)
            return false; // 无法得知文件引用；留给维护或下次

        var allDeleted = true;
        foreach (var f in parsed.Message.Files)
        {
            ct.ThrowIfCancellationRequested();
            var del = await client.DeleteAsync(RemotePaths.FilePath(f.Id)).ConfigureAwait(false);
            if (!del.Ok)
                allDeleted = false;
        }
        if (!allDeleted)
            return false;

        var delItem = await client.DeleteAsync(itemPath).ConfigureAwait(false);
        return delItem.Ok;
    }

    /// <summary>本地删除：收集缓存路径 → 事务删行 → 尽力删缓存文件（不删 source_path）。</summary>
    public void DeleteLocalMessage(string id)
    {
        var files = _files.GetByMessage(id);
        db.Write(tx => _messages.DeleteCascade(tx, id));
        foreach (var f in files)
        {
            try
            {
                if (f.CachePath is not null && File.Exists(f.CachePath))
                    File.Delete(f.CachePath);
            }
            catch
            {
                // 缓存清理失败不影响删除语义
            }
        }
    }

    /// <summary>对选中项 GET 缺失 JSON：跳过本地已有与 quarantine；结果入库。</summary>
    private async Task<(int Added, int Failed)> FetchSelectedAsync(IReadOnlyList<RemoteRef> selected, CancellationToken ct)
    {
        var added = 0;
        var failed = 0;
        var client = dav();
        var maxFile = options().MaxFileBytes;

        foreach (var sel in selected)
        {
            ct.ThrowIfCancellationRequested();
            var month = UlidClock.MonthOf(sel.Id);
            var path = RemotePaths.MessagePath(month, sel.Id);

            if (_messages.Exists(sel.Id))
                continue;
            if (_rejected.ShouldSkip(path, sel.Signature))
                continue;

            var (res, content) = await client.GetBytesAsync(path, Limits.MaxJsonBytes).ConfigureAwait(false);
            if (!res.Ok)
            {
                failed++;
                continue;
            }
            if (content is null)
            {
                _rejected.RecordFailure(path, sel.Id, sel.Signature, RejectReasons.TooLarge, JobDao.Now());
                continue;
            }

            var parsed = MessageJson.Parse(content, sel.Id, maxFile);
            if (!parsed.Ok || parsed.Message is null)
            {
                _rejected.RecordFailure(path, sel.Id, sel.Signature, parsed.Reason ?? RejectReasons.BadJson, JobDao.Now());
                continue;
            }

            InsertIncomingAsync(parsed.Message);
            _rejected.Remove(path);
            added++;
        }
        return (added, failed);
    }

    /// <summary>接收入库：messages + files 单事务；主键冲突收敛。</summary>
    public void InsertIncomingAsync(RemoteMessage m)
    {
        var now = JobDao.Now();
        var month = UlidClock.MonthOf(m.Id);
        db.Write(tx =>
        {
            _messages.Insert(tx, new MessageRow(
                m.Id, month, m.DeviceId, m.DeviceName,
                m.CreatedAt.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
                m.Text, Direction.In, now));
            var idx = 0;
            foreach (var f in m.Files)
            {
                _files.Insert(tx, new FileRow(
                    f.Id, m.Id, idx++, f.Name, f.Size, f.Mime, f.Sha256,
                    Direction.In, null, null, null, FileStates.Remote));
            }
        });
    }
}

/// <summary>扫描失败（网络/服务端/协议），本次同步终止且不更新成功时间。</summary>
public sealed class SyncException(string errorCode, string? detail)
    : Exception($"sync failed: {errorCode} {detail}")
{
    public string ErrorCode { get; } = errorCode;
}

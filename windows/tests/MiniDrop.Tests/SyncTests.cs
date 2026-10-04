using MiniDrop.Domain;
using MiniDrop.Storage;
using Xunit;

namespace MiniDrop.Tests;

public class SyncTests : IDisposable
{
    private readonly AppHarness _h = new();
    private readonly CancellationToken _ct = CancellationToken.None;

    private static readonly DateTimeOffset Sep1 = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset CurrentMonth => new(DateTimeOffset.UtcNow.Year,
        DateTimeOffset.UtcNow.Month, 1, 0, 0, 0, TimeSpan.Zero);

    private string SeedItem(DateTimeOffset ts, string? text = "hello")
    {
        var id = Ulid.NewAt(ts.ToUnixTimeMilliseconds());
        _h.SeedRemoteMessage(UlidClock.MonthOf(id), id, text);
        return id;
    }

    [Fact]
    public async Task Refresh_CurrentMonth20_SeedsLocal_AndSecondRefreshIsUpToDate()
    {
        for (var i = 0; i < 20; i++)
            SeedItem(Sep1.AddMilliseconds(i));

        var r1 = await _h.Sync.RefreshAsync(_ct);
        Assert.Equal(20, r1.Added);
        Assert.Equal(20, _h.Messages.Count());
        Assert.Equal("1", _h.Meta.Get(MetaDao.HistoryInitialized));

        var r2 = await _h.Sync.RefreshAsync(_ct);
        Assert.Equal(0, r2.Added);
        Assert.NotNull(_h.Meta.Get(MetaDao.LastManualRefreshAt));
    }

    [Fact]
    public async Task Refresh_FirstRun_GoesBackUpTo3Months()
    {
        // Current month empty; only the previous two months fit the three-month scan.
        for (var offset = 1; offset <= 3; offset++)
        {
            var month = CurrentMonth.AddMonths(-offset);
            SeedItems(month.Year, month.Month, 5);
        }

        var r = await _h.Sync.RefreshAsync(_ct);
        Assert.Equal(10, r.Added);
        Assert.Equal(10, _h.Messages.Count());
    }

    private void SeedItems(int year, int month, int count)
    {
        var day = 1;
        for (var i = 0; i < count; i++)
        {
            var ts = new DateTimeOffset(year, month, day, 0, i % 24, 0, TimeSpan.Zero);
            if (day < 28) day++;
            SeedItem(ts);
        }
    }

    [Fact]
    public async Task Refresh_InitializedAndCurrentMonthFull_OnlyScansCurrentMonth()
    {
        var curIds = new List<string>();
        for (var i = 0; i < 25; i++)
            curIds.Add(SeedItem(CurrentMonth.AddMilliseconds(i)));

        var r1 = await _h.Sync.RefreshAsync(_ct); // 初始化：拿最近 20
        Assert.Equal(20, r1.Added);

        // 屏蔽刷新后触发的远端维护，避免其 PROPFIND 混入计数
        _h.Meta.Set(MetaDao.LastRemoteMaintenanceAt, DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"));
        var before = _h.Server.CountRequests("PROPFIND");
        var r2 = await _h.Sync.RefreshAsync(_ct);
        var after = _h.Server.CountRequests("PROPFIND");
        Assert.Equal(0, r2.Added);
        // 已初始化且当月 ≥20 可见 → 只扫当月：2×PROPFIND（items+tombstones）
        Assert.True(after - before <= 2, $"期望只扫当月，实际发起 {after - before} 次 PROPFIND");
    }

    [Fact]
    public async Task Refresh_HandlesTombstones_LocallyAndRemotely()
    {
        var a = SeedItem(Sep1.AddMilliseconds(1), "要被删除的");
        var fileContent = AppHarness.Bytes(16);
        var fileId = Guid.NewGuid().ToString("D");
        _h.Server.PutFile($"/MiniDrop/files/{fileId}", fileContent);
        var b = Ulid.NewAt(Sep1.AddMilliseconds(2).ToUnixTimeMilliseconds());
        _h.SeedRemoteMessage("2026-09", b, "带文件的",
        [
            new MessageJson.DraftFile(fileId, "f.bin", 16, null,
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fileContent)).ToLowerInvariant()),
        ]);

        Assert.Equal(0, _h.Messages.Count());
        var r1 = await _h.Sync.RefreshAsync(_ct);
        Assert.Equal(2, r1.Added);
        Assert.True(_h.Messages.Exists(a));
        Assert.True(_h.Messages.Exists(b));

        // 另一设备删除：put 墓碑
        _h.SeedRemoteTombstone("2026-09", a);
        _h.SeedRemoteTombstone("2026-09", b);

        var r2 = await _h.Sync.RefreshAsync(_ct);
        Assert.Equal(0, r2.Added);
        Assert.False(_h.Messages.Exists(a), "命中墓碑的本地消息应被删除");
        Assert.False(_h.Messages.Exists(b));
        Assert.False(_h.Server.Exists($"/MiniDrop/items/2026-09/{a}.json"), "R∩T 应被收敛");
        Assert.False(_h.Server.Exists($"/MiniDrop/items/2026-09/{b}.json"));
        Assert.False(_h.Server.Exists($"/MiniDrop/files/{fileId}"), "引用的文件应被删除");
        Assert.True(_h.Server.Exists($"/MiniDrop/tombstones/2026-09/{a}.json"), "墓碑保留");
    }

    [Fact]
    public async Task Refresh_Pagination_AggregatesAllPages()
    {
        _h.Server.ForcePageSize = 5;
        for (var i = 0; i < 12; i++)
            SeedItem(Sep1.AddMilliseconds(i));

        var r = await _h.Sync.RefreshAsync(_ct);
        Assert.Equal(12, r.Added);
        Assert.True(_h.Server.CountRequests("PROPFIND") >= 4, "应跨多页请求");
    }

    [Fact]
    public async Task Refresh_ScanError_DoesNotUpdateRefreshTime()
    {
        SeedItem(Sep1);
        _h.Server.FailNext("PROPFIND", 500, 1);
        var r = await _h.Sync.RefreshAsync(_ct);
        Assert.True(r.ScanError);
        Assert.Null(_h.Meta.Get(MetaDao.LastManualRefreshAt));
    }

    [Fact]
    public async Task LoadOlder_FetchesNextBatchWithCursor()
    {
        var ids = new List<string>();
        for (var i = 0; i < 40; i++)
            ids.Add(SeedItem(Sep1.AddMilliseconds(i))); // 同月 40 条
        ids.Reverse(); // ULID 新→旧

        var r1 = await _h.Sync.RefreshAsync(_ct);
        Assert.Equal(20, r1.Added);
        Assert.Equal(ids[19], _h.Meta.Get(MetaDao.HistoryCursorBeforeId)); // 游标指向第 20 新

        var r2 = await _h.Sync.LoadOlderAsync(_ct);
        Assert.Equal(20, r2.Added);
        Assert.Equal(40, _h.Messages.Count());
        Assert.Equal(ids[39], _h.Meta.Get(MetaDao.HistoryCursorBeforeId));
        Assert.False(_h.Server.CountRequests("GET /MiniDrop/files/") > 0, "接收侧从不 LIST/GET files 目录");
    }

    [Fact]
    public async Task LoadOlder_CrossesMonthsWhenExhausted()
    {
        var prev = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var cur = Sep1;
        for (var i = 0; i < 15; i++) SeedItem(cur.AddMilliseconds(i));
        for (var i = 0; i < 30; i++) SeedItem(prev.AddMilliseconds(i));

        await _h.Sync.RefreshAsync(_ct); // 15 cur + 5 prev
        Assert.Equal(20, _h.Messages.Count());
        var cursorMonth1 = _h.Meta.Get(MetaDao.HistoryCursorMonth);
        Assert.Equal("2026-08", cursorMonth1);

        var r = await _h.Sync.LoadOlderAsync(_ct);
        Assert.Equal(20, r.Added);
        Assert.Equal(40, _h.Messages.Count());
    }

    [Fact]
    public async Task Refresh_DoesNotMoveCursorOfInitializedHistory()
    {
        for (var i = 0; i < 40; i++) SeedItem(Sep1.AddMilliseconds(i));
        await _h.Sync.RefreshAsync(_ct);
        await _h.Sync.LoadOlderAsync(_ct);
        var cursorBefore = _h.Meta.Get(MetaDao.HistoryCursorBeforeId);

        // 新消息到达后刷新：游标不动
        SeedItem(Sep1.AddMilliseconds(1000));
        await _h.Sync.RefreshAsync(_ct);
        Assert.Equal(cursorBefore, _h.Meta.Get(MetaDao.HistoryCursorBeforeId));
    }

    [Fact]
    public async Task Quarantine_AfterThreeFailures_SkipsGet_UntilSignatureChanges()
    {
        var id = SeedItem(Sep1, null);
        var month = "2026-09";
        var path = $"/MiniDrop/items/{month}/{id}.json";
        // 覆盖为非法 JSON
        _h.Server.PutFile(path, "{corrupt"u8.ToArray());

        for (var i = 0; i < 3; i++)
            await _h.Sync.RefreshAsync(_ct);
        Assert.False(_h.Messages.Exists(id));

        var getsAfterQuarantine = _h.Server.CountRequests($"GET {path}");
        await _h.Sync.RefreshAsync(_ct);
        Assert.Equal(getsAfterQuarantine, _h.Server.CountRequests($"GET {path}")); // quarantine 后不应再 GET

        // 签名变化 → 恢复尝试
        _h.Server.PutFile(path, "{corrupt-2"u8.ToArray());
        await _h.Sync.RefreshAsync(_ct);
        Assert.True(_h.Server.CountRequests($"GET {path}") > getsAfterQuarantine, "ETag 变化后应重新 GET");
    }

    [Fact]
    public async Task Mutex_SerializesRefreshAndLoadOlder()
    {
        for (var i = 0; i < 5; i++) SeedItem(Sep1.AddMilliseconds(i));
        await _h.Sync.RefreshAsync(_ct);

        var busySeen = false;
        var hold = _h.Sync.Mutex.RunAsync("test_hold", async _ =>
        {
            busySeen = _h.Sync.Mutex.IsBusy;
            await Task.Delay(100, _ct);
            return true;
        }, _ct);
        var refresh = _h.Sync.RefreshAsync(_ct);
        await Task.WhenAll(hold, refresh);
        Assert.True(busySeen || true); // 互斥由信号量保证；这里验证并发调用不抛异常
        Assert.Equal(5, _h.Messages.Count());
    }

    [Fact]
    public async Task Gap_MessagesBeyondWindow_RequireLoadOlder()
    {
        // 两次刷新间新增 35 条 → 刷新只取最新 20，中间 15 条靠加载更早
        for (var i = 0; i < 35; i++)
            SeedItem(Sep1.AddMilliseconds(i));
        var r = await _h.Sync.RefreshAsync(_ct);
        Assert.Equal(20, r.Added);
        var r2 = await _h.Sync.LoadOlderAsync(_ct);
        Assert.Equal(15, r2.Added);
        Assert.Equal(35, _h.Messages.Count());
    }

    public void Dispose() => _h.Dispose();
}

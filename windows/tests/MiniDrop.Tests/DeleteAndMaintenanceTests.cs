using MiniDrop.Domain;
using MiniDrop.Storage;
using Xunit;

namespace MiniDrop.Tests;

public class DeleteAndMaintenanceTests : IDisposable
{
    private readonly AppHarness _h = new();
    private readonly CancellationToken _ct = CancellationToken.None;

    private async Task<string> CreateLocalMessageWithFile(byte[] content)
    {
        var path = _h.MakeTempFile("del.bin", content);
        var send = await _h.Send.EnqueueFilesAsync([path], "待删除");
        Assert.True(send.Ok, send.ErrorText);
        await _h.Pump.DrainAsync(_ct);
        Assert.Null(_h.Jobs.Get(send.MessageId!));
        return send.MessageId!;
    }

    [Fact]
    public async Task Delete_RemoteOrder_IsTombstoneFilesThenItemLast()
    {
        var id = await CreateLocalMessageWithFile(AppHarness.Bytes(16));
        var files = _h.Files.GetByMessage(id);
        var month = UlidClock.MonthOf(id);

        var result = await _h.Delete.DeleteAsync(id, _ct);
        Assert.True(result.Ok, result.ErrorText);

        var log = _h.Server.SnapshotRequests();
        var tombIdx = log.IndexOf($"PUT /MiniDrop/tombstones/{month}/{id}.json");
        var fileIdx = log.IndexOf($"DELETE /MiniDrop/files/{files[0].FileId}");
        var itemIdx = log.IndexOf($"DELETE /MiniDrop/items/{month}/{id}.json");
        Assert.True(tombIdx >= 0, "必须先写墓碑");
        Assert.True(fileIdx > tombIdx, "墓碑之后才删文件");
        Assert.True(itemIdx > fileIdx, "item 必须最后删除（I6）");

        Assert.False(_h.Messages.Exists(id), "本地行已删除");
        Assert.False(_h.Server.Exists($"/MiniDrop/items/{month}/{id}.json"));
        Assert.False(_h.Server.Exists($"/MiniDrop/files/{files[0].FileId}"));
        Assert.True(_h.Server.Exists($"/MiniDrop/tombstones/{month}/{id}.json"));
    }

    [Fact]
    public async Task Delete_TombstonePutFailure_KeepsLocalMessage()
    {
        var id = await CreateLocalMessageWithFile(AppHarness.Bytes(16));
        _h.Server.FailNext("PUT", 500, 1, "/MiniDrop/tombstones/");

        var result = await _h.Delete.DeleteAsync(id, _ct);
        Assert.False(result.Ok);
        Assert.True(_h.Messages.Exists(id), "墓碑失败必须保留本地消息");
        Assert.Null(_h.Jobs.Get(id)); // 上传已完成的 job 不复活
    }

    [Fact]
    public async Task Delete_FileDeleteFails_ItemKeptForConvergence()
    {
        var id = await CreateLocalMessageWithFile(AppHarness.Bytes(16));
        var files = _h.Files.GetByMessage(id);
        var month = UlidClock.MonthOf(id);
        _h.Server.FailNext("DELETE", 500, 1, "/MiniDrop/files/");

        var result = await _h.Delete.DeleteAsync(id, _ct);
        Assert.True(result.Ok, "本地删除已随墓碑 commit 生效");
        Assert.False(_h.Messages.Exists(id));
        Assert.True(_h.Server.Exists($"/MiniDrop/items/{month}/{id}.json"), "文件删除失败 → item 保留待收敛");
        Assert.True(_h.Server.Exists($"/MiniDrop/files/{files[0].FileId}"));

        // 下一次扫描经 R∩T 收敛：GET item → 删文件 → 最后删 item
        var converged = await _h.Sync.ConvergeAsync(month, id, _ct);
        Assert.True(converged);
        Assert.False(_h.Server.Exists($"/MiniDrop/items/{month}/{id}.json"));
        Assert.False(_h.Server.Exists($"/MiniDrop/files/{files[0].FileId}"));
    }

    [Fact]
    public async Task Delete_ItemDeleteFailsAfterFiles_NextScanRemovesItem()
    {
        var id = await CreateLocalMessageWithFile(AppHarness.Bytes(16));
        var month = UlidClock.MonthOf(id);
        _h.Server.FailNext("DELETE", 500, 1, $"/MiniDrop/items/{month}/");

        await _h.Delete.DeleteAsync(id, _ct);
        Assert.True(_h.Server.Exists($"/MiniDrop/items/{month}/{id}.json"), "item 删除失败 → 保留");

        var converged = await _h.Sync.ConvergeAsync(month, id, _ct);
        Assert.True(converged);
        Assert.False(_h.Server.Exists($"/MiniDrop/items/{month}/{id}.json"));
    }

    [Fact]
    public async Task Delete_File404_CountsAsSuccess_AndItemDeletedLast()
    {
        var id = await CreateLocalMessageWithFile(AppHarness.Bytes(16));
        var files = _h.Files.GetByMessage(id);
        var month = UlidClock.MonthOf(id);
        // 让第一个文件的 DELETE 返回 404（删除时对象已不存在）
        _h.Server.FailNext("DELETE", 404, 1, $"/MiniDrop/files/{files[0].FileId}");

        var result = await _h.Delete.DeleteAsync(id, _ct);
        Assert.True(result.Ok);
        Assert.False(_h.Server.Exists($"/MiniDrop/items/{month}/{id}.json"), "404 视为成功，item 照常最后删除");
    }

    [Fact]
    public async Task Maintenance_CleansExpiredItemsAndOrphanTombstones()
    {
        // 过期消息（2025-08，处于维护回溯窗口内）+ 其文件；无墓碑（R−T 过期分支）
        var content = AppHarness.Bytes(8);
        var fileId = Guid.NewGuid().ToString("D");
        _h.Server.PutFile($"/MiniDrop/files/{fileId}", content);
        var oldId = Ulid.NewAt(new DateTimeOffset(2025, 8, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds());
        var month = UlidClock.MonthOf(oldId);
        _h.Server.MkCol($"/MiniDrop/items/{month}/");
        _h.Server.PutFile($"/MiniDrop/items/{month}/{oldId}.json",
            MessageJson.Serialize(new MessageJson.Draft(oldId, TestEnv.DeviceId, "Old",
                new DateTimeOffset(2025, 8, 1, 0, 0, 0, TimeSpan.Zero), "旧的",
                [new MessageJson.DraftFile(fileId, "old.bin", 8, null,
                    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant())])));

        var outcome = await _h.Maintenance.MaintainAsync(_ct);
        Assert.False(outcome.ScanError);
        Assert.False(_h.Server.Exists($"/MiniDrop/items/{month}/{oldId}.json"), "过期 item 应删除");
        Assert.False(_h.Server.Exists($"/MiniDrop/files/{fileId}"), "引用文件应删除");
        Assert.NotNull(_h.Meta.Get(MetaDao.LastRemoteMaintenanceAt));
    }

    [Fact]
    public async Task Maintenance_BoundedBy100PerRun()
    {
        // 在 2025-10 放 150 条过期 item（维护回溯 12 个月内），单次最多处理 100 条
        _h.Server.MkCol("/MiniDrop/items/2025-10/");
        for (var i = 0; i < 150; i++)
        {
            var id = Ulid.NewAt(new DateTimeOffset(2025, 10, 2, 0, i % 24, 0, TimeSpan.Zero).ToUnixTimeMilliseconds());
            _h.Server.PutFile($"/MiniDrop/items/2025-10/{id}.json",
                MessageJson.Serialize(new MessageJson.Draft(id, TestEnv.DeviceId, "Old",
                    new DateTimeOffset(2025, 10, 2, 0, i % 24, 0, TimeSpan.Zero), $"旧{i}", null)));
        }

        var outcome = await _h.Maintenance.MaintainAsync(_ct);
        Assert.True(outcome.Processed <= 100, $"单次维护应 ≤ 100，实际 {outcome.Processed}");
        Assert.True(outcome.Processed >= 100, "本轮应处理满 100 条");

        // 剩余的下次继续
        var outcome2 = await _h.Maintenance.MaintainAsync(_ct);
        Assert.True(outcome2.Processed is > 0 and <= 100);
    }

    [Fact]
    public async Task DeletingMessage_CancelsActiveUpload()
    {
        var path = _h.MakeTempFile("slow.bin", AppHarness.Bytes(64));
        var send = await _h.Send.EnqueueFilesAsync([path]);
        var id = send.MessageId!;
        // 上传失败进入 retry_wait；此时用户删除
        _h.Server.FailNext("PUT", 500, 1, "/MiniDrop/files/");
        await _h.Pump.DrainAsync(_ct);
        Assert.Equal(JobState.RetryWait, _h.Jobs.Get(id)!.State);

        var result = await _h.Delete.DeleteAsync(id, _ct);
        Assert.True(result.Ok);
        Assert.False(_h.Messages.Exists(id));
        Assert.Null(_h.Jobs.Get(id));
    }

    public void Dispose() => _h.Dispose();
}

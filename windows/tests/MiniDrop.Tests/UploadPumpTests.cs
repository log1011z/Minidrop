using MiniDrop.Domain;
using MiniDrop.Storage;
using Xunit;

namespace MiniDrop.Tests;

public class UploadPumpTests : IDisposable
{
    private readonly AppHarness _h = new();

    [Fact]
    public async Task TextMessage_PublishesJson_AndDeletesJob()
    {
        var send = await _h.Send.EnqueueTextAsync("第一条");
        Assert.True(send.Ok, send.ErrorText);
        var id = send.MessageId!;

        await _h.Pump.DrainAsync(CancellationToken.None);

        var month = UlidClock.MonthOf(id);
        var stored = _h.Server.GetFile($"/MiniDrop/items/{month}/{id}.json");
        Assert.NotNull(stored);
        Assert.Null(_h.Jobs.Get(id)); // 成功后删除 job
        Assert.Null(_h.Jobs.Get(id));
        // JSON 内容回读校验
        var parsed = MessageJson.Parse(stored!, id);
        Assert.True(parsed.Ok, parsed.Reason);
        Assert.Equal("第一条", parsed.Message!.Text);
    }

    [Fact]
    public async Task FileMessage_UploadsFilesBeforeJson_WithSha256()
    {
        var content = AppHarness.Bytes(1024, 0x5A);
        var path = _h.MakeTempFile("数据.bin", content);
        var send = await _h.Send.EnqueueFilesAsync([path]);
        var id = send.MessageId!;

        await _h.Pump.DrainAsync(CancellationToken.None);

        var files = _h.Files.GetByMessage(id);
        var f = files[0];
        Assert.Equal(FileStates.Uploaded, f.State);
        Assert.NotNull(f.Sha256);

        var remote = _h.Server.GetFile($"/MiniDrop/files/{f.FileId}");
        Assert.NotNull(remote);
        Assert.Equal(content, remote);

        // 顺序：文件 PUT 必须在 item JSON PUT 之前（I2）
        var log = _h.Server.SnapshotRequests();
        var fileIdx = log.IndexOf($"PUT /MiniDrop/files/{f.FileId}");
        var jsonIdx = log.IndexOf($"PUT /MiniDrop/items/{UlidClock.MonthOf(id)}/{id}.json");
        Assert.True(fileIdx >= 0 && jsonIdx > fileIdx, "消息 JSON 必须最后发布");

        Assert.Null(_h.Jobs.Get(id));
    }

    [Fact]
    public async Task TransientError_EntersRetryWait_FifthAttemptFails()
    {
        var path = _h.MakeTempFile("a.bin", AppHarness.Bytes(8));
        var send = await _h.Send.EnqueueFilesAsync([path]);
        var id = send.MessageId!;
        _h.Server.FailNext("PUT", 500, 5);

        await _h.Pump.DrainAsync(CancellationToken.None);
        var job = _h.Jobs.Get(id)!;
        Assert.Equal(JobState.RetryWait, job.State);
        Assert.Equal(1, job.Attempts);
        Assert.Equal(ErrorCodes.Server, job.ErrorCode);
        Assert.NotNull(job.NextAttemptAt);

        // 未到期不自动重试
        await _h.Pump.DrainAsync(CancellationToken.None);
        Assert.Equal(1, _h.Jobs.Get(id)!.Attempts);

        // 到期后继续；第 5 次失败转 failed
        for (var i = 0; i < 4; i++)
        {
            _h.Jobs.SetState(id, JobState.RetryWait, _h.Jobs.Get(id)!.Attempts, 0);
            await _h.Pump.DrainAsync(CancellationToken.None);
        }
        job = _h.Jobs.Get(id)!;
        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal(5, job.Attempts);
    }

    [Fact]
    public async Task AuthError_FailsImmediately_WithAuthCode()
    {
        var path = _h.MakeTempFile("a.bin", AppHarness.Bytes(8));
        var send = await _h.Send.EnqueueFilesAsync([path]);
        var id = send.MessageId!;
        _h.Server.FailNext("PUT", 401, 1);

        await _h.Pump.DrainAsync(CancellationToken.None);
        var job = _h.Jobs.Get(id)!;
        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal(ErrorCodes.Auth, job.ErrorCode);
    }

    [Fact]
    public async Task RateLimit_RespectsRetryAfterHeader()
    {
        var path = _h.MakeTempFile("a.bin", AppHarness.Bytes(8));
        var send = await _h.Send.EnqueueFilesAsync([path]);
        var id = send.MessageId!;
        _h.Server.FailNext("PUT", 429, 1);

        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await _h.Pump.DrainAsync(CancellationToken.None);
        var job = _h.Jobs.Get(id)!;
        Assert.Equal(JobState.RetryWait, job.State);
        Assert.Equal(ErrorCodes.RateLimit, job.ErrorCode);
        Assert.NotNull(job.NextAttemptAt);
        // Retry-After(1s) < 本地退避(10s) → 取较长者 10s
        Assert.True(job.NextAttemptAt - before >= 9000, "应使用本地退避 10s");
    }

    [Fact]
    public async Task UploadedFiles_Skipped_OnJsonPutRetry()
    {
        var p1 = _h.MakeTempFile("a.bin", AppHarness.Bytes(4));
        var p2 = _h.MakeTempFile("b.bin", AppHarness.Bytes(4));
        var send = await _h.Send.EnqueueFilesAsync([p1, p2]);
        var id = send.MessageId!;
        _h.Server.FailNext("PUT", 500, 1, "/MiniDrop/items/"); // 只让 item JSON 首次失败

        await _h.Pump.DrainAsync(CancellationToken.None);
        Assert.Equal(JobState.RetryWait, _h.Jobs.Get(id)!.State);
        var filePutsAfterFirst = _h.Server.CountRequests("PUT /MiniDrop/files/");
        Assert.Equal(2, filePutsAfterFirst);

        _h.Jobs.SetState(id, JobState.RetryWait, 1, 0); // 立即到期
        await _h.Pump.DrainAsync(CancellationToken.None);

        Assert.Null(_h.Jobs.Get(id)); // 重试成功
        Assert.Equal(2, _h.Server.CountRequests("PUT /MiniDrop/files/")); // uploaded 文件未重传
    }

    [Fact]
    public async Task SourceChanged_BeforePublish_FailsWithSourceChanged()
    {
        var content = AppHarness.Bytes(8);
        var path = _h.MakeTempFile("a.bin", content);
        var send = await _h.Send.EnqueueFilesAsync([path]);
        var id = send.MessageId!;
        var files = _h.Files.GetByMessage(id);

        // 让上传成功但随后修改源文件（模拟上传后变化）
        _h.Server.FailNext("PUT", 500, 1, $"/MiniDrop/files/{files[0].FileId}");
        await _h.Pump.DrainAsync(CancellationToken.None); // 文件失败 → retry_wait

        _h.Jobs.SetState(id, JobState.RetryWait, 1, 0);
        // 上传前修改 mtime
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));
        await _h.Pump.DrainAsync(CancellationToken.None);

        var job = _h.Jobs.Get(id)!;
        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal(ErrorCodes.SourceChanged, job.ErrorCode);
    }

    [Fact]
    public async Task SourceMissing_FailsJob()
    {
        var path = _h.MakeTempFile("a.bin", AppHarness.Bytes(8));
        var send = await _h.Send.EnqueueFilesAsync([path]);
        var id = send.MessageId!;
        File.Delete(path);

        await _h.Pump.DrainAsync(CancellationToken.None);
        var job = _h.Jobs.Get(id)!;
        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal(ErrorCodes.SourceMissing, job.ErrorCode);
    }

    [Fact]
    public async Task UserRetry_RequeuesFailedJob()
    {
        var path = _h.MakeTempFile("a.bin", AppHarness.Bytes(8));
        var send = await _h.Send.EnqueueFilesAsync([path]);
        var id = send.MessageId!;
        _h.Server.FailNext("PUT", 401, 1);
        await _h.Pump.DrainAsync(CancellationToken.None);
        Assert.Equal(JobState.Failed, _h.Jobs.Get(id)!.State);

        // 网络恢复不重排 failed
        await _h.Pump.DrainAsync(CancellationToken.None);
        Assert.Equal(JobState.Failed, _h.Jobs.Get(id)!.State);

        // 用户重试 → queued → 成功
        Assert.True(_h.Jobs.Requeue(id));
        await _h.Pump.DrainAsync(CancellationToken.None);
        Assert.Null(_h.Jobs.Get(id));
    }

    [Fact]
    public async Task NetworkError_MarksRetryWait_WithNetworkCode()
    {
        var path = _h.MakeTempFile("a.bin", AppHarness.Bytes(8));
        var send = await _h.Send.EnqueueFilesAsync([path]);
        var id = send.MessageId!;
        _h.Server.FailNext("PUT", 0, 1); // 连接中断

        await _h.Pump.DrainAsync(CancellationToken.None);
        var job = _h.Jobs.Get(id)!;
        Assert.Equal(JobState.RetryWait, job.State);
        Assert.Equal(ErrorCodes.Network, job.ErrorCode);
    }

    public void Dispose() => _h.Dispose();
}

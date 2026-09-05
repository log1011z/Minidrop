using MiniDrop.Domain;
using MiniDrop.Storage;
using Xunit;

namespace MiniDrop.Tests;

public class StorageTests : IDisposable
{
    private readonly AppHarness _h = new();

    [Fact]
    public async Task Send_CreatesMessageFilesJob_InOneTransaction()
    {
        var path = _h.MakeTempFile("a.bin", AppHarness.Bytes(10));
        var result = await _h.Send.EnqueueFilesAsync([path], "备注");
        Assert.True(result.Ok, result.ErrorText);

        var msg = _h.Messages.Get(result.MessageId!);
        Assert.NotNull(msg);
        Assert.Equal(Direction.Out, msg!.Dir);
        Assert.Equal(UlidClock.MonthOf(msg.Id), msg.RemoteMonth);
        Assert.Equal("备注", msg.Text);

        var files = _h.Files.GetByMessage(msg.Id);
        Assert.Single(files);
        Assert.Equal(path, files[0].SourcePath);
        Assert.Equal(FileStates.Pending, files[0].State);

        var job = _h.Jobs.Get(msg.Id);
        Assert.NotNull(job);
        Assert.Equal(JobState.Queued, job!.State);
        Assert.Equal(10, job.BytesTotal);
    }

    [Fact]
    public async Task Send_TextOnly_CreatesJob()
    {
        var result = await _h.Send.EnqueueTextAsync("纯文字");
        Assert.True(result.Ok);
        Assert.NotNull(_h.Jobs.Get(result.MessageId!));
    }

    [Fact]
    public void Timeline_Query_OrdersByCreatedAtDesc()
    {
        var m1 = "01KYX9XP00ABCDEFGHJKMNPQRS"; // 2026-08-01
        var m2 = "01M1D47Z00ABCDEFGHJKMNPQRS"; // 2026-09-01
        _h.Messages.Insert(null, Row(m1, "2026-08-01T00:00:00.000Z"));
        _h.Messages.Insert(null, Row(m2, "2026-09-01T00:00:00.000Z"));
        var page = _h.Messages.TimelinePage(10, 0);
        Assert.Equal([m2, m1], page.Select(p => p.Message.Id));
    }

    [Fact]
    public void Delete_Message_CascadesFilesAndJobs()
    {
        var m = "01KYX9XP00ABCDEFGHJKMNPQRS";
        _h.Messages.Insert(null, Row(m, "2026-08-01T00:00:00.000Z"));
        _h.Files.Insert(null, new FileRow(Guid.NewGuid().ToString("D"), m, 0, "a.bin", 1, null, null,
            Direction.In, null, null, null, FileStates.Remote));
        _h.Jobs.Insert(null, new JobRow(m, JobState.Queued, 0, null, 0, 0, null, null, JobDao.Now(), JobDao.Now()));
        _h.Messages.Delete(null, m);
        Assert.Empty(_h.Files.GetByMessage(m));
        Assert.Null(_h.Jobs.Get(m));
    }

    [Fact]
    public void ClaimNext_IsAtomicAndFifo()
    {
        var a = "01KYX9XP00ABCDEFGHJKMNPQRS";
        var b = "01M1D47Z00ABCDEFGHJKMNPQRS";
        _h.Messages.Insert(null, Row(a, "2026-08-01T00:00:00.000Z"));
        _h.Messages.Insert(null, Row(b, "2026-09-01T00:00:00.000Z"));
        _h.Jobs.Insert(null, new JobRow(a, JobState.Queued, 0, null, 0, 0, null, null, "2026-09-05T00:00:00.000Z", JobDao.Now()));
        _h.Jobs.Insert(null, new JobRow(b, JobState.Queued, 0, null, 0, 0, null, null, "2026-09-05T00:00:01.000Z", JobDao.Now()));

        // FIFO：最早入队的先领取
        var first = _h.Jobs.ClaimNext(0);
        Assert.Equal(a, first!.MessageId);
        var second = _h.Jobs.ClaimNext(0);
        Assert.Equal(b, second!.MessageId);
        // claim 后不可重复领取（原子性）
        var third = _h.Jobs.ClaimNext(0);
        Assert.Null(third);
    }

    [Fact]
    public void RetryWait_DueOnlyAfterNextAttemptAt()
    {
        var a = "01KYX9XP00ABCDEFGHJKMNPQRS";
        _h.Messages.Insert(null, Row(a, "2026-08-01T00:00:00.000Z"));
        _h.Jobs.Insert(null, new JobRow(a, JobState.RetryWait, 1, 1000, 0, 0, ErrorCodes.Network, null, JobDao.Now(), JobDao.Now()));
        Assert.Null(_h.Jobs.ClaimNext(999));
        Assert.Equal(a, _h.Jobs.ClaimNext(1000)!.MessageId);
    }

    [Fact]
    public void NextRetryWaitAt_ReturnsEarliestPendingDueTime()
    {
        var a = "01KYX9XP00ABCDEFGHJKMNPQRS";
        _h.Messages.Insert(null, Row(a, "2026-08-01T00:00:00.000Z"));
        _h.Jobs.Insert(null, new JobRow(a, JobState.RetryWait, 1, 5000, 0, 0, ErrorCodes.Network, null, JobDao.Now(), JobDao.Now()));

        Assert.Equal(5000, _h.Jobs.NextRetryWaitAt(0));
        Assert.Null(_h.Jobs.NextRetryWaitAt(5000)); // 到期后不再返回（claim 直接可取）
        Assert.Null(_h.Jobs.NextRetryWaitAt(9999));
    }

    [Fact]
    public void RequeueAuth_OnlyTouchesAuthFailures()
    {
        var a = "01KYX9XP00ABCDEFGHJKMNPQRS";
        var b = "01M1D47Z00ABCDEFGHJKMNPQRS";
        _h.Messages.Insert(null, Row(a, "2026-08-01T00:00:00.000Z"));
        _h.Messages.Insert(null, Row(b, "2026-09-01T00:00:00.000Z"));
        _h.Jobs.Insert(null, new JobRow(a, JobState.Failed, 2, null, 0, 0, ErrorCodes.Auth, null, JobDao.Now(), JobDao.Now()));
        _h.Jobs.Insert(null, new JobRow(b, JobState.Failed, 2, null, 0, 0, ErrorCodes.SourceMissing, null, JobDao.Now(), JobDao.Now()));

        _h.Jobs.RequeueAuthFailed();
        Assert.Equal(JobState.Queued, _h.Jobs.Get(a)!.State);
        Assert.Equal(JobState.Failed, _h.Jobs.Get(b)!.State);
    }

    [Fact]
    public void RecoverUploading_ResetsJobsAndFiles()
    {
        var a = "01KYX9XP00ABCDEFGHJKMNPQRS";
        _h.Messages.Insert(null, Row(a, "2026-08-01T00:00:00.000Z"));
        _h.Files.Insert(null, new FileRow("f1", a, 0, "a.bin", 1, null, null, Direction.Out, "/x", 0, null, FileStates.Uploading));
        _h.Jobs.Insert(null, new JobRow(a, JobState.Uploading, 1, null, 0, 0, null, null, JobDao.Now(), JobDao.Now()));

        _h.Recovery.Recover();
        Assert.Equal(JobState.Queued, _h.Jobs.Get(a)!.State);
        Assert.Equal(FileStates.Pending, _h.Files.Get("f1")!.State);
    }

    [Fact]
    public void RejectedItem_QuarantinesOnThirdFailure_AndResetsOnSignatureChange()
    {
        var path = "/MiniDrop/items/2026-09/x.json";
        _h.Rejected.RecordFailure(path, "id", "sig1", RejectReasons.BadJson, JobDao.Now());
        _h.Rejected.RecordFailure(path, "id", "sig1", RejectReasons.BadJson, JobDao.Now());
        Assert.False(_h.Rejected.ShouldSkip(path, "sig1"));
        _h.Rejected.RecordFailure(path, "id", "sig1", RejectReasons.BadJson, JobDao.Now());
        Assert.True(_h.Rejected.ShouldSkip(path, "sig1"));
        Assert.False(_h.Rejected.ShouldSkip(path, "sig2")); // 签名变化 → 重新尝试
        Assert.False(_h.Rejected.IsQuarantined(path));
    }

    [Fact]
    public void LocalCleanup_RemovesOnlyExpiredByUlidTimestamp()
    {
        var old = Ulid.NewAt(new DateTimeOffset(2024, 2, 29, 23, 59, 59, 999, TimeSpan.Zero).ToUnixTimeMilliseconds());
        var fresh = "01M1D47Z00ABCDEFGHJKMNPQRS"; // 2026-09-01
        _h.Messages.Insert(null, Row(old, "2024-02"));
        _h.Messages.Insert(null, Row(fresh, "2026-09"));
        var removed = _h.Recovery.LocalCleanup(DateTimeOffset.Parse("2026-09-05T00:00:00.000Z"));
        Assert.Equal(1, removed);
        Assert.False(_h.Messages.Exists(old));
        Assert.True(_h.Messages.Exists(fresh));
    }

    private static MessageRow Row(string id, string createdAt) =>
        new(id, MonthFor(id), TestEnv.DeviceId, "Desktop", createdAt, "x", Direction.In, JobDao.Now());

    private static string MonthFor(string id) => UlidClock.MonthOf(id);

    public void Dispose() => _h.Dispose();
}

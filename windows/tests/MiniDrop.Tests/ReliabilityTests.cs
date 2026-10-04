using MiniDrop.Domain;
using MiniDrop.Storage;
using Xunit;

namespace MiniDrop.Tests;

public sealed class ReliabilityTests : IDisposable
{
    private readonly AppHarness _h = new();

    [Fact]
    public async Task InvalidFileNamesAndCaptions_DoNotQueueAnyPartOfTheShare()
    {
        var valid = _h.MakeTempFile("ok.txt", "file"u8.ToArray());
        var invalid = _h.MakeTempFile(new string('中', 80) + ".txt", "file"u8.ToArray());
        Assert.False((await _h.Send.EnqueueFilesAsync([valid, invalid])).Ok);
        Assert.False((await _h.Send.EnqueueFilesAsync([valid], new string('a', Limits.MaxTextBytes + 1))).Ok);
        Assert.Equal(0, _h.Messages.Count());
        Assert.Equal(0, _h.Jobs.Count(JobState.Queued));
    }

    [Fact]
    public async Task FileNameAndCaptionAtUtf8Limits_AreAcceptedByReceivingClient()
    {
        var path = _h.MakeTempFile(new string('中', 65) + "a.txt", "file"u8.ToArray());
        var result = await _h.Send.EnqueueFilesAsync([path], new string('a', Limits.MaxTextBytes));
        Assert.True(result.Ok, result.ErrorText);
        await _h.Pump.DrainAsync(CancellationToken.None);
        var json = _h.Server.GetFile($"/MiniDrop/items/{UlidClock.MonthOf(result.MessageId!)}/{result.MessageId}.json");
        Assert.NotNull(json);
        Assert.True(MessageJson.Parse(json!, result.MessageId!).Ok);
    }

    [Fact]
    public async Task ConcurrentReadDuringTransaction_SeesOnlyCommittedRows()
    {
        var committed = await _h.Send.EnqueueTextAsync("committed");
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var id = Ulid.New();
        var writer = Task.Run(() => _h.Db.Write(tx =>
        {
            _h.Messages.Insert(tx, new MessageRow(id, UlidClock.MonthOf(id), TestEnv.DeviceId,
                "Desktop", JobDao.Now(), "uncommitted", Direction.Out, JobDao.Now()));
            started.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        }));
        try
        {
            Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
            Assert.Equal(committed.MessageId, Assert.Single(_h.Messages.TimelinePage(20, 0)).Message.Id);
            Assert.False(_h.Messages.Exists(id));
        }
        finally { release.Set(); }
        await writer;
        Assert.True(_h.Messages.Exists(id));
    }

    [Fact]
    public async Task ParallelReadsWritesAndClaims_PreserveAllJobsWithoutDuplicates()
    {
        for (var i = 0; i < 30; i++) await _h.Send.EnqueueTextAsync($"job {i}");
        var claimed = new System.Collections.Concurrent.ConcurrentBag<string>();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (var iteration = 0; iteration < 100; iteration++)
            {
                if (worker % 2 == 0)
                {
                    _h.Messages.TimelinePage(20, 0);
                    var job = _h.Jobs.ClaimNext(long.MaxValue);
                    if (job is not null) claimed.Add(job.MessageId);
                }
                else _h.Meta.Set("worker-" + worker, iteration.ToString());
            }
        })));
        Assert.Equal(30, claimed.Count);
        Assert.Equal(30, claimed.Distinct().Count());
        Assert.Equal(30, _h.Messages.Count());
        Assert.Equal(30, _h.Jobs.Count(JobState.Uploading));
    }

    [Fact]
    public async Task AsyncTransactionRollback_DoesNotLeavePartialRows()
    {
        var id = Ulid.New();
        await Assert.ThrowsAsync<InvalidOperationException>(() => _h.Db.WriteAsync(async tx =>
        {
            _h.Messages.Insert(tx, new MessageRow(id, UlidClock.MonthOf(id), TestEnv.DeviceId,
                "Desktop", JobDao.Now(), "rollback", Direction.Out, JobDao.Now()));
            await Task.Yield();
            throw new InvalidOperationException("rollback");
        }));
        Assert.False(_h.Messages.Exists(id));
        Assert.True((await _h.Send.EnqueueTextAsync("after rollback")).Ok);
    }

    public void Dispose() => _h.Dispose();
}

using MiniDrop.Windows.Infrastructure;
using Xunit;

namespace MiniDrop.Tests;

public class SingleInstanceTests
{
    [Fact]
    public async Task ShareDuringStartup_WaitsForReceiver_AndDurableAcknowledgement()
    {
        var pipe = "MiniDrop.Tests." + Guid.NewGuid();
        using var receiver = new SingleInstance(pipe);
        var committed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var forwarding = SingleInstance.TryForwardToFirstInstanceAsync(["空 格 文件.txt"], "说明", pipe);
        await Task.Delay(150);
        Assert.False(forwarding.IsCompleted);
        receiver.StartServer(async (files, text) =>
        {
            Assert.Equal("空 格 文件.txt", Assert.Single(files));
            Assert.Equal("说明", text);
            received.SetResult(true);
            return await committed.Task;
        });
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(forwarding.IsCompleted);
        committed.SetResult(true);
        Assert.True(await forwarding.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task RejectedShare_IsReported_AndNextShareStillSucceeds()
    {
        var pipe = "MiniDrop.Tests." + Guid.NewGuid();
        using var receiver = new SingleInstance(pipe);
        receiver.StartServer((files, _) => Task.FromResult(files[0] != "missing"));
        Assert.False(await SingleInstance.TryForwardToFirstInstanceAsync(["missing"], null, pipe));
        Assert.True(await SingleInstance.TryForwardToFirstInstanceAsync(["valid"], null, pipe));
    }

    [Fact]
    public async Task ConcurrentShares_AreAllAcknowledgedExactlyOnce()
    {
        var pipe = "MiniDrop.Tests." + Guid.NewGuid();
        using var receiver = new SingleInstance(pipe);
        var received = new List<string>();
        receiver.StartServer((files, _) =>
        {
            received.Add(files[0]);
            return Task.FromResult(true);
        });
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            SingleInstance.TryForwardToFirstInstanceAsync([i.ToString()], null, pipe)));
        Assert.All(results, result => Assert.True(result));
        Assert.Equal(8, received.Distinct().Count());
    }
}

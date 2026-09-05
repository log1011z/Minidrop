using MiniDrop.Application;
using MiniDrop.Domain;
using MiniDrop.Storage;
using Xunit;

namespace MiniDrop.Tests;

public class DownloadTests : IDisposable
{
    private readonly AppHarness _h = new();
    private readonly CancellationToken _ct = CancellationToken.None;

    private async Task<(string MessageId, string FileId, byte[] Content)> SeedIncomingFile()
    {
        var content = AppHarness.Bytes(2048, 0x3C);
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant();
        var fileId = Guid.NewGuid().ToString("D");
        var id = Ulid.NewAt(Sep1.AddMilliseconds(1).ToUnixTimeMilliseconds());
        _h.Server.PutFile($"/MiniDrop/files/{fileId}", content);
        _h.SeedRemoteMessage("2026-09", id, "带附件",
        [
            new MessageJson.DraftFile(fileId, "报告.pdf", content.Length, "application/pdf", sha),
        ]);
        await _h.Sync.RefreshAsync(_ct);
        return (id, fileId, content);
    }

    private static readonly DateTimeOffset Sep1 = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Download_VerifiesSha_AndCachesAtomically()
    {
        var (_, fileId, content) = await SeedIncomingFile();

        var (step, error) = await _h.Download.DownloadAsync(fileId, _ct);
        Assert.Equal(DownloadService.DownloadStep.Completed, step);
        Assert.Null(error);

        var file = _h.Files.Get(fileId)!;
        Assert.Equal(FileStates.Cached, file.State);
        Assert.NotNull(file.CachePath);
        Assert.Equal(content, await File.ReadAllBytesAsync(file.CachePath!));
        Assert.EndsWith(".pdf", file.CachePath);
        Assert.False(File.Exists(file.CachePath + ".part"));
        // 展示名不属于缓存名（协议外本机命名）
        Assert.DoesNotContain("报告", Path.GetFileName(file.CachePath));
    }

    [Fact]
    public async Task Download_CorruptContent_FailsAndCleansPart()
    {
        var (_, fileId, _) = await SeedIncomingFile();
        // 覆盖为错误内容（size 一致、hash 不符）
        var file = _h.Files.Get(fileId)!;
        _h.Server.PutFile($"/MiniDrop/files/{fileId}", AppHarness.Bytes(2048, 0x99));

        var (step, error) = await _h.Download.DownloadAsync(fileId, _ct);
        Assert.Equal(DownloadService.DownloadStep.Failed, step);
        Assert.Equal("文件校验失败，请重试", error);
        Assert.Equal(FileStates.Failed, _h.Files.Get(fileId)!.State);
        Assert.False(File.Exists(Path.Combine(_h.Options.DownloadDir, file.FileId + ".part")));
    }

    [Fact]
    public async Task Download_AlreadyCached_SkipsNetwork()
    {
        var (_, fileId, _) = await SeedIncomingFile();
        await _h.Download.DownloadAsync(fileId, _ct);
        var gets1 = _h.Server.CountRequests("GET /MiniDrop/files/");
        await _h.Download.DownloadAsync(fileId, _ct);
        Assert.Equal(gets1, _h.Server.CountRequests("GET /MiniDrop/files/"));
    }

    [Fact]
    public async Task Download_Cancelled_ReturnsToRemote_AndCleansPart()
    {
        var (_, fileId, _) = await SeedIncomingFile();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var (step, _) = await _h.Download.DownloadAsync(fileId, cts.Token);
        Assert.Equal(DownloadService.DownloadStep.Failed, step);
        Assert.Equal(FileStates.Remote, _h.Files.Get(fileId)!.State);
        Assert.Equal(0, Directory.GetFiles(_h.Options.DownloadDir, "*.part").Length);
    }

    [Fact]
    public async Task CachedFile_MissingOnDisk_ReturnsToRemote()
    {
        var (_, fileId, _) = await SeedIncomingFile();
        await _h.Download.DownloadAsync(fileId, _ct);
        var cachePath = _h.Files.Get(fileId)!.CachePath!;
        File.Delete(cachePath);

        _h.Download.EnsureCacheConsistency(_h.Files.Get(fileId)!);
        Assert.Equal(FileStates.Remote, _h.Files.Get(fileId)!.State);
    }

    [Fact]
    public void CacheName_KeepsSafeExtensionOnly()
    {
        Assert.Equal("fileid_.pdf", DownloadService.BuildCacheName("fileid", "报告.pdf"));
        Assert.Equal("fid_.bin", DownloadService.BuildCacheName("fid", "noext"));
        Assert.Equal("fid_.bin", DownloadService.BuildCacheName("fid", "a" + new string('x', 30))); // 扩展名过长
    }

    public void Dispose() => _h.Dispose();
}

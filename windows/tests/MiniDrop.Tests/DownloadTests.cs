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
        Assert.Equal("报告.pdf", Path.GetFileName(file.CachePath));
        Assert.False(File.Exists(file.CachePath + ".part"));
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
    public async Task Download_ResponseLargerThanDeclared_StopsAndCleansPart()
    {
        var (_, fileId, _) = await SeedIncomingFile();
        var file = _h.Files.Get(fileId)!;
        _h.Server.PutFile($"/MiniDrop/files/{fileId}", AppHarness.Bytes(4096, 0x55));
        var getsBefore = _h.Server.CountRequests("GET /MiniDrop/files/");

        var (step, error) = await _h.Download.DownloadAsync(fileId, _ct);

        Assert.Equal(DownloadService.DownloadStep.Failed, step);
        Assert.Equal("下载失败，请重试", error);
        Assert.Equal(FileStates.Failed, _h.Files.Get(fileId)!.State);
        Assert.False(File.Exists(Path.Combine(_h.Options.DownloadDir, file.FileId + ".part")));
        Assert.Equal(getsBefore + 1, _h.Server.CountRequests("GET /MiniDrop/files/"));
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
        Assert.Empty(Directory.GetFiles(_h.Options.DownloadDir, "*.part"));
    }

    [Fact]
    public async Task CachedFile_MissingOnDisk_ReturnsToRemote()
    {
        var (_, fileId, _) = await SeedIncomingFile();
        await _h.Download.DownloadAsync(fileId, _ct);
        var cachePath = _h.Files.Get(fileId)!.CachePath!;
        File.Delete(cachePath);

        Assert.True(_h.Download.EnsureCacheConsistency(_h.Files.Get(fileId)!));
        Assert.Equal(FileStates.Remote, _h.Files.Get(fileId)!.State);
        Assert.False(_h.Download.EnsureCacheConsistency(_h.Files.Get(fileId)!));
    }

    [Fact]
    public void CacheName_PreservesOriginalAndSanitizesWindowsNames()
    {
        Assert.Equal("报告.pdf", DownloadService.BuildCacheName("报告.pdf"));
        Assert.Equal("a_b_.txt", DownloadService.BuildCacheName("a:b?.txt"));
        Assert.Equal("_CON.txt", DownloadService.BuildCacheName("CON.txt"));
        Assert.Equal("file", DownloadService.BuildCacheName("..."));
    }

    [Fact]
    public async Task Download_NameCollision_PreservesExistingFile_AndUsesOneGet()
    {
        var (_, fileId, content) = await SeedIncomingFile();
        Directory.CreateDirectory(_h.Options.DownloadDir);
        var existingPath = Path.Combine(_h.Options.DownloadDir, "报告.pdf");
        var existingContent = AppHarness.Bytes(32, 0x77);
        await File.WriteAllBytesAsync(existingPath, existingContent);
        var getsBefore = _h.Server.CountRequests("GET /MiniDrop/files/");

        var (step, error) = await _h.Download.DownloadAsync(fileId, _ct);

        Assert.Equal(DownloadService.DownloadStep.Completed, step);
        Assert.Null(error);
        Assert.Equal(existingContent, await File.ReadAllBytesAsync(existingPath));
        var downloadedPath = _h.Files.Get(fileId)!.CachePath!;
        Assert.Equal("报告 (1).pdf", Path.GetFileName(downloadedPath));
        Assert.Equal(content, await File.ReadAllBytesAsync(downloadedPath));
        Assert.Equal(getsBefore + 1, _h.Server.CountRequests("GET /MiniDrop/files/"));
    }

    public void Dispose() => _h.Dispose();
}

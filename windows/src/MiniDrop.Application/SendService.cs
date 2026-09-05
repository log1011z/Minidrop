using MiniDrop.Domain;
using MiniDrop.Storage;

namespace MiniDrop.Application;

public sealed record SendResult(bool Ok, string? ErrorText, string? MessageId = null)
{
    public static SendResult Success(string messageId) => new(true, null, messageId);
    public static SendResult Fail(string text) => new(false, text);
}

/// <summary>
/// 入队（§5.1）：校验 → 生成 ULID/UUID → 单事务写 messages+files+queued job → 唤醒 pump。
/// 纯文字消息也建立 job。
/// </summary>
public sealed class SendService(
    Database db,
    Func<AppOptions> options,
    IUploadTrigger trigger,
    IDiagLog log)
{
    public Task<SendResult> EnqueueTextAsync(string text)
    {
        var trimmed = text?.Trim() ?? "";
        if (trimmed.Length == 0)
            return Task.FromResult(SendResult.Fail("内容为空"));
        if (System.Text.Encoding.UTF8.GetByteCount(text!) > Limits.MaxTextBytes)
            return Task.FromResult(SendResult.Fail("文字太长（上限 100 KiB）"));

        return EnqueueAsync(text!, []);
    }

    public Task<SendResult> EnqueueFilesAsync(IReadOnlyList<string> paths, string? text = null)
    {
        if (paths.Count == 0)
            return Task.FromResult(SendResult.Fail("没有文件"));
        if (paths.Count > Limits.MaxFiles)
            return Task.FromResult(SendResult.Fail($"一次最多 {Limits.MaxFiles} 个文件"));

        var maxBytes = options().MaxFileBytes;
        var infos = new List<(string Path, long Size, long MtimeMs)>(paths.Count);
        foreach (var p in paths)
        {
            try
            {
                var fi = new FileInfo(p);
                if (!fi.Exists)
                    return Task.FromResult(SendResult.Fail("文件不存在：" + p));
                if (fi.Length > maxBytes)
                    return Task.FromResult(SendResult.Fail($"单个文件不能超过 {FormatBytes(maxBytes)}"));
                // 可读性检查
                using var fs = fi.OpenRead();
                infos.Add((fi.FullName, fi.Length, new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds()));
            }
            catch (Exception)
            {
                return Task.FromResult(SendResult.Fail("文件不可读：" + p));
            }
        }

        return EnqueueAsync(string.IsNullOrWhiteSpace(text) ? null : text, infos);
    }

    private async Task<SendResult> EnqueueAsync(string? text, IReadOnlyList<(string Path, long Size, long MtimeMs)> files)
    {
        var opt = options();
        var id = Ulid.New();
        var month = UlidClock.MonthOf(id);
        var now = JobDao.Now();
        var bytesTotal = files.Sum(f => f.Size);

        var fileRows = files.Select((f, i) => new FileRow(
            FileId: Guid.NewGuid().ToString("D"),
            MessageId: id,
            Idx: i,
            Name: Path.GetFileName(f.Path),
            Size: f.Size,
            Mime: null,
            Sha256: null,
            Dir: Direction.Out,
            SourcePath: f.Path,
            SourceModifiedAt: f.MtimeMs,
            CachePath: null,
            State: FileStates.Pending)).ToList();

        db.Write(tx =>
        {
            new MessageDao(db).Insert(tx, new MessageRow(
                id, month, opt.DeviceId.ToString("D"), opt.DeviceName,
                now, string.IsNullOrWhiteSpace(text) ? null : text, Direction.Out, now));
            foreach (var f in fileRows)
                new FileDao(db).Insert(tx, f);
            new JobDao(db).Insert(tx, new JobRow(
                id, JobState.Queued, 0, null, 0, bytesTotal, null, null, now, now));
        });

        log.Info("send", $"enqueued type={(files.Count > 0 ? "files+text" : "text")} files={files.Count}");
        trigger.Wake();
        return SendResult.Success(id);
    }

    internal static string FormatBytes(long bytes)
    {
        return bytes switch
        {
            >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):0.#} GB",
            >= 1024L * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
            >= 1024 => $"{bytes / 1024.0:0.#} KB",
            _ => $"{bytes} B",
        };
    }
}

/// <summary>上传泵唤醒接口（Windows: UploadPump 自身；Android: WorkManager）。</summary>
public interface IUploadTrigger
{
    void Wake();
}

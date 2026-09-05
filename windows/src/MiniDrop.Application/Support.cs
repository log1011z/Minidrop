using System.Collections.Concurrent;
using MiniDrop.Domain;

namespace MiniDrop.Application;

/// <summary>应用配置（§11.1）。密码等凭证由平台层提供，不落库。</summary>
public sealed class AppOptions
{
    public Guid DeviceId { get; set; } = Guid.NewGuid();
    public string DeviceName { get; set; } = Environment.MachineName is { Length: > 0 } m && m.Length <= 32 ? m : "Windows";
    public string DownloadDir { get; set; } = "";
    public long MaxFileBytes { get; set; } = Limits.DefaultMaxFileBytes;
    public bool NotifyOnSendSuccess { get; set; } = true;
    public bool NotifyOnSendFailure { get; set; } = true;
}

/// <summary>诊断日志：不记 Authorization、密码、消息正文、文件内容与完整响应（§13）。</summary>
public interface IDiagLog
{
    void Info(string op, string detail);
    void Warn(string op, string detail);
    void Error(string op, string detail);
}

public sealed class FileDiagLog : IDiagLog
{
    private readonly string _dir;
    private readonly object _gate = new();

    public FileDiagLog(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(dir);
    }

    public static string DefaultDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MiniDrop", "logs");

    public void Info(string op, string detail) => Write("INFO", op, detail);
    public void Warn(string op, string detail) => Write("WARN", op, detail);
    public void Error(string op, string detail) => Write("ERROR", op, detail);

    private void Write(string level, string op, string detail)
    {
        lock (_gate)
        {
            try
            {
                var file = Path.Combine(_dir, $"minidrop-{DateTime.UtcNow:yyyyMMdd}.log");
                File.AppendAllText(file, $"{DateTime.UtcNow:yyyy-MM-dd'T'HH:mm:ss.fff'Z'} {level} {op} {detail}{Environment.NewLine}");
            }
            catch
            {
                // 日志失败不影响业务
            }
        }
    }
}

/// <summary>进行中传输注册表：删除消息时取消对应上传/下载。</summary>
public sealed class TransferRegistry
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _uploads = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _downloads = new();

    public CancellationToken RegisterUpload(string messageId)
    {
        var cts = new CancellationTokenSource();
        _uploads[messageId] = cts;
        return cts.Token;
    }

    public CancellationToken RegisterDownload(string fileId)
    {
        var cts = new CancellationTokenSource();
        _downloads[fileId] = cts;
        return cts.Token;
    }

    public void CompleteUpload(string messageId)
    {
        if (_uploads.TryRemove(messageId, out var cts)) cts.Dispose();
    }

    public void CompleteDownload(string fileId)
    {
        if (_downloads.TryRemove(fileId, out var cts)) cts.Dispose();
    }

    public void CancelMessage(string messageId)
    {
        if (_uploads.TryRemove(messageId, out var u)) { u.Cancel(); u.Dispose(); }
    }

    public void CancelDownload(string fileId)
    {
        if (_downloads.TryRemove(fileId, out var d)) { d.Cancel(); d.Dispose(); }
    }

    public bool IsDownloading(string fileId) => _downloads.ContainsKey(fileId);
}

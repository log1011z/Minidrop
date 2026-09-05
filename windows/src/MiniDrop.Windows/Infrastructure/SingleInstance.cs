using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace MiniDrop.Windows.Infrastructure;

/// <summary>
/// 单实例（§9.3）：命名互斥体判定首实例；第二实例经命名管道（长度前缀 JSON）
/// 把文件路径交给首实例后退出。管道消息总大小 1 MiB、最多 50 个文件、拒绝不存在路径。
/// </summary>
public sealed class SingleInstance : IDisposable
{
    public const string MutexName = "Local\\MiniDrop.SingleInstance";
    private const string PipeName = "MiniDrop.IPC";
    private const int MaxFiles = 50;
    private const int MaxMessageBytes = 1024 * 1024;

    private Mutex? _mutex;
    private CancellationTokenSource? _serverCts;
    private Task? _serverTask;

    public bool IsFirstInstance()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        return createdNew;
    }

    public static bool TryForwardToFirstInstance(IReadOnlyList<string> files, string? text)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(2000);
            var payload = JsonSerializer.SerializeToUtf8Bytes(new IpcMessage(files, text));
            if (payload.Length > MaxMessageBytes || files.Count > MaxFiles)
                return false;
            var len = BitConverter.GetBytes(payload.Length);
            client.Write(len);
            client.Write(payload);
            client.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>首实例启动管道服务；收到文件后回调（已 marshal 到 UI 线程前）。</summary>
    public void StartServer(Action<IReadOnlyList<string>, string?> onReceive)
    {
        _serverCts = new CancellationTokenSource();
        _serverTask = Task.Run(async () =>
        {
            while (!_serverCts.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In);
                    await server.WaitForConnectionAsync(_serverCts.Token).ConfigureAwait(false);
                    var lenBuf = await ReadExactAsync(server, 4, _serverCts.Token).ConfigureAwait(false);
                    var len = BitConverter.ToInt32(lenBuf);
                    if (len is <= 0 or > MaxMessageBytes)
                        continue;
                    var payload = await ReadExactAsync(server, len, _serverCts.Token).ConfigureAwait(false);
                    var msg = JsonSerializer.Deserialize<IpcMessage>(payload);
                    if (msg?.Files is not { Count: > 0 } files || files.Count > MaxFiles)
                        continue;
                    if (files.Any(f => !File.Exists(f)))
                        continue;
                    onReceive(files, msg.Text);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch
                {
                    // 单个连接失败不影响服务
                }
            }
        }, _serverCts.Token);
    }

    private static async Task<byte[]> ReadExactAsync(PipeStream stream, int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        var total = 0;
        while (total < count)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(total, count - total), ct).ConfigureAwait(false);
            if (n == 0)
                throw new IOException("pipe closed");
            total += n;
        }
        return buffer;
    }

    public void Dispose()
    {
        _serverCts?.Cancel();
        try { _serverTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _serverCts?.Dispose();
        if (_mutex is not null)
        {
            try { _mutex.ReleaseMutex(); } catch { }
            _mutex.Dispose();
        }
    }

    private sealed record IpcMessage(IReadOnlyList<string> Files, string? Text);
}

/// <summary>发送到 MiniDrop 右键菜单（SendTo.lnk）创建与恢复。</summary>
public static class SendToShortcut
{
    private static string LinkPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft", "Windows", "SendTo", "MiniDrop.lnk");

    public static void Ensure()
    {
        try
        {
            var link = LinkPath();
            if (File.Exists(link))
                return;
            Create();
        }
        catch
        {
            // 创建失败不影响主流程
        }
    }

    public static void Create()
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("WScript.Shell 不可用");
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic shortcut = shell.CreateShortcut(LinkPath());
        shortcut.TargetPath = Environment.ProcessPath ?? "";
        shortcut.Description = "把文件发送到 MiniDrop";
        shortcut.Save();
    }
}

using System.IO;
using System.IO.Pipes;
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
    private readonly string _pipeName;
    private readonly string _mutexName;

    public SingleInstance(string pipeName = PipeName, string mutexName = MutexName)
    {
        _pipeName = pipeName;
        _mutexName = mutexName;
    }

    private Mutex? _mutex;
    private CancellationTokenSource? _serverCts;
    private Task? _serverTask;

    public bool IsFirstInstance()
    {
        _mutex = new Mutex(initiallyOwned: true, _mutexName, out var createdNew);
        return createdNew;
    }

    public static async Task<bool> TryForwardToFirstInstanceAsync(
        IReadOnlyList<string> files, string? text, string pipeName = PipeName)
    {
        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(new IpcMessage(files, text));
            if (payload.Length > MaxMessageBytes || files.Count > MaxFiles)
                return false;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
            var len = BitConverter.GetBytes(payload.Length);
            await client.WriteAsync(len, timeout.Token).ConfigureAwait(false);
            await client.WriteAsync(payload, timeout.Token).ConfigureAwait(false);
            await client.FlushAsync(timeout.Token).ConfigureAwait(false);
            var acknowledgement = await ReadExactAsync(client, 1, timeout.Token).ConfigureAwait(false);
            return acknowledgement[0] == 1;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>首实例启动管道服务；收到文件后回调（已 marshal 到 UI 线程前）。</summary>
    public void StartServer(Func<IReadOnlyList<string>, string?, Task<bool>> onReceive)
    {
        _serverCts = new CancellationTokenSource();
        var serverToken = _serverCts.Token;
        _serverTask = Task.Run(async () =>
        {
            while (!serverToken.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.InOut,
                        1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(serverToken).ConfigureAwait(false);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(15));
                    var lenBuf = await ReadExactAsync(server, 4, timeout.Token).ConfigureAwait(false);
                    var len = BitConverter.ToInt32(lenBuf);
                    if (len is <= 0 or > MaxMessageBytes)
                        continue;
                    var payload = await ReadExactAsync(server, len, timeout.Token).ConfigureAwait(false);
                    var msg = JsonSerializer.Deserialize<IpcMessage>(payload);
                    if (msg?.Files is not { Count: > 0 } files || files.Count > MaxFiles)
                    {
                        await server.WriteAsync(new byte[] { 0 }, timeout.Token).ConfigureAwait(false);
                        continue;
                    }
                    // Let the send service report missing/unreadable files in the visible UI.
                    var accepted = await onReceive(files, msg.Text).ConfigureAwait(false);
                    await server.WriteAsync(new byte[] { accepted ? (byte)1 : (byte)0 }, timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    if (serverToken.IsCancellationRequested)
                        return;
                }
                catch
                {
                    // 单个连接失败不影响服务
                }
            }
        }, serverToken);
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
            // Refresh the executable target after moving or updating the app.
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
        shortcut.Arguments = "";
        shortcut.WorkingDirectory = Path.GetDirectoryName(Environment.ProcessPath) ?? "";
        shortcut.Description = "把文件发送到 MiniDrop";
        shortcut.Save();
    }
}

using MiniDrop.Application;
using MiniDrop.Domain;
using MiniDrop.Storage;
using MiniDrop.WebDav;

namespace MiniDrop.Tests;

/// <summary>测试装配：假服务器 + 真实 SQLite + 真实应用层。</summary>
public sealed class AppHarness : IDisposable
{
    private readonly string _tempDir;

    public FakeWebDavServer Server { get; } = new();
    public Database Db { get; }
    public AppOptions Options { get; } = new()
    {
        DeviceId = Guid.Parse(TestEnv.DeviceId),
        DeviceName = "Desktop",
        DownloadDir = Path.Combine(Path.GetTempPath(), "minidrop-test-" + Guid.NewGuid().ToString("N"), "downloads"),
    };
    public TransferRegistry Transfers { get; } = new();
    public UploadPump Pump { get; }
    public SendService Send { get; }
    public SyncCoordinator Sync { get; }
    public MaintenanceService Maintenance { get; }
    public DeleteService Delete { get; }
    public DownloadService Download { get; }
    public StartupRecovery Recovery { get; }
    public NullLog Log { get; } = new();

    public AppHarness()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "minidrop-test-" + Guid.NewGuid().ToString("N"));
        Db = new Database(Path.Combine(_tempDir, "app.db"));
        Func<WebDavClient> dav = () => new WebDavClient(new WebDavOptions
        {
            RootUrl = Server.RootUrl,
            Account = "u",
            PasswordProvider = () => "p",
            Timeout = TimeSpan.FromSeconds(10),
        });

        var mutex = new SyncMutex();
        Pump = new UploadPump(Db, dav, () => Options, Transfers, Log);
        Send = new SendService(Db, () => Options, Pump, Log);
        Sync = new SyncCoordinator(Db, dav, () => Options, mutex, Log);
        Maintenance = new MaintenanceService(Db, dav, () => Options, mutex, Sync, Log);
        Sync.Maintenance = Maintenance;
        Delete = new DeleteService(Db, dav, () => Options, Sync, Transfers, Log);
        Download = new DownloadService(Db, dav, () => Options, Transfers, Log);
        Recovery = new StartupRecovery(Db, () => Options, Log);

        // 远端根目录
        Server.MkCol("/MiniDrop/");
        Server.MkCol("/MiniDrop/items/");
        Server.MkCol("/MiniDrop/tombstones/");
        Server.MkCol("/MiniDrop/files/");
    }

    public string MakeTempFile(string name, byte[] content)
    {
        var dir = Path.Combine(Path.GetTempPath(), "minidrop-src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, content);
        var fi = new FileInfo(path);
        return path;
    }

    public static byte[] Bytes(int size, byte fill = 0xAB)
    {
        var b = new byte[size];
        Array.Fill(b, fill);
        return b;
    }

    public void SeedRemoteMessage(string month, string ulid, string? text = "seed", IReadOnlyList<MessageJson.DraftFile>? files = null, byte[]? rawOverride = null)
    {
        var draft = new MessageJson.Draft(ulid, TestEnv.DeviceId, "Desktop",
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), text, files);
        var json = rawOverride ?? MessageJson.Serialize(draft);
        Server.MkCol($"/MiniDrop/items/{month}/");
        Server.PutFile($"/MiniDrop/items/{month}/{ulid}.json", json);
    }

    public void SeedRemoteTombstone(string month, string ulid)
    {
        Server.MkCol($"/MiniDrop/tombstones/{month}/");
        Server.PutFile($"/MiniDrop/tombstones/{month}/{ulid}.json",
            MessageJson.SerializeTombstone(ulid, DateTimeOffset.UtcNow, TestEnv.DeviceId));
    }

    public JobDao Jobs => new(Db);
    public MessageDao Messages => new(Db);
    public FileDao Files => new(Db);
    public MetaDao Meta => new(Db);
    public RejectedDao Rejected => new(Db);

    public void Dispose()
    {
        Db.Dispose();
        Server.Dispose();
        try { Directory.Delete(_tempDir, true); } catch { }
        try { Directory.Delete(Options.DownloadDir, true); } catch { }
    }
}

public sealed class NullLog : IDiagLog
{
    public List<string> Entries { get; } = [];
    public void Info(string op, string detail) { Entries.Add($"INFO {op} {detail}"); Console.WriteLine($"[log] INFO {op} {detail}"); }
    public void Warn(string op, string detail) { Entries.Add($"WARN {op} {detail}"); Console.WriteLine($"[log] WARN {op} {detail}"); }
    public void Error(string op, string detail) { Entries.Add($"ERROR {op} {detail}"); Console.WriteLine($"[log] ERROR {op} {detail}"); }
}

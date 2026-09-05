using System.IO;
using System.Text.Json;

namespace MiniDrop.Windows.Infrastructure;

/// <summary>持久配置（§11.1）。应用密码不在此文件，存 Credential Manager。</summary>
public sealed class AppSettings
{
    public string RootUrl { get; set; } = "https://dav.jianguoyun.com/dav/MiniDrop/";
    public string Account { get; set; } = "";
    public Guid DeviceId { get; set; } = Guid.NewGuid();
    public string DeviceName { get; set; } = Environment.MachineName is { Length: > 0 } m && m.Length <= 32 ? m : "Windows";
    public string DownloadDir { get; set; } = DefaultDownloadDir();
    public long MaxFileBytes { get; set; } = MiniDrop.Domain.Limits.DefaultMaxFileBytes;
    public bool NotifyOnSendSuccess { get; set; } = true;
    public bool NotifyOnSendFailure { get; set; } = true;

    public static string DefaultDownloadDir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MiniDrop", "downloads");

    public static string FilePath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MiniDrop", "settings.json");

    public bool IsConfigured => Account.Length > 0;

    public static AppSettings Load()
    {
        try
        {
            var path = FilePath();
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path));
                if (loaded is not null)
                    return loaded;
            }
        }
        catch
        {
            // 配置损坏时回退默认值
        }
        return new AppSettings();
    }

    public void Save()
    {
        var path = FilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}

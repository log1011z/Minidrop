using System.IO;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using MiniDrop.Application;
using MiniDrop.Domain;
using MiniDrop.Storage;
using MiniDrop.WebDav;
using MiniDrop.Windows.Infrastructure;

namespace MiniDrop.Windows.Views;

/// <summary>连接变更通过测试后保存；本地偏好可离线保存。</summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _vm;

    public SettingsWindow()
    {
        InitializeComponent();
        _vm = new SettingsViewModel();
        DataContext = _vm;
        Loaded += (_, _) => PasswordBox.Password = _vm.LoadedPassword ?? "";
        RootBorder.SizeChanged += (_, _) => UpdateRootClip();
    }

    /// <summary>Clip child backgrounds to the same radius drawn by RootBorder.</summary>
    private void UpdateRootClip()
    {
        if (RootBorder.ActualWidth <= 0 || RootBorder.ActualHeight <= 0)
            return;

        RootBorder.Clip = new RectangleGeometry(
            new Rect(0, 0, RootBorder.ActualWidth, RootBorder.ActualHeight), 8, 8);
    }

    private void BrowseDownloadDir_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择下载目录",
        };
        if (dialog.ShowDialog(this) == true)
            _vm.DownloadDir = dialog.FolderName;
    }

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        await _vm.TestConnectionAsync(PasswordBox.Password);
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        DisableButtons();
        try
        {
            var (ok, error) = await _vm.SaveAsync(PasswordBox.Password, this);
            if (ok)
            {
                MessageBox.Show(this, "设置已保存", "MiniDrop", MessageBoxButton.OK, MessageBoxImage.Information);
                DialogResult = true;
            }
            else
            {
                MessageBox.Show(this, error ?? "保存失败", "MiniDrop", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            EnableButtons();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void CloseTitleButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { Close(); return; }
        try { DragMove(); } catch (InvalidOperationException) { }
    }

    private async void MaintainRemote_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "扫描 90 天前月份并清理远端记录？", "MiniDrop",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        DisableButtons();
        try
        {
            var outcome = await Services.Maintenance.MaintainAsync(CancellationToken.None);
            _vm.RefreshMaintenanceInfo();
            MessageBox.Show(this,
                outcome.ScanError ? "维护失败，请检查网络" : $"已清理 {outcome.Processed} 条远端记录",
                "MiniDrop", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally
        {
            EnableButtons();
        }
    }

    private void RetryRejected_Click(object sender, RoutedEventArgs e)
    {
        new RejectedDao(Services.Db).ClearQuarantine();
        _vm.RefreshMaintenanceInfo();
        MessageBox.Show(this, "已清除拒绝标记，下次刷新将重新尝试", "MiniDrop", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void DisableButtons() => IsEnabled = false;
    private void EnableButtons() => IsEnabled = true;
}

public partial class SettingsViewModel : ObservableObject
{
    [ObservableProperty] private string _rootUrl = "";
    [ObservableProperty] private string _account = "";
    [ObservableProperty] private string _deviceName = "";
    [ObservableProperty] private string _downloadDir = "";
    [ObservableProperty] private string _maxFileMb = "500";
    [ObservableProperty] private bool _notifyOnSendSuccess = true;
    [ObservableProperty] private string _testResult = "";
    [ObservableProperty] private string _maintenanceInfo = "";

    public string? LoadedPassword { get; private set; }

    private readonly string _originalAccount;
    private readonly string _originalRootUrl;
    private readonly string _originalPassword;
    private readonly Func<AppSettings> _loadSettings;
    private readonly Action<AppSettings> _saveSettings;

    public SettingsViewModel(Func<AppSettings>? loadSettings = null, Action<AppSettings>? saveSettings = null,
        Func<string?>? loadPassword = null)
    {
        _loadSettings = loadSettings ?? AppSettings.Load;
        _saveSettings = saveSettings ?? (settings => settings.Save());
        var s = _loadSettings();
        RootUrl = s.RootUrl;
        Account = s.Account;
        DeviceName = s.DeviceName;
        DownloadDir = s.DownloadDir;
        MaxFileMb = (s.MaxFileBytes / (1024 * 1024)).ToString();
        NotifyOnSendSuccess = s.NotifyOnSendSuccess;
        LoadedPassword = (loadPassword ?? CredentialManager.Load)();
        _originalAccount = s.Account;
        _originalRootUrl = s.RootUrl;
        _originalPassword = LoadedPassword ?? "";
        RefreshMaintenanceInfo();
    }

    public void RefreshMaintenanceInfo()
    {
        var meta = new MetaDao(Services.Db);
        var last = meta.Get(MetaDao.LastRemoteMaintenanceAt);
        MaintenanceInfo = last is null
            ? "尚未执行过远端维护；手动刷新后超过 7 天会自动执行一次受限维护"
            : $"最近一次远端维护：{last}（UTC）";
    }

    /// <summary>测试连接（§11.2）：PROPFIND 根 → 确保三个一级目录；不扫描消息。</summary>
    public async Task<(bool Ok, string? Error)> TestConnectionAsync(string password)
    {
        TestResult = "正在测试连接…";
        try
        {
            using var dav = new WebDavClient(new WebDavOptions
            {
                RootUrl = WebDavClient.NormalizeRootUrl(RootUrl),
                Account = Account,
                PasswordProvider = () => password,
                Timeout = TimeSpan.FromSeconds(15),
            }, App.Gate);
            var propfind = await dav.PropfindDirAsync("");
            if (propfind.Result.Status == DavStatus.AuthError)
            {
                TestResult = "测试失败：账号或应用密码不可用";
                return (false, "账号或应用密码不可用");
            }
            if (!propfind.Result.Ok && propfind.Result.Status != DavStatus.NotFound)
            {
                TestResult = $"测试失败：服务不可达（{propfind.Result.Status}）";
                return (false, "服务不可达");
            }
            foreach (var dir in new[] { RemotePaths.ItemsRoot, RemotePaths.TombstonesRoot, RemotePaths.FilesRoot })
            {
                var mk = await dav.MkColAsync(dir);
                if (!mk.Ok && mk.HttpStatusCode != 405)
                {
                    TestResult = $"测试失败：无法创建目录 {dir}（{mk.Status}）";
                    return (false, TestResult);
                }
            }
            TestResult = "测试成功 ✓";
            return (true, null);
        }
        catch (ArgumentException)
        {
            TestResult = "测试失败：只允许 HTTPS 根 URL";
            return (false, "只允许 HTTPS 根 URL");
        }
        catch (Exception)
        {
            TestResult = "测试失败：网络不可用";
            return (false, "网络不可用");
        }
    }

    public async Task<(bool Ok, string? Error)> SaveAsync(string password, Window owner)
    {
        var connectionChanged = Account.Trim() != _originalAccount
            || RootUrl.Trim() != _originalRootUrl || password != _originalPassword;
        if (connectionChanged)
        {
            var (ok, error) = await TestConnectionAsync(password);
            if (!ok) return (false, error);
        }

        var s = _loadSettings();

        // 账号或根 URL 变更 = 切换数据集（§11.3）
        var accountChanged = !string.Equals(Account.Trim(), _originalAccount, StringComparison.Ordinal);
        var urlChanged = !string.Equals(WebDavClient.NormalizeRootUrl(RootUrl), _originalRootUrl, StringComparison.Ordinal);
        if ((accountChanged || urlChanged) && (_originalAccount.Length > 0))
        {
            if (new JobDao(Services.Db).Count(JobState.Uploading) > 0)
                return (false, "有正在上传的任务，请等待完成后再切换账号");
            var confirm = MessageBox.Show(owner,
                "账号或根 URL 变更将切换到新的数据集：\n将清空本地消息索引、下载缓存、拒绝记录和历史游标（不会删除你的原始文件）。\n\n继续？",
                "MiniDrop", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes)
                return (false, "已取消");

            Services.Db.Write(tx =>
            {
                using var cmd = tx.Connection!.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM messages; DELETE FROM rejected_items; DELETE FROM sync_months; DELETE FROM meta;";
                cmd.ExecuteNonQuery();
            });
        }

        s.RootUrl = WebDavClient.NormalizeRootUrl(RootUrl);
        s.Account = Account.Trim();
        s.DeviceName = DeviceName.Trim();
        s.DownloadDir = DownloadDir;
        s.MaxFileBytes = Math.Max(1, long.TryParse(MaxFileMb, out var mb) ? mb : 500) * 1024 * 1024;
        s.NotifyOnSendSuccess = NotifyOnSendSuccess;
        Directory.CreateDirectory(s.DownloadDir);
        _saveSettings(s);

        if (connectionChanged)
        {
            CredentialManager.Save(password);
            new JobDao(Services.Db).RequeueAuthFailed();
            Services.Pump.Wake();
        }

        return (true, null);
    }
}

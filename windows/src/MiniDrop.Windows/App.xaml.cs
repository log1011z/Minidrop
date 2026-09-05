using System.Windows;
using H.NotifyIcon;
using MiniDrop.Application;
using MiniDrop.Storage;
using MiniDrop.WebDav;
using MiniDrop.Windows.Infrastructure;
using MiniDrop.Windows.ViewModels;
using MiniDrop.Windows.Views;

namespace MiniDrop.Windows;

using Application = System.Windows.Application;

public partial class App : Application
{
    private SingleInstance? _singleInstance;
    private TaskbarIcon? _tray;
    private byte[]? _trayIconData;
    private UploadPump? _pump;
    private Database? _db;
    private MainViewModel? _mainVm;

    public static AppSettings Settings { get; private set; } = null!;
    public static Database Db { get; private set; } = null!;
    public static IDiagLog Log { get; private set; } = null!;
    public static Func<AppOptions> OptionsFactory { get; private set; } = null!;

    private async void OnStartup(object sender, StartupEventArgs e)
    {
        Settings = AppSettings.Load();
        Log = new FileDiagLog(FileDiagLog.DefaultDir());
        var dataDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _db = new Database(System.IO.Path.Combine(dataDir, "MiniDrop", "minidrop.db"));
        Db = _db;

        OptionsFactory = () =>
        {
            // 每次读取最新配置：设置窗口保存后立即生效（避免内存快照过期）
            var s = AppSettings.Load();
            return new AppOptions
            {
                DeviceId = s.DeviceId,
                DeviceName = s.DeviceName,
                DownloadDir = s.DownloadDir,
                MaxFileBytes = s.MaxFileBytes,
                NotifyOnSendSuccess = s.NotifyOnSendSuccess,
                NotifyOnSendFailure = s.NotifyOnSendFailure,
            };
        };

        // 单实例：第二实例把文件转发给首实例后退出
        _singleInstance = new SingleInstance();
        if (!_singleInstance.IsFirstInstance())
        {
            if (e.Args.Length > 0)
            {
                SingleInstance.TryForwardToFirstInstance(e.Args, null);
            }
            Shutdown();
            return;
        }

        // 启动恢复与本地清理（零网络）
        var mutex = new SyncMutex();
        var transfers = new TransferRegistry();
        var recovery = new StartupRecovery(Db, OptionsFactory, Log);
        recovery.Recover();
        recovery.LocalCleanup(DateTimeOffset.UtcNow);

        WebDavClient DavFactory() => new(new WebDavOptions
        {
            // 每次读取最新配置：账号/根 URL 在设置保存后立即生效
            RootUrl = WebDavClient.NormalizeRootUrl(AppSettings.Load().RootUrl),
            Account = AppSettings.Load().Account,
            PasswordProvider = CredentialManager.Load,
        });

        _pump = new UploadPump(Db, DavFactory, OptionsFactory, transfers, Log);
        _pump.StartBackgroundLoop(((App)Current).GetAppCts().Token);

        var sync = new SyncCoordinator(Db, DavFactory, OptionsFactory, mutex, Log);
        var maintenance = new MaintenanceService(Db, DavFactory, OptionsFactory, mutex, sync, Log);
        sync.Maintenance = maintenance;

        var send = new SendService(Db, OptionsFactory, _pump, Log);
        var delete = new DeleteService(Db, DavFactory, OptionsFactory, sync, transfers, Log);
        var download = new DownloadService(Db, DavFactory, OptionsFactory, transfers, Log);

        Services.Configure(Db, OptionsFactory, send, sync, maintenance, delete, download, _pump, transfers);

        // 命名管道接收 SendTo/第二实例文件
        _singleInstance.StartServer((files, text) =>
            Dispatcher.BeginInvoke(() => _ = _mainVm?.SendFilesAsync(files, text)));

        // 托盘（代码创建的 TaskbarIcon 必须 ForceCreate 才会真正注册到通知区域）
        _tray = new TaskbarIcon
        {
            ToolTipText = "MiniDrop — 点击打开，右键更多",
        };
        try
        {
            var resource = Application.GetResourceStream(new Uri("pack://application:,,,/app.ico"))
                ?? throw new InvalidOperationException("app.ico resource missing");
            using var ms = new System.IO.MemoryStream();
            resource.Stream.CopyTo(ms);
            _trayIconData = ms.ToArray();
            _tray.Icon = new System.Drawing.Icon(new System.IO.MemoryStream(_trayIconData));
        }
        catch (Exception iconEx)
        {
            Log.Error("tray", $"icon load failed: {iconEx.GetType().Name}");
        }
        _tray.LeftClickCommand = new RelayCommand(ShowMainWindow);
        BuildTrayMenu();
        _tray.Visibility = Visibility.Visible;
        try
        {
            _tray.ForceCreate();
        }
        catch (Exception createEx)
        {
            Log.Error("tray", $"force create failed: {createEx.GetType().Name}");
        }
        Log.Info("tray", $"created={_tray.IsCreated}");

        // 主窗口（热键在窗口 OnSourceInitialized 注册）
        _mainVm = new MainViewModel();
        var window = new MainWindow(_mainVm) { App = this };
        MainWindow = window;
        SendToShortcut.Ensure();

        if (e.Args.Length > 0)
        {
            await _mainVm.SendFilesAsync(e.Args, null);
        }

        window.Show();
    }

    private CancellationTokenSource _appCts = new();
    private CancellationTokenSource GetAppCts() => _appCts;

    private void BuildTrayMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu();
        var open = new System.Windows.Controls.MenuItem { Header = "打开" };
        open.Click += (_, _) => ShowMainWindow();
        var refresh = new System.Windows.Controls.MenuItem { Header = "刷新" };
        refresh.Click += (_, _) => Dispatcher.BeginInvoke(async () => await _mainVm!.RefreshAsyncCommand.ExecuteAsync(null));
        var settings = new System.Windows.Controls.MenuItem { Header = "设置" };
        settings.Click += (_, _) => ShowSettings();
        var exit = new System.Windows.Controls.MenuItem { Header = "退出" };
        exit.Click += (_, _) => ExitApp();
        menu.Items.Add(open);
        menu.Items.Add(refresh);
        menu.Items.Add(settings);
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(exit);
        _tray!.ContextMenu = menu;
    }

    public void ShowMainWindow()
    {
        var w = MainWindow as MainWindow;
        if (w is null)
            return;
        w.ShowFromTray();
    }

    public void ToggleMainWindow()
    {
        var w = MainWindow as MainWindow;
        if (w is null)
            return;
        if (w.IsVisible && w.WindowState != WindowState.Minimized && w.IsActive)
            w.HideToTray();
        else
            w.ShowFromTray();
    }

    public void ShowSettings()
    {
        var settingsWindow = new SettingsWindow { Owner = MainWindow };
        settingsWindow.ShowDialog();
        Settings = AppSettings.Load(); // 保存后立即刷新内存快照
        _mainVm?.ReloadFromDb();
    }

    public void ShowTrayBalloon(string title, string text)
    {
        try
        {
            _tray?.ShowNotification(title, text);
        }
        catch
        {
            // 通知失败不影响业务
        }
    }

    public void ExitApp()
    {
        _appCts.Cancel();
        _tray?.Dispose();
        _singleInstance?.Dispose();
        _db?.Dispose();
        Shutdown();
    }
}

/// <summary>组合根服务定位（窗口/VM 共用）。</summary>
public static class Services
{
    public static Database Db { get; private set; } = null!;
    public static Func<AppOptions> Options { get; private set; } = null!;
    public static SendService Send { get; private set; } = null!;
    public static SyncCoordinator Sync { get; private set; } = null!;
    public static MaintenanceService Maintenance { get; private set; } = null!;
    public static DeleteService Delete { get; private set; } = null!;
    public static DownloadService Download { get; private set; } = null!;
    public static UploadPump Pump { get; private set; } = null!;
    public static TransferRegistry Transfers { get; private set; } = null!;

    public static void Configure(
        Database db, Func<AppOptions> options, SendService send, SyncCoordinator sync,
        MaintenanceService maintenance, DeleteService delete, DownloadService download,
        UploadPump pump, TransferRegistry transfers)
    {
        Db = db; Options = options; Send = send; Sync = sync;
        Maintenance = maintenance; Delete = delete; Download = download;
        Pump = pump; Transfers = transfers;
    }
}

/// <summary>极简 ICommand（避免引入额外依赖）。</summary>
public sealed class RelayCommand(Action execute) : System.Windows.Input.ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add => System.Windows.Input.CommandManager.RequerySuggested += value;
        remove => System.Windows.Input.CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => execute();
}

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiniDrop.Domain;
using MiniDrop.Storage;
using MiniDrop.Application;
using MiniDrop.Windows.Infrastructure;

namespace MiniDrop.Windows.ViewModels;

public sealed record DraftAttachment(string Path, bool IsTemporary = false)
{
    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>文件卡（§8.2 / §4.5：按下载状态显示）。</summary>
public partial class FileItemViewModel : ObservableObject
{
    [ObservableProperty] private string _fileId = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _sizeText = "";
    [ObservableProperty] private string _stateText = "";
    [ObservableProperty] private string _actionText = "下载";
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isImage;
    [ObservableProperty] private System.Windows.Media.Imaging.BitmapSource? _thumbnail;
    [ObservableProperty] private string _previewLabel = "点击加载图片";
    private string? _imageKey;

    public event Action<FileItemViewModel>? ActionRequested;
    public event Action<FileItemViewModel>? OpenRequested;
    public event Action<FileItemViewModel, string>? QuickActionRequested;

    public void RefreshState(FileRow row)
    {
        IsImage = ImagePreview.IsImage(row.Name, row.Mime);
        if (IsImage) UpdateThumbnail(row);
        StateText = row.State switch
        {
            FileStates.Remote => "",
            FileStates.Downloading => "正在下载…",
            FileStates.Cached => "已下载",
            FileStates.Failed => "下载失败，点击重试",
            _ => "",
        };
        ActionText = row.State switch
        {
            FileStates.Cached => "打开",
            FileStates.Failed => "重试",
            FileStates.Downloading => "…",
            _ => "下载并打开",
        };
        if (IsImage && row.State != FileStates.Downloading) ActionText = "预览";
        Busy = row.State == FileStates.Downloading;
        if (row.State != FileStates.Downloading)
            Progress = 0;
    }

    private async void UpdateThumbnail(FileRow row)
    {
        var path = new[] { row.CachePath, row.SourcePath }.FirstOrDefault(p => p is not null && File.Exists(p));
        var key = path is null ? null : path + File.GetLastWriteTimeUtc(path).Ticks;
        if (_imageKey == key) return;
        _imageKey = key;
        Thumbnail = null;
        PreviewLabel = path is null ? "点击加载图片" : "正在加载图片…";
        if (path is null) return;
        var bitmap = await Task.Run(() => ImagePreview.Load(path, 512));
        if (_imageKey != key) return;
        Thumbnail = bitmap;
        PreviewLabel = bitmap is null ? "无法生成缩略图，点击预览" : "";
    }

    [RelayCommand]
    private void Action() => ActionRequested?.Invoke(this);

    [RelayCommand]
    private void Open() => OpenRequested?.Invoke(this);

    [RelayCommand] private void CopyFile() => QuickActionRequested?.Invoke(this, "copy");
    [RelayCommand] private void RevealFile() => QuickActionRequested?.Invoke(this, "reveal");
    [RelayCommand] private void DownloadOnly() => QuickActionRequested?.Invoke(this, "download");
}

/// <summary>消息卡：状态推导（§4.5）。</summary>
public partial class MessageViewModel : ObservableObject
{
    public string Id { get; set; } = "";
    public string Month { get; set; } = "";
    [ObservableProperty] private string _timeText = "";
    [ObservableProperty] private string _deviceName = "";
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _errorText = "";
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private bool _showRetry;
    [ObservableProperty] private bool _hasText;
    [ObservableProperty] private bool _showTextToggle;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TextMaxHeight))]
    [NotifyPropertyChangedFor(nameof(TextToggleLabel))]
    private bool _textExpanded;
    public double TextMaxHeight => TextExpanded ? double.PositiveInfinity : 100;
    public string TextToggleLabel => TextExpanded ? "收起" : "展开全文";

    [RelayCommand]
    private void ToggleText() => TextExpanded = !TextExpanded;
    public ObservableCollection<FileItemViewModel> Files { get; } = [];

    public static MessageViewModel From(TimelineRow row)
    {
        var vm = new MessageViewModel
        {
            Id = row.Message.Id,
            Month = row.Message.RemoteMonth,
        };
        if (DateTimeOffset.TryParse(row.Message.CreatedAt, out var at))
            vm.TimeText = at.ToLocalTime().ToString("HH:mm");
        vm.DeviceName = row.Message.DeviceName;
        vm.Text = row.Message.Text ?? "";
        vm.HasText = vm.Text.Trim().Length > 0;
        vm.ApplyJob(row.Job);
        return vm;
    }

    public void ApplyJob(JobRow? job)
    {
        if (job is null)
        {
            StatusText = "";
            HasError = false;
            ShowRetry = false;
            return;
        }
        switch (job.State)
        {
            case JobState.Queued:
                StatusText = "排队中";
                HasError = false;
                ShowRetry = false;
                break;
            case JobState.Uploading:
                StatusText = job.BytesTotal > 0
                    ? $"正在上传 {job.BytesDone * 100 / job.BytesTotal}%"
                    : "正在上传…";
                HasError = false;
                ShowRetry = false;
                break;
            case JobState.RetryWait:
                StatusText = "等待重试";
                HasError = false;
                ShowRetry = false;
                break;
            case JobState.Failed:
                StatusText = "上传失败";
                ErrorText = job.ErrorCode switch
                {
                    ErrorCodes.Auth => "账号或应用密码不可用",
                    ErrorCodes.SourceMissing => "原文件不存在或已移动",
                    ErrorCodes.SourceChanged => "文件在发送后发生变化，请重新发送",
                    ErrorCodes.FileTooLarge => "单个文件超过上限",
                    ErrorCodes.Quota => "账户空间或流量不足",
                    _ => "网络不可用，稍后继续上传",
                };
                HasError = true;
                ShowRetry = true;
                break;
        }
    }
}

/// <summary>主窗口 VM：时间线、刷新、加载更早、发送、删除、下载。</summary>
public partial class MainViewModel : ObservableObject
{
    private const int LocalPageSize = 200;

    private readonly MessageDao _messages = new(Services.Db);
    private readonly FileDao _files = new(Services.Db);
    private readonly JobDao _jobs = new(Services.Db);
    private readonly MetaDao _meta = new(Services.Db);

    public ObservableCollection<MessageViewModel> Timeline { get; } = [];
    private readonly Dictionary<string, MessageViewModel> _byId = new();

    [ObservableProperty] private string _inputText = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private bool _sendBusy;
    [ObservableProperty] private bool _showLoadOlder = true;
    [ObservableProperty] private bool _showUnconfigured;

    private int _localOffset;
    private bool _localEndReached;
    private bool _remoteEndReached;
    private readonly Func<bool> _isConfigured;
    private readonly Action<string, string>? _localFileAction;
    public ObservableCollection<DraftAttachment> Attachments { get; } = [];

    public void AddAttachments(IEnumerable<string> paths, bool isTemporary = false)
    {
        foreach (var path in paths)
            if (!Attachments.Any(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase)))
                Attachments.Add(new DraftAttachment(path, isTemporary));
    }


    [RelayCommand]
    private void RemoveAttachment(DraftAttachment? attachment)
    {
        if (SendBusy || attachment is null || !Attachments.Remove(attachment)) return;
        if (attachment.IsTemporary)
            try { File.Delete(attachment.Path); } catch (IOException) { }
    }

    partial void OnSendBusyChanged(bool value) => SendCommand.NotifyCanExecuteChanged();

    public MainViewModel(Func<bool>? isConfigured = null, Action<string, string>? localFileAction = null)
    {
        _isConfigured = isConfigured ?? (() => AppSettings.Load().IsConfigured);
        _localFileAction = localFileAction;
        RefreshAsyncCommand = new AsyncRelayCommand(RefreshAsync, () => !Busy);
        LoadOlderAsyncCommand = new AsyncRelayCommand(LoadOlderAsync, () => !Busy);
        SendCommand = new AsyncRelayCommand(SendInputAsync, () => !SendBusy);
        DeleteCommand = new AsyncRelayCommand<string>(DeleteAsync);
        RetryCommand = new AsyncRelayCommand<string>(RetryAsync);
        ReloadFromDb();
    }

    public IAsyncRelayCommand RefreshAsyncCommand { get; }
    public IAsyncRelayCommand LoadOlderAsyncCommand { get; }
    public IAsyncRelayCommand SendCommand { get; }
    public IAsyncRelayCommand DeleteCommand { get; }
    public IAsyncRelayCommand RetryCommand { get; }

    // ---------- 本地时间线（打开只读本地，不请求 WebDAV） ----------

    public void ReloadFromDb()
    {
        // Keep existing cards and expansion state so refresh does not reset the scroll viewer.
        var limit = Math.Max(LocalPageSize, Timeline.Count);
        var rows = _messages.TimelinePage(limit, 0);
        var ids = rows.Select(row => row.Message.Id).ToHashSet();
        foreach (var vm in Timeline.Where(vm => !ids.Contains(vm.Id)).ToList())
        {
            Timeline.Remove(vm);
            _byId.Remove(vm.Id);
        }
        foreach (var row in rows)
            InsertTimeline(row);
        _localOffset = rows.Count;
        _localEndReached = rows.Count < limit;
        ShowLoadOlder = !_localEndReached || !_remoteEndReached;
        ShowUnconfigured = !_isConfigured();
    }

    public void LoadMoreLocal()
    {
        if (_localEndReached)
            return;
        AppendLocalPage();
    }

    private void AppendLocalPage()
    {
        var rows = _messages.TimelinePage(LocalPageSize, _localOffset);
        _localOffset += rows.Count;
        if (rows.Count < LocalPageSize)
            _localEndReached = true;
        foreach (var row in rows)
            InsertTimeline(row);
        ShowLoadOlder = !_localEndReached || !_remoteEndReached;
    }

    private void InsertTimeline(TimelineRow row)
    {
        if (_byId.TryGetValue(row.Message.Id, out var existing))
        {
            existing.ApplyJob(row.Job);
            SyncFiles(existing, row.Message.Id);
            return;
        }
        var vm = MessageViewModel.From(row);
        _byId.Add(row.Message.Id, vm);
        WireFileActions(vm);

        var index = 0;
        while (index < Timeline.Count && string.CompareOrdinal(Timeline[index].Id, vm.Id) > 0)
            index++;
        Timeline.Insert(index, vm);
        SyncFiles(vm, row.Message.Id);
    }

    private void SyncFiles(MessageViewModel vm, string messageId)
    {
        var rows = _files.GetByMessage(messageId);
        if (rows.Count == 0)
            return;
        var byFile = vm.Files.ToDictionary(f => f.FileId);
        foreach (var row in rows)
        {
            if (!byFile.TryGetValue(row.FileId, out var fvm))
            {
                fvm = new FileItemViewModel
                {
                    FileId = row.FileId,
                    Name = row.Name,
                    SizeText = FormatBytes(row.Size),
                };
                fvm.ActionRequested += FileActionAsync;
                fvm.OpenRequested += FileOpen;
                fvm.QuickActionRequested += FileQuickAction;
                vm.Files.Add(fvm);
            }
            fvm.RefreshState(row);
        }
    }

    private void WireFileActions(MessageViewModel _)
    {
        // ActionRequested 在 SyncFiles 中按文件挂接
    }

    // ---------- 用户动作 ----------

    public async Task SendInputAsync()
    {
        if (SendBusy) return;
        var text = InputText;
        var attachments = Attachments.ToArray();
        if (text.Trim().Length == 0 && attachments.Length == 0)
            return;
        SendBusy = true;
        try
        {
            var result = attachments.Length == 0
                ? await Services.Send.EnqueueTextAsync(text)
                : await Services.Send.EnqueueFilesAsync(attachments.Select(a => a.Path).ToArray(), text);
            if (!result.Ok)
            {
                StatusText = result.ErrorText ?? "发送失败";
                NotifySend(false);
                return;
            }
            if (InputText == text) InputText = "";
            foreach (var attachment in attachments) Attachments.Remove(attachment);
            StatusText = "已加入 MiniDrop";
            InsertTimeline(new TimelineRow(_messages.Get(result.MessageId!)!, _jobs.Get(result.MessageId!)));
            NotifySend(true);
        }
        catch (Exception)
        {
            StatusText = "发送失败，请重试";
            NotifySend(false);
        }
        finally
        {
            SendBusy = false;
        }
    }

    public async Task<bool> SendFilesAsync(IReadOnlyList<string> paths, string? text)
    {
        if (paths.Count == 0)
            return false;
        StatusText = $"正在加入 {paths.Count} 个文件…";
        var result = await Services.Send.EnqueueFilesAsync(paths, text);
        if (!result.Ok)
        {
            StatusText = result.ErrorText ?? "发送失败";
            NotifySend(false);
            return false;
        }
        InsertTimeline(new TimelineRow(_messages.Get(result.MessageId!)!, _jobs.Get(result.MessageId!)));
        StatusText = $"已加入 {paths.Count} 个文件";
        NotifySend(true);
        return true;
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (!EnsureConfigured())
            return;
        Busy = true;
        StatusText = "正在刷新…";
        try
        {
            var outcome = await Services.Sync.RefreshAsync(CancellationToken.None);
            _remoteEndReached = false;
            ReloadFromDb();
            StatusText = outcome.ScanError
                ? "刷新失败，请检查网络"
                : outcome.Failed > 0
                    ? $"已获取 {outcome.Added} 条，{outcome.Failed} 条暂时无法读取"
                    : outcome.Added > 0
                        ? $"已刷新，新增 {outcome.Added} 条"
                        : "已是最新";
        }
        catch (OperationCanceledException)
        {
            StatusText = "刷新已取消";
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    public async Task LoadOlderAsync()
    {
        if (!_localEndReached)
        {
            LoadMoreLocal();
            return;
        }
        if (_meta.Get(MetaDao.HistoryInitialized) != "1")
        {
            await RefreshAsync();
            return;
        }
        if (!EnsureConfigured())
            return;
        Busy = true;
        StatusText = "正在加载更早…";
        try
        {
            var outcome = await Services.Sync.LoadOlderAsync(CancellationToken.None);
            _remoteEndReached = outcome.NoMore && !outcome.ScanError && outcome.Failed == 0;
            ReloadFromDb();
            LoadMoreLocal();
            StatusText = outcome.ScanError
                ? "加载失败，请检查网络"
                : outcome.Added > 0
                    ? $"已加载 {outcome.Added} 条"
                    : outcome.NoMore ? "没有更早记录" : "已是本地最早";
        }
        finally
        {
            Busy = false;
        }
    }

    public async Task RetryAsync(string? messageId)
    {
        if (string.IsNullOrEmpty(messageId))
            return;
        if (_jobs.Requeue(messageId))
        {
            Services.Pump.Wake();
            StatusText = "已重新排队";
        }
        await Task.CompletedTask;
    }

    public async Task DeleteAsync(string? messageId)
    {
        if (string.IsNullOrEmpty(messageId))
            return;
        // 未发布的消息只删本机（无需网络/配置）；已发布的要走全端删除协议
        var unpublished = _jobs.Get(messageId) is not null;
        var text = unpublished
            ? "这条消息还没有上传成功，将从本机删除（不可恢复）。"
            : "删除这条消息？\n\n将从所有设备删除（不可恢复）。";
        if (!unpublished && !EnsureConfigured())
            return;
        var confirm = MessageBox.Show(text, "MiniDrop",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
            return;
        Busy = true;
        try
        {
            var result = await Services.Delete.DeleteAsync(messageId, CancellationToken.None);
            if (!result.Ok)
            {
                StatusText = result.ErrorText ?? "删除失败";
            }
            else
            {
                if (_byId.Remove(messageId))
                {
                    var vm = Timeline.FirstOrDefault(m => m.Id == messageId);
                    if (vm is not null) Timeline.Remove(vm);
                }
                StatusText = "已删除";
            }
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    public void CopyText(string text)
    {
        try { Clipboard.SetText(text); StatusText = "已复制"; }
        catch { /* 剪贴板被占用时忽略 */ }
    }

    private async void FileActionAsync(FileItemViewModel file)
        => await RunFileActionAsync(file, "open");

    private async void FileQuickAction(FileItemViewModel file, string action)
        => await RunFileActionAsync(file, action);

    public async Task RunFileActionAsync(FileItemViewModel file, string action)
    {
        if (file.Busy) return;
        var row = _files.Get(file.FileId);
        if (row is null)
            return;
        if (action == "open" && file.IsImage && row.SourcePath is { } source && File.Exists(source))
        {
            ShowImage(source, file.Name);
            return;
        }
        if (Services.Download.EnsureCacheConsistency(row))
        {
            row = _files.Get(file.FileId);
            if (row is null)
                return;
            file.RefreshState(row);
        }
        if (row.State == FileStates.Cached)
        {
            UseLocalFile(file, action);
            return;
        }
        if (row.State == FileStates.Downloading)
            return;

        if (!EnsureConfigured())
            return;
        file.Busy = true;
        try
        {
            var (step, error) = await Services.Download.DownloadAsync(file.FileId, CancellationToken.None);
            var fresh = _files.Get(file.FileId);
            if (fresh is not null)
                file.RefreshState(fresh);
            StatusText = error ?? (step == DownloadService.DownloadStep.Completed ? "下载完成" : "");
            if (step == DownloadService.DownloadStep.Completed) UseLocalFile(file, action);
        }
        finally
        {
            file.Busy = false;
        }
    }

    private void UseLocalFile(FileItemViewModel file, string action)
    {
        if (action == "download") return;
        if (_localFileAction is not null && _files.Get(file.FileId)?.CachePath is { } localPath)
        {
            _localFileAction(localPath, action);
            return;
        }
        if (action == "open")
        {
            if (file.IsImage && _files.Get(file.FileId)?.CachePath is { } imagePath) ShowImage(imagePath, file.Name);
            else FileOpen(file);
            return;
        }
        var path = _files.Get(file.FileId)?.CachePath;
        if (path is null) return;
        try
        {
            if (action == "copy")
            {
                var paths = new System.Collections.Specialized.StringCollection { path };
                Clipboard.SetFileDropList(paths);
                StatusText = "已复制文件，可直接粘贴";
            }
            else if (action == "reveal")
            {
                using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe", Arguments = $"/select,\"{path}\"", UseShellExecute = true,
                });
            }
        }
        catch { StatusText = "文件操作失败，请重试"; }
    }

    private static void ShowImage(string path, string name)
    {
        var window = new Views.ImagePreviewWindow(path, name) { Owner = System.Windows.Application.Current?.MainWindow };
        window.ShowDialog();
    }

    private void FileOpen(FileItemViewModel file)
    {
        var row = _files.Get(file.FileId);
        if (row is null)
            return;
        if (Services.Download.EnsureCacheConsistency(row))
        {
            var fresh = _files.Get(file.FileId);
            if (fresh is not null)
                file.RefreshState(fresh);
            StatusText = "本地文件已删除，请重新下载";
            return;
        }
        if (row.CachePath is null)
            return;
        try
        {
            using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = row.CachePath,
                UseShellExecute = true,
            });
        }
        catch
        {
            StatusText = "没有关联应用可以打开该文件";
        }
    }

    // ---------- 辅助 ----------

    private bool EnsureConfigured()
    {
        if (_isConfigured())
            return true;
        StatusText = "请先完成 WebDAV 设置";
        (System.Windows.Application.Current as App)?.ShowSettings();
        return false;
    }

    private void NotifySend(bool ok)
    {
        var settings = AppSettings.Load();
        if (ok ? !settings.NotifyOnSendSuccess : !settings.NotifyOnSendFailure)
            return;
        (System.Windows.Application.Current as App)?.ShowTrayBalloon(
            ok ? "MiniDrop" : "MiniDrop 发送失败",
            ok ? "已加入 MiniDrop" : StatusText);
    }

    internal static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):0.#} GB",
        >= 1024L * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        >= 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes} B",
    };

    /// <summary>本地状态轮询（仅读 SQLite，零网络）：刷新可见消息的上传/下载状态。</summary>
    public void PollJobStates(IEnumerable<MessageViewModel>? visibleMessages = null)
    {
        foreach (var vm in visibleMessages ?? Timeline)
        {
            var job = _jobs.Get(vm.Id);
            vm.ApplyJob(job);

            foreach (var f in vm.Files)
            {
                var row = _files.Get(f.FileId);
                if (row is not null)
                {
                    if (Services.Download.EnsureCacheConsistency(row))
                        row = _files.Get(f.FileId) ?? row;
                    fvm_Poll(f, row);
                }
            }
        }
    }

    private static void fvm_Poll(FileItemViewModel f, FileRow row) => f.RefreshState(row);
}

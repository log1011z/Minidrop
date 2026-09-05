using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MiniDrop.Windows.Infrastructure;
using MiniDrop.Windows.ViewModels;

namespace MiniDrop.Windows.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _pollTimer;
    private bool _hintedOnce;
    public required App App { get; init; }

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        try
        {
            Icon = System.Windows.Media.Imaging.BitmapFrame.Create(
                new Uri("pack://application:,,,/app.ico"));
        }
        catch { /* 图标加载失败不影响主流程 */ }

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _pollTimer.Tick += (_, _) => _vm.PollJobStates();
        _pollTimer.Start();

        Loaded += (_, _) => InputBox.Focus();
    }

    public IntPtr GetHwnd() => new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        InputBox.Focus();
    }

    public void HideToTray() => Hide();

    // ---------- 热键/关闭行为 ----------

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            var hotkey = new GlobalHotkey(GetHwnd());
            hotkey.Pressed += () => Dispatcher.BeginInvoke(() => App.ToggleMainWindow());
            Closed += (_, _) => hotkey.Dispose();
        }
        catch (InvalidOperationException)
        {
            // 热键被占用：不阻塞启动
        }
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // 关闭按钮 → 隐藏到托盘（§9.4）；首次关闭给出气泡提示
        e.Cancel = true;
        HideToTray();
        if (!_hintedOnce)
        {
            _hintedOnce = true;
            App.ShowTrayBalloon("MiniDrop 仍在后台运行",
                "点击托盘图标重新打开窗口；右键托盘图标 → 退出");
        }
    }

    private void AddFilesButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要投递的文件",
            Multiselect = true,
        };
        if (dialog.ShowDialog(this) == true && dialog.FileNames.Length > 0)
        {
            _ = _vm.SendFilesAsync(dialog.FileNames, null);
        }
    }

    // ContextMenu 不在可视树内，命令绑定取不到窗口 VM —— 改用 Click + DataContext
    private void DeleteMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MessageViewModel message)
        {
            _ = _vm.DeleteAsync(message.Id);
        }
    }

    private void CopyMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MessageViewModel message && message.HasText)
        {
            _vm.CopyText(message.Text);
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            HideToTray();
            e.Handled = true;
        }
        else if (e.Key == Key.F5)
        {
            _ = _vm.RefreshAsyncCommand.ExecuteAsync(null);
            e.Handled = true;
        }
    }

    // ---------- 输入行为 ----------

    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Enter 发送，Shift+Enter 换行（§9.5）
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            return;
        e.Handled = true;
        _ = _vm.SendCommand.ExecuteAsync(null);
    }

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.V && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            // 剪贴板含文件列表 → 发送文件；否则默认粘贴文本（§9.5）
            var files = ReadClipboardFiles();
            if (files is { Count: > 0 })
            {
                e.Handled = true;
                _ = _vm.SendFilesAsync(files.Cast<string>().ToList(), null);
            }
        }
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
            if (files.Length > 0)
                _ = _vm.SendFilesAsync(files.Cast<string>().ToList(), null);
            e.Handled = true;
        }
    }

    private static StringCollection? ReadClipboardFiles()
    {
        try
        {
            return Clipboard.ContainsFileDropList() ? Clipboard.GetFileDropList() : null;
        }
        catch
        {
            return null;
        }
    }

    private void TimelineList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // 滚到顶部继续读本地页（§9.4 本地分页）
        if (e.Delta < 0)
            return;
        var scroll = (ScrollViewer)FindScroll(TimelineList)!;
        if (scroll is not null && scroll.VerticalOffset == 0)
            _vm.LoadMoreLocal();
    }

    private static ScrollViewer? FindScroll(DependencyObject root)
    {
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(current);
            for (var i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(current, i);
                if (child is ScrollViewer sv)
                    return sv;
                queue.Enqueue(child);
            }
        }
        return null;
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => App.ShowSettings();
}

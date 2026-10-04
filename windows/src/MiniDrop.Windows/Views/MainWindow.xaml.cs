using System.Collections.Specialized;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MiniDrop.Windows.Infrastructure;
using MiniDrop.Windows.ViewModels;

namespace MiniDrop.Windows.Views;

public partial class MainWindow : Window
{
    private void MessageText_SizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateMessageText(sender);

    private void MessageText_Loaded(object sender, RoutedEventArgs e)
        => UpdateMessageText(sender);

    private void MessageText_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => UpdateMessageText(sender));

    private static void UpdateMessageText(object sender)
    {
        if (sender is not SelectableMessageText { DataContext: MessageViewModel message } text || text.ActualWidth <= 0)
            return;
        var fullText = new TextBlock
        {
            Text = message.Text,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = text.FontFamily,
            FontSize = text.FontSize,
            FontWeight = text.FontWeight,
            FontStyle = text.FontStyle,
            LineHeight = 20,
            MaxHeight = 120,
        };
        fullText.Measure(new Size(text.ActualWidth, double.PositiveInfinity));
        message.ShowTextToggle = fullText.DesiredSize.Height > 100;
    }

    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _pollTimer;
    private ScrollViewer? _timelineScroll;
    private double _scrollTarget;
    private long _lastScrollFrame;
    private bool _animatingScroll;
    private (MessageViewModel Message, double Top)? _scrollAnchor;
    private bool _hintedOnce;
    public required App App { get; init; }

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        InputBox.CommandBindings.Add(new CommandBinding(ApplicationCommands.Paste, PasteIntoDraft, CanPasteIntoDraft));
        _vm.Timeline.CollectionChanged += Timeline_CollectionChanged;
        try
        {
            Icon = System.Windows.Media.Imaging.BitmapFrame.Create(
                new Uri("pack://application:,,,/app.ico"));
        }
        catch { /* 图标加载失败不影响主流程 */ }

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _pollTimer.Tick += (_, _) =>
        {
            if (IsVisible && !_animatingScroll)
                _vm.PollJobStates(_vm.Timeline.Where(vm =>
                    TimelineList.ItemContainerGenerator.ContainerFromItem(vm) is FrameworkElement));
        };
        _pollTimer.Start();

        Loaded += (_, _) => InputBox.Focus();
        RootBorder.SizeChanged += (_, _) => UpdateRootClip();
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

    public void HideToTray()
    {
        StopScrollAnimation();
        Hide();
    }

    // ---------- 自定义标题栏 ----------

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => TryHideToTray();

    private void RefreshTitleButton_Click(object sender, RoutedEventArgs e) =>
        _ = _vm.RefreshAsyncCommand.ExecuteAsync(null);

    private void SettingsTitleButton_Click(object sender, RoutedEventArgs e) => App.ShowSettings();

    // ---------- 标题栏拖拽（CaptionHeight=0，按钮点击不经 chrome 命中测试） ----------

    private bool _dragStarted;

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            MaximizeButton_Click(sender, e);
            return;
        }
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            _dragStarted = true;
            try { DragMove(); } catch (InvalidOperationException) { }
        }
    }

    private void TitleBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => _dragStarted = false;

    private void TitleBar_MouseMove(object sender, MouseEventArgs e)
    {
        // DragMove 本身即拖拽；此处仅处理最大化状态下按住标题恢复窗口的拖动
        if (_dragStarted && WindowState == WindowState.Maximized && e.LeftButton == MouseButtonState.Pressed)
        {
            _dragStarted = false;
            WindowState = WindowState.Normal;
            try { DragMove(); } catch (InvalidOperationException) { }
        }
    }

    /// <summary>关闭/隐藏到托盘：首次给出气泡提示（§9.4）。</summary>
    private void TryHideToTray()
    {
        HideToTray();
        if (!_hintedOnce)
        {
            _hintedOnce = true;
            App.ShowTrayBalloon("MiniDrop 仍在后台运行",
                "点击托盘图标重新打开窗口；右键托盘图标 → 退出");
        }
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        var maximized = WindowState == WindowState.Maximized;
        RootBorder.CornerRadius = maximized ? new CornerRadius(0) : new CornerRadius(8);
        RootBorder.Margin = maximized ? new Thickness(6) : new Thickness(0);
        UpdateRootClip();
        if (MaxRestoreButton is not null)
            MaxRestoreButton.Content = maximized ? "\uE923" : "\uE922";
    }

    /// <summary>
    /// Border.CornerRadius only rounds the border's own drawing; it does not clip
    /// child backgrounds. Clip the complete visual tree so the transparent window
    /// has real rounded corners rather than square title-bar pixels at the corners.
    /// </summary>
    private void UpdateRootClip()
    {
        if (RootBorder is null || RootBorder.ActualWidth <= 0 || RootBorder.ActualHeight <= 0)
            return;

        var radius = WindowState == WindowState.Maximized ? 0d : 8d;
        RootBorder.Clip = new RectangleGeometry(
            new Rect(0, 0, RootBorder.ActualWidth, RootBorder.ActualHeight), radius, radius);
    }

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
        // Alt+F4 等系统关闭 → 隐藏到托盘（§9.4）
        e.Cancel = true;
        TryHideToTray();
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
            _vm.AddAttachments(dialog.FileNames);
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

    private void CanPasteIntoDraft(object sender, CanExecuteRoutedEventArgs e)
    {
        try { e.CanExecute = Clipboard.ContainsFileDropList() || Clipboard.ContainsImage() || Clipboard.ContainsText(); }
        catch { e.CanExecute = false; }
        e.Handled = true;
    }

    private void PasteIntoDraft(object sender, ExecutedRoutedEventArgs e)
    {
        e.Handled = true;
        try
        {
            if (Clipboard.GetDataObject() is { } data) PasteIntoDraft(data);
        }
        catch { _vm.StatusText = "粘贴失败，请重试"; }
    }

    public void PasteIntoDraft(IDataObject data, string? screenshotDirectory = null)
    {
        try
        {
            if (data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
            {
                _vm.AddAttachments(files);
            }
            else if (data.GetData(DataFormats.Bitmap) is System.Windows.Media.Imaging.BitmapSource image)
            {
                var directory = screenshotDirectory ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MiniDrop", "screenshots");
                _vm.AddAttachments([ClipboardImageStore.Save(image, directory)], isTemporary: true);
            }
            else if (data.GetData(DataFormats.UnicodeText) is string text)
            {
                InputBox.SelectedText = text;
                InputBox.CaretIndex = InputBox.SelectionStart + InputBox.SelectionLength;
                InputBox.SelectionLength = 0;
            }
        }
        catch { _vm.StatusText = "粘贴失败，请重试"; }
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
            if (files.Length > 0)
                _vm.AddAttachments(files);
            e.Handled = true;
        }
    }

    private void TimelineList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        _timelineScroll ??= FindScroll(TimelineList);
        if (_timelineScroll is not { ScrollableHeight: > 0 } scroll)
            return;
        var lines = SystemParameters.WheelScrollLines;
        if (lines == 0) return;
        var step = lines < 0 ? scroll.ViewportHeight : lines * 20d;
        _scrollTarget = Math.Clamp((_animatingScroll ? _scrollTarget : scroll.VerticalOffset)
            - e.Delta / 120d * step, 0, scroll.ScrollableHeight);
        e.Handled = true;
        if (!SystemParameters.ClientAreaAnimation)
        {
            scroll.ScrollToVerticalOffset(_scrollTarget);
            return;
        }
        if (!_animatingScroll)
        {
            _animatingScroll = true;
            _lastScrollFrame = Stopwatch.GetTimestamp();
            CompositionTarget.Rendering += AnimateScroll;
        }
    }

    private void AnimateScroll(object? sender, EventArgs e)
    {
        if (_timelineScroll is not { } scroll || !IsVisible)
        {
            StopScrollAnimation();
            return;
        }
        var now = Stopwatch.GetTimestamp();
        var elapsed = Math.Clamp((now - _lastScrollFrame) / (double)Stopwatch.Frequency, 0, 0.1);
        _lastScrollFrame = now;
        _scrollTarget = Math.Clamp(_scrollTarget, 0, scroll.ScrollableHeight);
        var remaining = _scrollTarget - scroll.VerticalOffset;
        if (Math.Abs(remaining) < 0.5)
        {
            scroll.ScrollToVerticalOffset(_scrollTarget);
            StopScrollAnimation();
            return;
        }
        scroll.ScrollToVerticalOffset(scroll.VerticalOffset + remaining * (1 - Math.Exp(-elapsed * 22)));
    }

    private void StopScrollAnimation()
    {
        CompositionTarget.Rendering -= AnimateScroll;
        _animatingScroll = false;
    }

    private void TimelineList_PreviewMouseDown(object sender, MouseButtonEventArgs e) => StopScrollAnimation();
    private void TimelineList_PreviewKeyDown(object sender, KeyEventArgs e) => StopScrollAnimation();

    private void TimelineList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // Read the next local page near the bottom; do not make a network request.
        if (e.OriginalSource is ScrollViewer scroll && scroll == FindScroll(TimelineList)
            && e.VerticalChange > 0 && scroll.ScrollableHeight - scroll.VerticalOffset < scroll.ViewportHeight)
            _vm.LoadMoreLocal();
    }

    private void Timeline_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _timelineScroll ??= FindScroll(TimelineList);
        if (_scrollAnchor is not null || _timelineScroll is not { VerticalOffset: > 1 } scroll)
            return;
        foreach (var message in _vm.Timeline)
        {
            if (TimelineList.ItemContainerGenerator.ContainerFromItem(message) is not FrameworkElement container)
                continue;
            var top = container.TranslatePoint(new Point(), scroll).Y;
            if (top + container.ActualHeight <= 0 || top >= scroll.ViewportHeight)
                continue;
            _scrollAnchor = (message, top);
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, RestoreScrollAnchor);
            break;
        }
    }

    private void RestoreScrollAnchor()
    {
        if (_scrollAnchor is not { } anchor || _timelineScroll is not { } scroll)
            return;
        _scrollAnchor = null;
        if (!_vm.Timeline.Contains(anchor.Message)) return;
        if (TimelineList.ItemContainerGenerator.ContainerFromItem(anchor.Message) is not FrameworkElement)
        {
            TimelineList.ScrollIntoView(anchor.Message);
            TimelineList.UpdateLayout();
        }
        if (TimelineList.ItemContainerGenerator.ContainerFromItem(anchor.Message) is FrameworkElement container)
        {
            var adjustment = container.TranslatePoint(new Point(), scroll).Y - anchor.Top;
            scroll.ScrollToVerticalOffset(scroll.VerticalOffset + adjustment);
            if (_animatingScroll) _scrollTarget += adjustment;
        }
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

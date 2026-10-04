using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MiniDrop.Application;
using MiniDrop.Domain;
using MiniDrop.Storage;
using MiniDrop.WebDav;
using MiniDrop.Windows;
using MiniDrop.Windows.ViewModels;
using MiniDrop.Windows.Views;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var artifactDir = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/ui");
        Directory.CreateDirectory(artifactDir);
        var tempDir = Path.Combine(Path.GetTempPath(), "MiniDrop.UiChecks." + Guid.NewGuid());
        var app = new App();
        app.InitializeComponent(); // Load the actual production styles; do not run application startup.
        using var db = new Database(Path.Combine(tempDir, "checks.db"));
        var options = new AppOptions { DeviceId = Guid.NewGuid(), DeviceName = "Desktop", DownloadDir = tempDir };
        var transfers = new TransferRegistry();
        var log = new NullLog();
        WebDavClient Dav() => new(new WebDavOptions { RootUrl = "https://example.invalid/MiniDrop/", Account = "", PasswordProvider = () => null });
        var mutex = new SyncMutex();
        var pump = new UploadPump(db, Dav, transfers, log);
        var sync = new SyncCoordinator(db, Dav, () => options, mutex, log);
        var maintenance = new MaintenanceService(db, Dav, () => options, mutex, sync, log);
        Services.Configure(db, () => options, new SendService(db, () => options, pump, log), sync, maintenance,
            new DeleteService(db, Dav, () => options, sync, transfers, log),
            new DownloadService(db, Dav, () => options, transfers, log), pump, transfers);
        var now = DateTimeOffset.UtcNow;
        db.Write(tx =>
        {
            for (var i = 0; i < 320; i++)
            {
                var at = now.AddSeconds(-i);
                var id = Ulid.NewAt(at.ToUnixTimeMilliseconds());
                var text = i switch
                {
                    0 => "随手记录，随时取用。\n拖选一段文字，或打开 https://example.com/docs?q=1。",
                    1 => string.Join("\n", Enumerable.Repeat("这是一段较长的分享文字，用来检查展开、收起和连续滚动时的布局。", 20)),
                    2 => "本周的资料已经整理好了。",
                    _ => string.Join("\n", Enumerable.Repeat($"第 {i + 1} 条记录 · 不同高度的卡片也能连续滚动。", 1 + i % 4)),
                };
                new MessageDao(db).Insert(tx, new MessageRow(id, UlidClock.MonthOf(id), options.DeviceId.ToString(),
                    i % 2 == 0 ? "我的电脑" : "我的手机", at.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"), text,
                    Direction.In, JobDao.Now()));
                if (i == 2)
                {
                    foreach (var (name, index) in new[] { ("项目资料与讨论纪要.pdf", 0), ("下周的工作计划.xlsx", 1) })
                        new FileDao(db).Insert(tx, new FileRow(Guid.NewGuid().ToString(), id, index, name,
                            128 * 1024 * (index + 1), null, null, Direction.In, null, null, null, FileStates.Remote));
                }
            }
        });
        var vm = new MainViewModel { ShowUnconfigured = false };
        var window = new MainWindow(vm) { App = app };
        var root = (FrameworkElement)window.Content;
        var list = (ListBox)window.FindName("TimelineList");
        void Layout(double width = 560, double height = 740)
        {
            root.Measure(new Size(width, height));
            root.Arrange(new Rect(0, 0, width, height));
            root.UpdateLayout();
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
            root.UpdateLayout();
        }
        void Capture(string name, int width, int height)
        {
            Layout(width, height);
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(artifactDir, name));
            encoder.Save(output);
        }
        Layout();
        var scroll = Descendants(list).OfType<ScrollViewer>().First();
        Check(VirtualizingPanel.GetScrollUnit(list) == ScrollUnit.Pixel, "Timeline must scroll by pixels");
        scroll.ScrollToVerticalOffset(37);
        Layout();
        Check(Math.Abs(scroll.VerticalOffset - 37) < 1, $"Expected 37 pixel offset, got {scroll.VerticalOffset}");
        var realized = vm.Timeline.Count(message => list.ItemContainerGenerator.ContainerFromItem(message) != null);
        Check(realized < 60, $"Expected virtualization, got {realized} realized cards");
        var existing = vm.Timeline[1];
        existing.TextExpanded = true;
        vm.ReloadFromDb();
        Check(ReferenceEquals(existing, vm.Timeline[1]) && existing.TextExpanded, "Refresh must reuse expanded cards");
        existing.TextExpanded = false;
        scroll.ScrollToVerticalOffset(600);
        Layout();
        var offset = scroll.VerticalOffset;
        vm.ReloadFromDb();
        Layout();
        Check(Math.Abs(scroll.VerticalOffset - offset) < 1, "Unchanged refresh must preserve scroll offset");
        var originalCard = vm.Timeline.First(message => list.ItemContainerGenerator.ContainerFromItem(message) is FrameworkElement element
            && element.TranslatePoint(new Point(), scroll).Y + element.ActualHeight > 0);
        var originalTop = ((FrameworkElement)list.ItemContainerGenerator.ContainerFromItem(originalCard)).TranslatePoint(new Point(), scroll).Y;
        var addedId = Ulid.NewAt(now.AddMinutes(1).ToUnixTimeMilliseconds());
        db.Write(tx => new MessageDao(db).Insert(tx, new MessageRow(addedId, UlidClock.MonthOf(addedId), options.DeviceId.ToString(),
            "新的投递", JobDao.Now(), "阅读旧消息时收到的新内容", Direction.In, JobDao.Now())));
        vm.ReloadFromDb();
        Layout();
        var updatedTop = ((FrameworkElement)list.ItemContainerGenerator.ContainerFromItem(originalCard)).TranslatePoint(new Point(), scroll).Y;
        Check(Math.Abs(updatedTop - originalTop) < 2, $"New shares must preserve the reading anchor: {originalTop} -> {updatedTop}");
        scroll.ScrollToTop();
        Layout();
        Capture("desktop.png", 560, 740);
        var selectable = Descendants(list).OfType<MiniDrop.Windows.Views.SelectableMessageText>()
            .First(body => body.MessageText.Contains("https://example.com"));
        var paragraph = (System.Windows.Documents.Paragraph)selectable.Document.Blocks.FirstBlock;
        Check(paragraph.Inlines.OfType<System.Windows.Documents.Hyperlink>().Single().NavigateUri.AbsoluteUri == "https://example.com/docs?q=1",
            "Message links must preserve the URL without trailing punctuation");
        var run = paragraph.Inlines.OfType<System.Windows.Documents.Run>().First(r => r.Text.Length >= 4);
        selectable.Selection.Select(run.ContentStart, run.ContentStart.GetPositionAtOffset(4)!);
        Check(selectable.Selection.Text == run.Text[..4] && selectable.IsReadOnly && selectable.IsDocumentEnabled,
            "Message text must allow partial selection without becoming editable");
        var links = MiniDrop.Windows.Views.SelectableMessageText.WebLinks("中文https://example.com/a?q=1&b=2，另见 (https://example.com/wiki/A_(B)). www.example.org!").ToArray();
        Check(links.Select(link => link.Url).SequenceEqual(new[] { "https://example.com/a?q=1&b=2", "https://example.com/wiki/A_(B)", "https://www.example.org" }),
            "Web links must handle Chinese punctuation, balanced parentheses, queries and www addresses");
        var bar = Descendants(list).OfType<System.Windows.Controls.Primitives.ScrollBar>().First(b => b.Orientation == Orientation.Vertical && b.ActualHeight > 0);
        var thumb = Descendants(bar).OfType<System.Windows.Controls.Primitives.Thumb>().First();
        Console.WriteLine($"Scrollbar: {bar.ActualWidth}x{bar.ActualHeight}; thumb: {thumb.ActualWidth}x{thumb.ActualHeight}; maximum={bar.Maximum}; viewport={bar.ViewportSize}");
        scroll.ScrollToVerticalOffset(290);
        Layout();
        Capture("desktop-files.png", 560, 740);
        scroll.ScrollToBottom();
        Layout();
        Check(vm.Timeline.Count == 321, "Scrolling near the bottom must append older local records");
        scroll.ScrollToTop();
        Layout();
        Capture("desktop-narrow.png", 400, 680);
        Check(Descendants(list).OfType<MiniDrop.Windows.Views.SelectableMessageText>().All(body => body.ActualWidth < list.ActualWidth - 60),
            "Selectable bodies must resize to the narrow viewport without clipping cards");
        Check(existing.ShowTextToggle, "Long messages must expose their expansion control");
        vm.Timeline.Clear();
        vm.ShowLoadOlder = false;
        Capture("desktop-empty.png", 560, 740);
        var bitmapDraft = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null,
            new byte[] { 0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255, 255, 255, 255, 255 }, 8);
        var pasteData = new DataObject(DataFormats.Bitmap, bitmapDraft);
        window.PasteIntoDraft(pasteData, tempDir);
        Check(vm.Attachments.Count == 1 && vm.Attachments[0].IsTemporary, "Pasting a screenshot must create a draft attachment");
        var screenshotPath = vm.Attachments[0].Path;
        var previewBitmap = MiniDrop.Windows.Infrastructure.ImagePreview.Load(screenshotPath, 512);
        Check(previewBitmap is { IsFrozen: true, PixelWidth: 2 }, "Local screenshot must decode into a reusable thumbnail");
        Check(MiniDrop.Windows.Infrastructure.ImagePreview.Load(Path.Combine(tempDir, "missing.png"), 512) is null,
            "Missing images must return a preview fallback");
        var imageVm = new FileItemViewModel { Name = "截图.png", FileId = "preview-test", Thumbnail = previewBitmap, IsImage = true, ActionText = "预览" };
        var previewMessage = new MessageViewModel { Id = "preview-card", DeviceName = "我的电脑", HasText = true, Text = "图片支持点击放大预览" };
        previewMessage.Files.Add(imageVm);
        vm.Timeline.Add(previewMessage);
        Capture("desktop-image-preview.png", 560, 740);
        Capture("desktop-image-preview-narrow.png", 400, 680);
        vm.Timeline.Clear();
        using (var input = File.OpenRead(screenshotPath))
        {
            var decoded = new PngBitmapDecoder(input, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            Check(decoded.Frames[0].PixelWidth == 2 && decoded.Frames[0].PixelHeight == 2, "Screenshot must remain a readable PNG");
        }
        vm.AddAttachments([Path.Combine(tempDir, "项目资料与讨论纪要.pdf")]);
        vm.InputText = "这张截图和附件是同一条消息的说明。";
        Capture("desktop-composer.png", 560, 740);
        Capture("desktop-composer-narrow.png", 400, 680);
        vm.RemoveAttachmentCommand.Execute(vm.Attachments[0]);
        Check(!File.Exists(screenshotPath), "Removing an unsent screenshot must remove its temporary file");
        vm.Attachments.Clear();
        var inputBox = (TextBox)window.FindName("InputBox");
        inputBox.Text = "before after";
        inputBox.Select(7, 5);
        window.PasteIntoDraft(new DataObject(DataFormats.UnicodeText, "pasted"));
        Check(inputBox.Text == "before pasted", "Text paste must replace the current selection");
        Console.WriteLine($"UI checks passed: pixel offsets, virtualization ({realized} cards), refresh state, reading anchor, long text, normal/narrow/empty layouts.");
        Console.WriteLine(artifactDir);
        ReliabilityChecks.Run();
        app.Shutdown();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private sealed class NullLog : IDiagLog
    {
        public void Info(string operation, string detail) { }
        public void Warn(string operation, string detail) { }
        public void Error(string operation, string detail) { }
    }
}

using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MiniDrop.Windows.Infrastructure;

namespace MiniDrop.Windows.Views;

public sealed class ImagePreviewWindow : Window
{
    public ImagePreviewWindow(string path, string name)
    {
        Title = name;
        Width = Math.Min(960, SystemParameters.WorkArea.Width);
        Height = Math.Min(760, SystemParameters.WorkArea.Height);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(24, 26, 30));
        var panel = new DockPanel { Margin = new Thickness(16) };
        var open = new Button { Content = "使用其他应用打开", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 0, 12) };
        open.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose(); }
            catch { open.Content = "无法打开，请检查关联应用"; }
        };
        DockPanel.SetDock(open, Dock.Top);
        panel.Children.Add(open);
        var container = new Grid();
        var status = new TextBlock { Text = "正在加载图片…", Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        container.Children.Add(status);
        panel.Children.Add(container);
        Content = panel;
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { Close(); e.Handled = true; } };
        Loaded += async (_, _) =>
        {
            var bitmap = await Task.Run(() => ImagePreview.Load(path, 4096));
            if (bitmap is null) status.Text = "无法预览此图片，可使用其他应用打开";
            else { container.Children.Clear(); container.Children.Add(new Image { Source = bitmap, Stretch = Stretch.Uniform }); }
        };
    }
}

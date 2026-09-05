using System.Runtime.InteropServices;

namespace MiniDrop.Windows.Infrastructure;

/// <summary>全局热键 Ctrl+Shift+D（§9.4）：显示/隐藏主窗口。</summary>
public sealed class GlobalHotkey : IDisposable
{
    private const int HotkeyId = 0xB00B;
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint VK_D = 0x44;

    private readonly IntPtr _hwnd;

    public GlobalHotkey(IntPtr hwnd)
    {
        _hwnd = hwnd;
        if (!RegisterHotKey(_hwnd, HotkeyId, MOD_CONTROL | MOD_SHIFT, VK_D))
            throw new InvalidOperationException("热键注册失败（可能被占用）");
    }

    public event Action? Pressed;

    public IntPtr HandleMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            Pressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public void Dispose() => UnregisterHotKey(_hwnd, HotkeyId);
}

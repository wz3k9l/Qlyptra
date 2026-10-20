using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;

namespace Qlyptra.Services;

public sealed class HotkeyManager : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const int HotkeyId = 0x51A;
    private readonly Window _window;
    private HwndSource? _source;
    private bool _registered;
    public event Action? Activated;

    public HotkeyManager(Window window) => _window = window;

    public void Initialize()
    {
        var helper = new WindowInteropHelper(_window);
        _source = HwndSource.FromHwnd(helper.Handle);
        _source?.AddHook(WndProc);
    }

    public bool Register(string description)
    {
        Unregister();
        if (!TryParse(description, out var modifiers, out var key)) return false;
        _registered = RegisterHotKey(new WindowInteropHelper(_window).Handle, HotkeyId, modifiers, (uint)KeyInterop.VirtualKeyFromKey(key));
        return _registered;
    }

    private static bool TryParse(string description, out uint modifiers, out Key key)
    {
        modifiers = 0; key = Key.None;
        var parts = description.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;
        foreach (var part in parts[..^1])
        {
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || part.Equals("Control", StringComparison.OrdinalIgnoreCase)) modifiers |= MOD_CONTROL;
            else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase)) modifiers |= MOD_ALT;
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase)) modifiers |= MOD_SHIFT;
            else if (part.Equals("Win", StringComparison.OrdinalIgnoreCase)) modifiers |= MOD_WIN;
            else return false;
        }
        if (modifiers == 0 || !Enum.TryParse(parts[^1], true, out key) || key is Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return false;
        return true;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            Activated?.Invoke(); handled = true;
        }
        return IntPtr.Zero;
    }

    public void Unregister()
    {
        if (!_registered) return;
        UnregisterHotKey(new WindowInteropHelper(_window).Handle, HotkeyId);
        _registered = false;
    }

    public void Dispose()
    {
        Unregister();
        _source?.RemoveHook(WndProc); _source = null;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}

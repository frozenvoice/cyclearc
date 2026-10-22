using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace CycleArc.Services;

/// <summary>
/// The optional system-wide shortcut that opens the detail popup. It uses RegisterHotKey on a
/// message-only window: no keyboard hook, and nothing is registered unless the person turns
/// the shortcut on. Registration fails cleanly when another program already owns the keys.
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    public const string DisplayText = "Ctrl + Alt + U";
    private const int WmHotkey = 0x0312;
    private const int HotkeyId = 0xCA01;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;
    private const uint VirtualKeyU = 0x55;
    private static readonly IntPtr MessageOnlyParent = new(-3);
    private HwndSource? _source;
    private bool _registered;

    public event Action? Pressed;

    public bool IsRegistered => _registered;

    /// <summary>Registers or releases the shortcut; returns false when it could not be registered.</summary>
    public bool Apply(bool enabled)
    {
        if (!enabled)
        {
            Release();
            return true;
        }
        if (_registered) return true;
        _source ??= CreateSource();
        _registered = RegisterHotKey(_source.Handle, HotkeyId, ModControl | ModAlt | ModNoRepeat, VirtualKeyU);
        return _registered;
    }

    private HwndSource CreateSource()
    {
        var source = new HwndSource(new HwndSourceParameters("CycleArc hotkey")
        {
            ParentWindow = MessageOnlyParent,
            Width = 0,
            Height = 0,
            WindowStyle = 0
        });
        source.AddHook(OnMessage);
        return source;
    }

    private IntPtr OnMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            Pressed?.Invoke();
        }
        return IntPtr.Zero;
    }

    private void Release()
    {
        if (_registered && _source is not null) UnregisterHotKey(_source.Handle, HotkeyId);
        _registered = false;
    }

    public void Dispose()
    {
        Release();
        _source?.Dispose();
        _source = null;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}

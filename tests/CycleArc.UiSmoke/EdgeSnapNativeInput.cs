using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace CycleArc.UiSmoke;

// Opt-in interactive checks only. Never used by the unattended/default gate.
internal static class EdgeSnapNativeInput
{
    public static void Drag(Window window, Point startScreenPixels, Point endScreenPixels, bool shift = false) =>
        Gesture(window, startScreenPixels, endScreenPixels, shift, move: true);

    public static void Click(Window window, Point screenPixels) =>
        Gesture(window, screenPixels, screenPixels, shift: false, move: false);

    private static void Gesture(Window window, Point start, Point end, bool shift, bool move)
    {
        if (!window.IsVisible) throw new InvalidOperationException("Native input needs a visible synthetic window.");
        if (!GetCursorPos(out var original)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var hwnd = new WindowInteropHelper(window).Handle;
        window.Activate();
        var operation = Task.Run(() =>
        {
            var pressed = false;
            var shifted = false;
            try
            {
                SetCursorPos((int)Math.Round(start.X), (int)Math.Round(start.Y));
                Thread.Sleep(80);
                SendMouse(0x0002); // LEFTDOWN
                pressed = true;
                Thread.Sleep(80);
                if (move)
                {
                    for (var step = 1; step <= 8; step++)
                    {
                        SetCursorPos((int)Math.Round(start.X + (end.X - start.X) * step / 8),
                            (int)Math.Round(start.Y + (end.Y - start.Y) * step / 8));
                        Thread.Sleep(25);
                    }
                }
                if (shift)
                {
                    SendKey(0x10, up: false);
                    shifted = true;
                    Thread.Sleep(40);
                }
                SendMouse(0x0004); // LEFTUP: modifiers are still held here.
                pressed = false;
                Thread.Sleep(80);
            }
            finally
            {
                if (pressed) SendMouse(0x0004, requireSuccess: false);
                if (shifted) SendKey(0x10, up: true, requireSuccess: false);
                // Cancel a stranded native move loop even if input was rejected by the desktop.
                PostMessage(hwnd, 0x001F, IntPtr.Zero, IntPtr.Zero); // WM_CANCELMODE
                SetCursorPos(original.X, original.Y);
            }
        });
        AccountUiChecks.PumpUntil(operation);
        for (var i = 0; i < 3; i++)
        {
            var frame = new DispatcherFrame();
            window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }

    private static void SendMouse(uint flags, bool requireSuccess = true) => Send(new Input
    {
        Type = 0, Data = new InputData { Mouse = new MouseInput { Flags = flags } }
    }, requireSuccess);

    private static void SendKey(ushort key, bool up, bool requireSuccess = true) => Send(new Input
    {
        Type = 1, Data = new InputData { Keyboard = new KeyboardInput { VirtualKey = key, Flags = up ? 2u : 0u } }
    }, requireSuccess);

    private static void Send(Input input, bool requireSuccess)
    {
        if (SendInput(1, [input], Marshal.SizeOf<Input>()) != 1 && requireSuccess)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Interactive desktop rejected synthetic mouse/keyboard input.");
    }

    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputData Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputData
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput
    {
        public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra;
    }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput
    {
        public ushort VirtualKey, Scan; public uint Flags, Time; public UIntPtr Extra;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}

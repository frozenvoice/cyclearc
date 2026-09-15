using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using CycleArc.Codex;
using CycleArc.Providers.Usage;
using CycleArc.Providers.Claude;

namespace CycleArc.UI;

public partial class FloatingWidget : Window
{
    public event Action<double, double>? Moved;
    public event Action? FlyoutRequested;
    public event Action? RefreshRequested;
    public event Action? ContextMenuRequested;

    private WidgetDragSession? _drag;
    private System.Windows.Media.Matrix _dragFromDevice;
    private bool _recoveringPosition;
    private bool _positionReady;
    private bool _applying;
    private bool _restoringPixels;
    private bool _closed;
    private (int X, int Y)? _savedPixels;

    public FloatingWidget()
    {
        InitializeComponent();
        ShowActivated = false;
        ContentRendered += (_, _) =>
        {
            if (_positionReady) return;
            _positionReady = true;
            RestoreAfterLayout();
        };
        Closed += (_, _) => _closed = true;
        SizeChanged += (_, _) => RecoverPosition();
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(() => RecoverPosition());
    }

    public void CloseWithoutActivation()
    {
        using var activation = new PassiveUpdate();
        Close();
    }

    private sealed class PassiveUpdate : IDisposable
    {
        private static readonly HookCallback Callback = FilterActivation;
        private IntPtr _hook;

        public PassiveUpdate()
        {
            // WPF's nested DPI resize omits NOACTIVATE. Veto activation only on this
            // UI thread during the synchronous widget update, including native creation.
            _hook = SetWindowsHookEx(5, Callback, IntPtr.Zero, GetCurrentThreadId()); // WH_CBT
            if (_hook == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }

        private static IntPtr FilterActivation(int code, IntPtr wParam, IntPtr lParam) =>
            code == 5 ? new IntPtr(1) : CallNextHookEx(IntPtr.Zero, code, wParam, lParam); // HCBT_ACTIVATE

        public void Dispose()
        {
            if (_hook == IntPtr.Zero) return;
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }

        private delegate IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr SetWindowsHookEx(int hook, HookCallback callback, IntPtr module, uint threadId);
        [DllImport("user32.dll")]
        private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();
    }

    public void Bind(CodexQuotaSnapshot snapshot)
    {
        Title = UiText.WidgetTitle;
        ProviderBadge.Provider = snapshot.Provider;
        CodexLabel.Text = CodexRingPresentation.From(snapshot).CenterSubLabel;
        CodexValue.Text = CycleArcPresentation.CompactText(snapshot)[(snapshot.Provider.Name().Length + 1)..];
        var showStatus = snapshot.Provider == UsageProviderId.Claude
            || snapshot.Status is not (CodexQuotaStatus.Available or CodexQuotaStatus.Refreshing);
        HistoryValue.Text = showStatus ? CycleArcPresentation.StatusLabel(snapshot) : "";
        HistoryValue.Visibility = showStatus ? Visibility.Visible : Visibility.Collapsed;
        var stale = ClaudeUsagePresentation.IsStale(snapshot);
        HistoryValue.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, stale ? "StaleBrush" : "MutedBrush");
        HistoryValue.FontWeight = stale ? FontWeights.SemiBold : FontWeights.Normal;
        CodexValue.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, stale ? "StaleBrush" : "TextBrush");
        ClaudeReceipt.Text = ClaudeUsagePresentation.LastReceivedText(snapshot);
        ClaudeReceipt.Visibility = snapshot.Provider == UsageProviderId.Claude && snapshot.LastSuccessfulRefresh is not null
            ? Visibility.Visible : Visibility.Collapsed;
        ToolTip = CycleArcPresentation.Tooltip(snapshot);
    }

    public void BindAccount(CodexAccountView? account)
    {
        Bind(account?.Snapshot ?? CodexQuotaSnapshot.Empty(CodexQuotaStatus.SignedOut));
        AccountName.Text = account?.DisplayName ?? "";
        AccountName.Visibility = account is not null ? Visibility.Visible : Visibility.Collapsed;
        if (account is not null) ToolTip = account.DisplayName + Environment.NewLine + ToolTip;
    }

    public void Apply(AppSettings settings)
    {
        using var activation = new PassiveUpdate();
        _applying = true;
        try
        {
            _savedPixels = settings.WidgetPixelLeft is int x && settings.WidgetPixelTop is int y ? (x, y) : null;
            Left = double.IsFinite(settings.WidgetLeft) ? settings.WidgetLeft : 40;
            Top = double.IsFinite(settings.WidgetTop) ? settings.WidgetTop : 40;
            Opacity = settings.WidgetOpacity;
            Topmost = settings.WidgetAlwaysOnTop;
            SetClickThrough(settings.WidgetClickThrough);
            Cursor = settings.WidgetClickThrough ? System.Windows.Input.Cursors.Arrow : System.Windows.Input.Cursors.SizeAll;
            if (_positionReady) RestoreAfterLayout();
        }
        finally { _applying = false; }
        RecoverPosition();
    }

    public void RecoverPosition(IReadOnlyList<ScreenRect>? workAreas = null)
    {
        if (_recoveringPosition || _drag is not null || _applying || _restoringPixels || _closed) return;
        // A minimized window's native rectangle is not a position to save or clamp.
        if (WindowState != WindowState.Normal || IsIconic(new WindowInteropHelper(this).Handle)) return;
        if (workAreas is null)
        {
            if (!_positionReady) return;
            RecoverPhysicalPosition();
            return;
        }
        _recoveringPosition = true;
        try
        {
            var content = (FrameworkElement)Content;
            content.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            var position = WidgetPlacement.Recover(Left, Top, content.DesiredSize.Width, content.DesiredSize.Height,
                workAreas ?? DesktopWorkAreas.For(this));
            if (position.Left == Left && position.Top == Top) return;
            Left = position.Left;
            Top = position.Top;
            Moved?.Invoke(Left, Top);
        }
        finally { _recoveringPosition = false; }
    }

    public bool EnsureVisible(bool alwaysOnTop)
    {
        if (_closed) return false;
        var repaired = false;
        if (WindowState != WindowState.Normal)
        {
            using var activation = new PassiveUpdate();
            WindowState = WindowState.Normal;
            repaired = true;
        }
        if (!IsVisible) { Show(); repaired = true; }
        var hwnd = new WindowInteropHelper(this).Handle;
        if (IsIconic(hwnd)) { ShowWindow(hwnd, 4); repaired = true; } // SHOWNOACTIVATE
        var nativeTopmost = (GetWindowLong(hwnd, GwlExstyle) & WsExTopmost) != 0;
        var displaced = alwaysOnTop && IsDisplacedByOrdinaryWindow(hwnd);
        if (!IsWindowVisible(hwnd) || nativeTopmost != alwaysOnTop || displaced || repaired)
        {
            // Native flags can disagree with WPF's cached Visibility/Topmost properties.
            SetWindowPos(hwnd, alwaysOnTop ? new IntPtr(-1) : new IntPtr(-2), 0, 0, 0, 0,
                SwpNoSize | SwpNoMove | SwpNoActivate | SwpShowWindow | SwpNoOwnerZOrder);
            repaired = true;
        }
        RecoverPosition();
        if (repaired) InvalidateVisual();
        return repaired;
    }

    public (int X, int Y)? PixelPosition
    {
        get
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            return hwnd != IntPtr.Zero && GetWindowRect(hwnd, out var rect) ? (rect.Left, rect.Top) : null;
        }
    }

    private void RestoreAfterLayout()
    {
        // Moving onto a different DPI monitor can temporarily retain the old pixel size.
        // Never clamp/persist that intermediate rectangle near a work-area edge.
        _restoringPixels = true;
        RestorePixels();
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, new Action(() =>
        {
            if (_closed) return;
            RestorePixels();
            _restoringPixels = false;
            RecoverPosition();
            Moved?.Invoke(Left, Top);
        }));
    }

    private void RestorePixels()
    {
        if (_savedPixels is { } pixels) SetPixelPosition(pixels.X, pixels.Y);
    }

    private void SetPixelPosition(int x, int y)
    {
        using var activation = new PassiveUpdate();
        SetWindowPos(new WindowInteropHelper(this).Handle, IntPtr.Zero, x, y, 0, 0,
            0x0001 | 0x0004 | 0x0010); // NOSIZE | NOZORDER | NOACTIVATE
    }

    private void RecoverPhysicalPosition()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (!GetWindowRect(hwnd, out var rect)) return;
        var areas = System.Windows.Forms.Screen.AllScreens.OrderByDescending(x => x.Primary)
            .Select(x => new ScreenRect(x.WorkingArea.X, x.WorkingArea.Y, x.WorkingArea.Width, x.WorkingArea.Height)).ToArray();
        var next = WidgetPlacement.Recover(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top, areas);
        if ((int)next.Left == rect.Left && (int)next.Top == rect.Top) return;
        _recoveringPosition = true;
        try
        {
            SetPixelPosition((int)next.Left, (int)next.Top);
            Moved?.Invoke(Left, Top);
        }
        finally { _recoveringPosition = false; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);

    private System.Windows.Point PointerOnScreen(System.Windows.Input.MouseEventArgs e) =>
        _dragFromDevice.Transform(PointToScreen(e.GetPosition(this)));

    private void OnPreviewLeftDown(object sender, MouseButtonEventArgs e)
    {
        _dragFromDevice = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice
            ?? System.Windows.Media.Matrix.Identity;
        var pointer = PointerOnScreen(e);
        BeginDrag(pointer);
        e.Handled = true;
    }

    private void OnPreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_drag is null) return;
        if (e.LeftButton != MouseButtonState.Pressed) { FinishDrag(false); return; }
        var pointer = PointerOnScreen(e);
        UpdateDragPosition(pointer);
        e.Handled = true;
    }

    private void OnPreviewLeftUp(object sender, MouseButtonEventArgs e)
    {
        if (_drag is null) return;
        var pointer = PointerOnScreen(e);
        UpdateDragPosition(pointer);
        FinishDrag(true);
        e.Handled = true;
    }

    private void OnLostMouseCapture(object sender, System.Windows.Input.MouseEventArgs e) => FinishDrag(false);

    private void BeginDrag(System.Windows.Point pointer)
    {
        _drag = new WidgetDragSession(Left, Top, pointer.X, pointer.Y);
        if (!CaptureMouse()) _drag = null;
    }

    private void UpdateDragPosition(System.Windows.Point pointer)
    {
        if (_drag is null) return;
        var position = _drag.Move(pointer.X, pointer.Y);
        if (_drag.IsDragging) { Left = position.Left; Top = position.Top; }
    }

    private void FinishDrag(bool allowClick)
    {
        var gesture = _drag;
        _drag = null;
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (gesture?.IsDragging == true) Moved?.Invoke(Left, Top);
        else if (gesture is not null && allowClick) FlyoutRequested?.Invoke();
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle)
        {
            RefreshRequested?.Invoke();
            e.Handled = true;
            return;
        }

        if (e.ChangedButton == MouseButton.Right)
        {
            ContextMenuRequested?.Invoke();
            e.Handled = true;
        }
    }

    private void SetClickThrough(bool enabled)
    {
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        var style = GetWindowLong(hwnd, GwlExstyle);
        style = enabled ? style | WsExTransparent : style & ~WsExTransparent;
        SetWindowLong(hwnd, GwlExstyle, style);
    }

    private const int GwlExstyle = -20;
    private const int WsExTopmost = 0x00000008;
    private const int WsExTransparent = 0x00000020;
    private const uint GwHwndPrev = 3;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const int DwmwaCloaked = 14;

    private static bool IsDisplacedByOrdinaryWindow(IntPtr hwnd)
    {
        // WS_EX_TOPMOST can remain set after a display/shell transition while the
        // native z-order leaves the widget below an ordinary visible window. Only
        // repair that concrete divergence. When all visible predecessors are
        // topmost, leave their existing order unchanged.
        var inspected = 0;
        for (var above = GetWindow(hwnd, GwHwndPrev); above != IntPtr.Zero && inspected++ < 1024;
             above = GetWindow(above, GwHwndPrev))
        {
            if (!IsWindowVisible(above) || IsIconic(above) || IsCloaked(above)) continue;
            if ((GetWindowLong(above, GwlExstyle) & WsExTopmost) == 0) return true;
        }

        return false;
    }

    private static bool IsCloaked(IntPtr hwnd)
    {
        try
        {
            return DwmGetWindowAttribute(hwnd, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
}

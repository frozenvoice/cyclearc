using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using CodexMeter.Codex;

namespace CodexMeter.UI;

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

    public void Bind(CodexQuotaSnapshot snapshot)
    {
        Title = UiText.WidgetTitle;
        CodexLabel.Text = CodexRingPresentation.From(snapshot).CenterSubLabel;
        CodexValue.Text = CodexMeterPresentation.CompactText(snapshot).Replace("Codex ", "", StringComparison.Ordinal);
        var needsAttention = snapshot.Status is not (CodexQuotaStatus.Available or CodexQuotaStatus.Refreshing);
        HistoryValue.Text = needsAttention ? CodexMeterPresentation.StatusLabel(snapshot) : "";
        HistoryValue.Visibility = needsAttention ? Visibility.Visible : Visibility.Collapsed;
        ToolTip = CodexMeterPresentation.Tooltip(snapshot);
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
    private const int WsExTransparent = 0x00000020;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
}

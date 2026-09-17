using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using CycleArc.Codex;
using CycleArc.Providers.Usage;

namespace CycleArc.UI;

public partial class FloatingWidget : Window
{
    public event Action<double, double>? Moved;
    public event Action? FlyoutRequested;
    public event Action? RefreshRequested;
    public event Action? ContextMenuRequested;
    public event Action<string>? AccountSelected;
    public event Action? SettingsRequested;
    public event Action? CloseRequested;

    private WidgetDragSession? _drag;
    private System.Windows.Media.Matrix _dragFromDevice;
    private bool _recoveringPosition;
    private bool _positionReady;
    private bool _applying;
    private bool _restoringPixels;
    private bool _closed;
    private (int X, int Y)? _savedPixels;
    private readonly List<WidgetAccountModuleView> _modules = [];
    private string? _pressedProfileId;
    private (int Count, int Columns) _shape = (-1, -1);

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

    /// <summary>The layout the last bind produced. Columns, rows and the wrapped size in DIP.</summary>
    public WidgetGridLayout? LastLayout { get; private set; }

    /// <summary>The account modules currently shown, in account-management order.</summary>
    public IReadOnlyList<WidgetAccountModuleView> Modules => _modules;

    /// <summary>
    /// Shows every account the overview says may be displayed, in its existing order, each with
    /// its own ring and every period its provider reported. Rebinding rewrites text in place;
    /// the module grid is rebuilt only when the account count or the column count changes.
    /// </summary>
    public void BindAccounts(IReadOnlyList<CodexAccountView> accounts, string selectedId,
        UsagePeriodPreference preference = UsagePeriodPreference.Auto,
        IReadOnlyList<ScreenRect>? workAreas = null, DateTimeOffset? now = null)
    {
        Title = UiText.WidgetTitle;
        var models = WidgetAccountModel.All(accounts, selectedId, preference, now);
        AccountCountText.Text = UiText.WidgetAccountsConnected(models.Count);
        ToolTip = UiText.ProductName + " · " + AccountCountText.Text;

        EmptyStateText.Text = UiText.T("No account to show. Connect one in Manage accounts.",
            "표시할 계정이 없습니다. 계정 관리에서 연결하세요.");
        EmptyStateText.Visibility = models.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ModuleScroller.Visibility = models.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        EnsureModules(models.Count);
        for (var i = 0; i < models.Count; i++) _modules[i].Bind(accounts[i], models[i]);
        LastLayout = ArrangeModules(models.Count, workAreas);
    }

    private void EnsureModules(int count)
    {
        while (_modules.Count < count) _modules.Add(new WidgetAccountModuleView());
        if (_modules.Count > count) _modules.RemoveRange(count, _modules.Count - count);
    }

    private WidgetGridLayout ArrangeModules(int count, IReadOnlyList<ScreenRect>? workAreas)
    {
        var area = CurrentWorkArea(workAreas);
        WidgetHeader.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        var headerHeight = WidgetHeader.DesiredSize.Height + WidgetHeader.Margin.Bottom;
        var heights = new double[_modules.Count];
        for (var i = 0; i < _modules.Count; i++)
        {
            _modules[i].Measure(new System.Windows.Size(WidgetGridLayout.ModuleWidth, double.PositiveInfinity));
            heights[i] = _modules[i].DesiredSize.Height;
        }
        var layout = WidgetGridLayout.For(count, headerHeight, heights, area,
            System.Windows.SystemParameters.VerticalScrollBarWidth);
        if (_shape != (count, layout.Columns)) BuildGrid(count, layout.Columns);
        _shape = (count, layout.Columns);
        // Only a grid that no longer fits the monitor scrolls; the panel itself never shrinks text.
        // A grid that fits stays unconstrained, so a sub-pixel rounding difference cannot
        // introduce a scrollbar the layout did not ask for.
        ModuleScroller.MaxHeight = layout.Scrolls ? layout.ModuleViewportHeight : double.PositiveInfinity;
        return layout;
    }

    private ScreenRect CurrentWorkArea(IReadOnlyList<ScreenRect>? workAreas)
    {
        var areas = workAreas ?? DesktopWorkAreas.For(this);
        var width = ActualWidth > 0 ? ActualWidth : WidgetGridLayout.ModuleWidth;
        var height = ActualHeight > 0 ? ActualHeight : WidgetGridLayout.ModuleWidth;
        return WidgetPlacement.AreaFor(Left, Top, width, height, areas);
    }

    private void BuildGrid(int count, int columns)
    {
        ModuleHost.Children.Clear();
        ModuleHost.ColumnDefinitions.Clear();
        ModuleHost.RowDefinitions.Clear();
        if (count == 0) return;
        var rows = (int)Math.Ceiling(count / (double)columns);
        for (var column = 0; column < columns; column++)
        {
            if (column > 0) ModuleHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ModuleHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }
        for (var row = 0; row < rows; row++)
        {
            if (row > 0) ModuleHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            ModuleHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
        // Reading order stays account order: left to right, then down to the next row.
        for (var index = 0; index < count; index++)
        {
            var column = index % columns;
            var row = index / columns;
            var module = _modules[index];
            Grid.SetColumn(module, column * 2);
            Grid.SetRow(module, row * 2);
            ModuleHost.Children.Add(module);
            if (column > 0) ModuleHost.Children.Add(Separator(vertical: true, (column * 2) - 1, row * 2, 1));
        }
        for (var row = 1; row < rows; row++)
            ModuleHost.Children.Add(Separator(vertical: false, 0, (row * 2) - 1, ModuleHost.ColumnDefinitions.Count));
    }

    private static Border Separator(bool vertical, int column, int row, int span)
    {
        var line = new Border
        {
            Tag = vertical ? "WidgetModuleSeparator" : "WidgetRowSeparator",
            Width = vertical ? WidgetGridLayout.SeparatorThickness : double.NaN,
            Height = vertical ? double.NaN : WidgetGridLayout.SeparatorThickness,
            // A row separator must measure exactly its own thickness, or the rendered grid grows
            // past the height the layout reserved for it. The modules' own padding is the gap.
            Margin = vertical ? new Thickness(0, 6, 0, 6) : new Thickness(6, 0, 6, 0),
            HorizontalAlignment = vertical ? HorizontalAlignment.Center : HorizontalAlignment.Stretch,
            VerticalAlignment = vertical ? VerticalAlignment.Stretch : VerticalAlignment.Center
        };
        line.SetResourceReference(BackgroundProperty, "LineBrush");
        Grid.SetColumn(line, column);
        Grid.SetRow(line, row);
        Grid.SetColumnSpan(line, span);
        return line;
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        SettingsRequested?.Invoke();
    }

    // Hides the widget surface only. Turning the setting off is the app's job; this never exits.
    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        CloseRequested?.Invoke();
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
        // Header buttons keep their click. Scrollbar chrome (thumb, track, buttons) must
        // scroll the module grid instead of moving the window or selecting an account.
        if (IsChromeButton(e.OriginalSource as DependencyObject)
            || IsScrollChrome(e.OriginalSource as DependencyObject)) return;
        _dragFromDevice = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice
            ?? System.Windows.Media.Matrix.Identity;
        _pressedProfileId = ModuleAt(e.OriginalSource as DependencyObject)?.ProfileId;
        var pointer = PointerOnScreen(e);
        BeginDrag(pointer);
        e.Handled = true;
    }

    internal static bool ShouldBeginWindowDrag(DependencyObject? source) =>
        !IsChromeButton(source) && !IsScrollChrome(source);

    private static bool IsChromeButton(DependencyObject? source) =>
        Ancestors(source).OfType<System.Windows.Controls.Primitives.ButtonBase>().Any();

    private static bool IsScrollChrome(DependencyObject? source) =>
        Ancestors(source).OfType<System.Windows.Controls.Primitives.ScrollBar>().Any();

    private static WidgetAccountModuleView? ModuleAt(DependencyObject? source) =>
        Ancestors(source).OfType<WidgetAccountModuleView>().FirstOrDefault();

    // Only visual ancestors: a non-visual original source (an inline, say) has no visual parent.
    private static IEnumerable<DependencyObject> Ancestors(DependencyObject? source)
    {
        for (var node = source; node is Visual or System.Windows.Media.Media3D.Visual3D;
             node = VisualTreeHelper.GetParent(node))
        {
            yield return node;
            if (node is Window) yield break;
        }
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
        var pressed = _pressedProfileId;
        _drag = null;
        _pressedProfileId = null;
        if (IsMouseCaptured) ReleaseMouseCapture();
        // A move that ended is a move, never an accidental open.
        if (gesture?.IsDragging == true) { Moved?.Invoke(Left, Top); return; }
        if (gesture is null || !allowClick) return;
        // Selecting reuses the existing selection state; it never starts a login or a request.
        if (!string.IsNullOrEmpty(pressed)) AccountSelected?.Invoke(pressed);
        FlyoutRequested?.Invoke();
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

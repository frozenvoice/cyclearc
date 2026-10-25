using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CycleArc.Setup;

/// <summary>
/// The installer's four screens in one frameless window: confirm, progress, done, failed.
/// Opening the file changes nothing; only the Install button starts the engine.
///
/// Every button, checkbox and line of text is still a real Win32 control with the same
/// IDs as before, so keyboard navigation, the accessibility tree and the UI verification
/// script keep working; <see cref="SetupScene"/> only paints them. Installing stays here.
/// </summary>
internal sealed class SetupWindow
{
    private const int IdInstall = 100;
    private const int IdCancel = 101;
    private const int IdRun = 102;
    private const int IdDesktopShortcut = 103;
    private const int IdDetail = 105;
    private const int IdMinimize = 110;
    private const int IdClose = 111;
    private const int IdText = 200;

    private const int WindowStyle = Native.WS_OVERLAPPED | Native.WS_CAPTION | Native.WS_SYSMENU
        | Native.WS_MINIMIZEBOX | Native.WS_CLIPCHILDREN;
    private const int SWP_FRAMECHANGED = 0x0020;

    private const nuint AnimationTimer = 1;
    private const double RevealSeconds = 0.28;
    private const double FillSeconds = 0.45;
    private const double FillHoldSeconds = 0.2;

    private readonly InstallTarget _target;
    private readonly string? _logPath;
    private readonly bool _desktopShortcut;
    private readonly Native.WndProc _proc;
    private readonly Dictionary<SetupElement, IntPtr> _controls = new();
    private readonly Dictionary<IntPtr, SetupElement> _elements = new();
    private readonly Dictionary<SetupElement, SetupCanvas> _canvases = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private IntPtr _window;
    private IntPtr _detail;
    private IntPtr _panelBrush;
    private IntPtr _bigIcon, _smallIcon;
    private SetupScene _scene = null!;
    private SetupCanvas? _surface;
    private bool _roundedByDwm;
    private EngineResult? _result;
    private bool _installing;
    private bool _finishing;
    private bool _timer;
    private double _revealStart = -1;
    private double _progressStart;
    private double _fillStart = -1;

    public SetupExitCode Exit { get; private set; } = SetupExitCode.Cancelled;

    public SetupWindow(InstallTarget target, string? logPath = null, bool? desktopShortcut = null)
    {
        _target = target;
        _logPath = logPath;
        _desktopShortcut = desktopShortcut ?? DesktopShortcuts.IsPresent(target.Directory);
        _proc = WindowProc;
    }

    public SetupExitCode Run()
    {
        var instance = Native.GetModuleHandle(null);
        var common = new Native.INITCOMMONCONTROLSEX
        {
            dwSize = Marshal.SizeOf<Native.INITCOMMONCONTROLSEX>(),
            dwICC = Native.ICC_STANDARD_CLASSES,
        };
        Native.InitCommonControlsEx(ref common);

        var className = "CycleArcSetupWindow";
        var classNamePtr = Marshal.StringToHGlobalUni(className);
        var wcx = new Native.WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<Native.WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = instance,
            hCursor = Native.LoadCursor(IntPtr.Zero, Native.IDC_ARROW),
            lpszClassName = classNamePtr,
        };
        if (Native.RegisterClassEx(ref wcx) == 0)
            return SetupExitCode.Failed;

        // Opened on the monitor the person is working on, not always the primary one. It is
        // created there first because a window's DPI is that of the monitor it is on.
        var work = WorkArea();
        _window = Native.CreateWindowEx(0, className, Strings.WindowTitle, WindowStyle,
            work.Left, work.Top, 100, 100, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        if (_window == IntPtr.Zero) return SetupExitCode.Failed;

        StyleFrame();
        BuildScene(Native.GetDpiForWindow(_window));
        BuildControls(instance);
        SetIcons(instance);
        SizeAndCentre(work);
        ShowPage(SetupPage.Confirm);
        // From here the parent's clock is a person's reading time, not an installation.
        SetupState.Report(SetupState.AwaitingApproval);
        Native.ShowWindow(_window, Native.SW_SHOW);

        while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (Native.IsDialogMessage(_window, ref msg)) continue;
            Native.TranslateMessage(ref msg);
            Native.DispatchMessage(ref msg);
        }

        ReleaseResources();
        Marshal.FreeHGlobal(classNamePtr);
        return Exit;
    }

    // --- Frame, scale and resources ------------------------------------------------------

    private static Native.RECT WorkArea()
    {
        Native.GetCursorPos(out var cursor);
        var monitor = Native.MonitorFromPoint(cursor, Native.MONITOR_DEFAULTTONEAREST);
        var info = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        if (monitor != IntPtr.Zero && Native.GetMonitorInfo(monitor, ref info)) return info.rcWork;
        return new Native.RECT
        {
            Right = Native.GetSystemMetrics(Native.SM_CXSCREEN),
            Bottom = Native.GetSystemMetrics(Native.SM_CYSCREEN),
        };
    }

    /// <summary>
    /// A dark, borderless frame that keeps the system shadow. Windows 11 rounds the corners
    /// itself; where it cannot, the backdrop draws a hairline border instead.
    /// </summary>
    private void StyleFrame()
    {
        var on = 1;
        Native.DwmSetWindowAttribute(_window, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
        var round = Native.DWMWCP_ROUND;
        _roundedByDwm = Native.DwmSetWindowAttribute(_window, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int)) == 0;
        var border = (int)Rgb.Hex(0x2E2E34).ColorRef;
        Native.DwmSetWindowAttribute(_window, Native.DWMWA_BORDER_COLOR, ref border, sizeof(int));
    }

    private void BuildScene(uint dpi)
    {
        var old = _scene;
        var reducedMotion = 1;
        Native.SystemParametersInfo(Native.SPI_GETCLIENTAREAANIMATION, 0, ref reducedMotion, 0);
        _scene = new SetupScene(dpi == 0 ? 96 : dpi, SetupPalette.Current(), reducedMotion == 0, !_roundedByDwm)
        {
            Page = old?.Page ?? SetupPage.Confirm,
            IsUpdate = _target.IsExistingInstall,
            Location = _target.Directory,
            Stage = old?.Stage,
            Detail = old?.Detail ?? "",
            Reveal = old?.Reveal ?? 1f,
            Phase = old?.Phase ?? 0,
            Fill = old?.Fill,
        };

        if (_panelBrush != IntPtr.Zero) Native.DeleteObject(_panelBrush);
        _panelBrush = Native.CreateSolidBrush(_scene.Palette.Panel.ColorRef);
        if (_detail != IntPtr.Zero)
        {
            Native.SendMessage(_detail, Native.WM_SETFONT, _scene.Fonts.Mono, 1);
            Native.SetWindowTheme(_detail, _scene.Palette.HighContrast ? null : "DarkMode_Explorer", null);
        }
        foreach (var canvas in _canvases.Values) canvas.Dispose();
        _canvases.Clear();
        _surface?.Dispose();
        _surface = null;
        old?.Dispose();
    }

    private void SizeAndCentre(Native.RECT work)
    {
        // No system frame: the window is exactly the scaled client area.
        var width = _scene.Width;
        var height = _scene.Height;
        var x = work.Left + Math.Max(0, (work.Right - work.Left - width) / 2);
        var y = work.Top + Math.Max(0, (work.Bottom - work.Top - height) / 2);
        Native.SetWindowPos(_window, IntPtr.Zero, x, y, width, height,
            Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    private void SetIcons(IntPtr instance)
    {
        // The application icon the SDK embeds from ApplicationIcon, for the taskbar and Alt+Tab.
        const int applicationIcon = 32512;
        _bigIcon = Native.LoadImage(instance, applicationIcon, Native.IMAGE_ICON,
            SetupLayout.Scale(32, _scene.Dpi), SetupLayout.Scale(32, _scene.Dpi), 0);
        _smallIcon = Native.LoadImage(instance, applicationIcon, Native.IMAGE_ICON,
            SetupLayout.Scale(16, _scene.Dpi), SetupLayout.Scale(16, _scene.Dpi), 0);
        if (_bigIcon != IntPtr.Zero) Native.SendMessage(_window, Native.WM_SETICON, 1, _bigIcon);
        if (_smallIcon != IntPtr.Zero) Native.SendMessage(_window, Native.WM_SETICON, 0, _smallIcon);
    }

    private void BuildControls(IntPtr instance)
    {
        void Add(SetupElement element, string cls, int style, int id)
        {
            var control = Native.CreateWindowEx(0, cls, "", Native.WS_CHILD | style,
                0, 0, 1, 1, _window, id, instance, IntPtr.Zero);
            _controls[element] = control;
            _elements[control] = element;
        }

        // Text the accessibility tree reads, painted by the scene.
        Add(SetupElement.Heading, "STATIC", Native.SS_OWNERDRAW, IdText);
        Add(SetupElement.Body, "STATIC", Native.SS_OWNERDRAW, IdText + 1);
        Add(SetupElement.Location, "STATIC", Native.SS_OWNERDRAW, IdText + 2);
        Add(SetupElement.Status, "STATIC", Native.SS_OWNERDRAW, IdText + 3);
        Add(SetupElement.Hint, "STATIC", Native.SS_OWNERDRAW, IdText + 4);
        // Like a title bar: reachable by mouse and Alt+Space, not in the Tab order.
        Add(SetupElement.Minimize, "BUTTON", Native.BS_PUSHBUTTON, IdMinimize);
        Add(SetupElement.Close, "BUTTON", Native.BS_PUSHBUTTON, IdClose);
        // Tab order follows creation order.
        Add(SetupElement.DesktopShortcut, "BUTTON", Native.BS_AUTOCHECKBOX | Native.WS_TABSTOP, IdDesktopShortcut);
        Add(SetupElement.Run, "BUTTON", Native.BS_AUTOCHECKBOX | Native.WS_TABSTOP, IdRun);
        Add(SetupElement.Detail, "EDIT",
            Native.ES_READONLY | Native.ES_MULTILINE | Native.WS_VSCROLL | Native.WS_TABSTOP, IdDetail);
        Add(SetupElement.Secondary, "BUTTON", Native.BS_PUSHBUTTON | Native.WS_TABSTOP, IdCancel);
        Add(SetupElement.Primary, "BUTTON", Native.BS_DEFPUSHBUTTON | Native.WS_TABSTOP, IdInstall);

        _detail = _controls[SetupElement.Detail];
        Native.SendMessage(_detail, Native.WM_SETFONT, _scene.Fonts.Mono, 1);
        Native.SendMessage(_detail, Native.EM_SETMARGINS, Native.EC_LEFTMARGIN | Native.EC_RIGHTMARGIN, IntPtr.Zero);
        Native.SetWindowTheme(_detail, _scene.Palette.HighContrast ? null : "DarkMode_Explorer", null);

        Native.SendMessage(_controls[SetupElement.DesktopShortcut], Native.BM_SETCHECK,
            _desktopShortcut ? Native.BST_CHECKED : Native.BST_UNCHECKED, IntPtr.Zero);
        Native.SendMessage(_controls[SetupElement.Run], Native.BM_SETCHECK, Native.BST_CHECKED, IntPtr.Zero);
    }

    /// <summary>Places, labels and shows exactly the controls the current page has.</summary>
    private void Layout()
    {
        foreach (var (element, control) in _controls)
        {
            if (_scene.Bounds(element) is not { } box)
            {
                Native.ShowWindow(control, Native.SW_HIDE);
                continue;
            }
            if (element == SetupElement.Detail)
            {
                var pad = SetupLayout.Scale(SetupLayout.DetailPadding, _scene.Dpi);
                box = new Native.RECT { Left = box.Left + pad, Top = box.Top + pad, Right = box.Right - pad, Bottom = box.Bottom - pad };
            }
            else
            {
                Native.SetWindowText(control, _scene.Text(element));
            }
            Native.SetWindowPos(control, IntPtr.Zero, box.Left, box.Top, box.Right - box.Left, box.Bottom - box.Top,
                Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            Native.ShowWindow(control, Native.SW_SHOW);
        }
    }

    private void ReleaseResources()
    {
        StopTimer();
        foreach (var canvas in _canvases.Values) canvas.Dispose();
        _canvases.Clear();
        _surface?.Dispose();
        _scene?.Dispose();
        if (_panelBrush != IntPtr.Zero) Native.DeleteObject(_panelBrush);
        if (_bigIcon != IntPtr.Zero) Native.DestroyIcon(_bigIcon);
        if (_smallIcon != IntPtr.Zero) Native.DestroyIcon(_smallIcon);
    }

    // --- Pages and motion ----------------------------------------------------------------

    private double Now => _clock.Elapsed.TotalSeconds;

    private void ShowPage(SetupPage page)
    {
        _scene.Page = page;
        if (page == SetupPage.Progress)
        {
            _progressStart = Now;
            _scene.Phase = 0;
            _scene.Fill = null;
        }
        Layout();
        // Nothing can be closed mid-install, and the close box says so by being disabled.
        Native.EnableWindow(_controls[SetupElement.Close], page != SetupPage.Progress);

        var focus = page == SetupPage.Progress ? _window : _controls[SetupElement.Primary];
        Native.SetFocus(focus);

        if (_scene.ReducedMotion)
        {
            _scene.Reveal = 1f;
            _revealStart = -1;
        }
        else
        {
            _scene.Reveal = 0f;
            _revealStart = Now;
        }
        RedrawAll();
        UpdateTimer();
    }

    private void RedrawAll() =>
        Native.RedrawWindow(_window, IntPtr.Zero, IntPtr.Zero, Native.RDW_INVALIDATE | Native.RDW_ALLCHILDREN);

    private bool Animating =>
        _revealStart >= 0 || _fillStart >= 0 || _scene.Page == SetupPage.Progress;

    private void UpdateTimer()
    {
        if (Animating && !_timer)
        {
            // Positions come from the clock, not the tick count, so a late tick never stutters.
            Native.SetTimer(_window, AnimationTimer, _scene.ReducedMotion ? 50u : 15u, IntPtr.Zero);
            _timer = true;
        }
        else if (!Animating) StopTimer();
    }

    private void StopTimer()
    {
        if (!_timer) return;
        Native.KillTimer(_window, AnimationTimer);
        _timer = false;
    }

    private void Tick()
    {
        var now = Now;
        var full = false;
        if (_revealStart >= 0)
        {
            var t = (now - _revealStart) / RevealSeconds;
            _scene.Reveal = (float)EaseOut(Math.Min(1, t));
            if (t >= 1) _revealStart = -1;
            full = true;
        }
        if (_scene.Page == SetupPage.Progress) _scene.Phase = now - _progressStart;
        if (_fillStart >= 0)
        {
            var t = (now - _fillStart) / FillSeconds;
            _scene.Fill = (float)EaseOut(Math.Min(1, t));
            if (now - _fillStart >= FillSeconds + FillHoldSeconds)
            {
                _fillStart = -1;
                _finishing = false;
                ShowPage(SetupPage.Done);
                return;
            }
        }

        if (full) RedrawAll();
        else if (_scene.Page == SetupPage.Progress)
        {
            var band = _scene.ProgressInvalidation();
            Native.InvalidateRect(_window, ref band, false);
        }
        UpdateTimer();
    }

    private static double EaseOut(double t) => 1 - Math.Pow(1 - t, 3);

    // --- Painting ------------------------------------------------------------------------

    private void Paint()
    {
        var hdc = Native.BeginPaint(_window, out var paint);
        try
        {
            if (_surface is null || _surface.Width != _scene.Width || _surface.Height != _scene.Height)
            {
                _surface?.Dispose();
                _surface = new SetupCanvas(_scene.Width, _scene.Height);
            }
            _scene.DrawWindow(_surface);
            var area = paint.rcPaint;
            Native.BitBlt(hdc, area.Left, area.Top, area.Right - area.Left, area.Bottom - area.Top,
                _surface.Dc, area.Left, area.Top, Native.SRCCOPY);
        }
        finally
        {
            Native.EndPaint(_window, ref paint);
        }
    }

    private void DrawControl(IntPtr control, SetupElement element, IntPtr hdc, Native.RECT rect, ControlState state)
    {
        if (_scene.Bounds(element) is not { } box) return;
        var width = box.Right - box.Left;
        var height = box.Bottom - box.Top;
        if (!_canvases.TryGetValue(element, out var canvas) || canvas.Width != width || canvas.Height != height)
        {
            canvas?.Dispose();
            canvas = new SetupCanvas(width, height);
            _canvases[element] = canvas;
        }
        canvas.OriginX = box.Left;
        canvas.OriginY = box.Top;
        _scene.DrawControl(canvas, element, state);
        canvas.CopyTo(hdc, rect.Left, rect.Top);
    }

    private ControlState StateOf(IntPtr control, uint itemState)
    {
        var state = Native.SendMessage(control, Native.BM_GETSTATE, IntPtr.Zero, IntPtr.Zero).ToInt64();
        var cues = Native.SendMessage(control, Native.WM_QUERYUISTATE, IntPtr.Zero, IntPtr.Zero).ToInt64();
        const uint selected = 0x1, disabled = 0x4, focus = 0x10, hot = 0x40;
        return new ControlState(
            Hot: (itemState & hot) != 0 || (state & Native.BST_HOT) != 0,
            Pressed: (state & Native.BST_PUSHED) != 0 || ((itemState & selected) != 0 && (state & 0x3) == 0 && !IsCheckbox(control)),
            // The ring appears once the keyboard is in use, as everywhere else in Windows.
            Focused: (itemState & focus) != 0 && (cues & Native.UISF_HIDEFOCUS) == 0,
            Enabled: (itemState & disabled) == 0 && Native.IsWindowEnabled(control),
            Checked: (state & 0x3) == Native.BST_CHECKED);
    }

    private bool IsCheckbox(IntPtr control) =>
        _elements.TryGetValue(control, out var element) && element is SetupElement.DesktopShortcut or SetupElement.Run;

    // --- Installing (unchanged in behaviour) ---------------------------------------------

    private void StartInstall()
    {
        if (_installing || _finishing) return;
        // Read controls on the UI thread before the worker starts.
        var wantsDesktopShortcut = Native.SendMessage(_controls[SetupElement.DesktopShortcut], Native.BM_GETCHECK,
            IntPtr.Zero, IntPtr.Zero).ToInt64() == Native.BST_CHECKED;
        _installing = true;
        SetupState.Report(SetupState.Installing);
        _scene.Stage = null;
        ShowPage(SetupPage.Progress);
        var window = _window;
        var log = _logPath;
        // Off the message loop, so the window keeps painting while the engine runs.
        var worker = new Thread(() =>
        {
            var result = EngineRunner.Install(_target.Directory, CancellationToken.None, log,
                createDesktopShortcut: wantsDesktopShortcut,
                stage: stage => Native.PostMessage(window, Native.WM_INSTALL_STAGE, (int)stage, IntPtr.Zero));
            _result = result;
            Native.PostMessage(window, Native.WM_INSTALL_DONE, IntPtr.Zero, IntPtr.Zero);
        })
        { IsBackground = true };
        worker.Start();
    }

    private void FinishInstall()
    {
        var result = _result;
        _installing = false;
        if (result is null || !result.Succeeded)
        {
            Exit = SetupExitCode.Failed;
            SetupState.Report(SetupState.Failed);
            ShowFailed(result ?? new EngineResult(EngineOutcome.Failed, -1, "", null));
            return;
        }

        Exit = SetupExitCode.Succeeded;
        SetupState.Report(SetupState.Done);
        if (_scene.ReducedMotion)
        {
            ShowPage(SetupPage.Done);
            return;
        }
        // The one true percentage this installer knows: finished. The bar fills, then the
        // completion page arrives. Commands wait until it has.
        _finishing = true;
        _fillStart = Now;
        UpdateTimer();
    }

    private void ShowFailed(EngineResult result)
    {
        var text = (result.Detail ?? "").Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\n", "\r\n", StringComparison.Ordinal).Trim();
        if (text.Length == 0) text = Strings.FailedNoDetail;
        if (result.ExitCode is not 0 and not -1) text = $"{Strings.FailedExitCode}: {result.ExitCode}\r\n{text}";
        // Only point at a log that is really there. A log that could not be written is
        // said so plainly, and never replaces the installation's own error.
        var tail = result.HasLog
            ? Strings.LogLabel + ": " + result.LogPath
            : result.LogError ?? Strings.NoLogWritten;
        // The log location first: the cause may need scrolling, the path should not.
        _scene.Detail = tail + "\r\n\r\n" + text;
        Native.SetWindowText(_detail, _scene.Detail);
        ShowPage(SetupPage.Failed);
    }

    // --- Messages ------------------------------------------------------------------------

    private unsafe IntPtr WindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Native.WM_NCCALCSIZE:
                // The whole window is client area; the brand and buttons replace the caption.
                if (wParam != IntPtr.Zero) return IntPtr.Zero;
                break;

            case Native.WM_NCHITTEST:
            {
                // Empty space drags the window, as a title bar would. Controls answer for themselves.
                var hit = Native.DefWindowProc(hWnd, msg, wParam, lParam);
                return hit == Native.HTCLIENT ? Native.HTCAPTION : hit;
            }

            case Native.WM_NCACTIVATE:
                // There is no frame to repaint on activation.
                return Native.DefWindowProc(hWnd, msg, wParam, new IntPtr(-1));

            case Native.WM_ERASEBKGND:
                return 1;

            case Native.WM_PAINT:
                Paint();
                return IntPtr.Zero;

            case Native.WM_DRAWITEM:
            {
                var item = (Native.DRAWITEMSTRUCT*)lParam;
                if (!_elements.TryGetValue(item->hwndItem, out var element)) break;
                DrawControl(item->hwndItem, element, item->hDC, item->rcItem, new ControlState(Enabled: true));
                return 1;
            }

            case Native.WM_NOTIFY:
            {
                var draw = (Native.NMCUSTOMDRAW*)lParam;
                if (draw->hdr.code != Native.NM_CUSTOMDRAW || !_elements.TryGetValue(draw->hdr.hwndFrom, out var element))
                    break;
                if (draw->dwDrawStage != Native.CDDS_PREPAINT) return Native.CDRF_DODEFAULT;
                DrawControl(draw->hdr.hwndFrom, element, draw->hdc, draw->rc, StateOf(draw->hdr.hwndFrom, draw->uItemState));
                return Native.CDRF_SKIPDEFAULT;
            }

            case Native.WM_CTLCOLORSTATIC:
            case Native.WM_CTLCOLOREDIT:
                if (lParam != _detail) break;
                Native.SetTextColor(wParam, _scene.Palette.PanelText.ColorRef);
                Native.SetBkColor(wParam, _scene.Palette.Panel.ColorRef);
                return _panelBrush;

            case Native.WM_TIMER:
                if (wParam == (IntPtr)AnimationTimer) Tick();
                return IntPtr.Zero;

            case Native.WM_DPICHANGED:
            {
                // Moved to a monitor with another scale: everything is rebuilt at the new DPI.
                var suggested = *(Native.RECT*)lParam;
                BuildScene((uint)(wParam.ToInt64() & 0xFFFF));
                Native.SetWindowPos(hWnd, IntPtr.Zero, suggested.Left, suggested.Top, _scene.Width, _scene.Height,
                    Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
                Layout();
                RedrawAll();
                return IntPtr.Zero;
            }

            case Native.WM_SETTINGCHANGE:
            case Native.WM_THEMECHANGED:
            case Native.WM_SYSCOLORCHANGE:
                // High contrast and the animation preference can change while the window is open.
                BuildScene(_scene.Dpi);
                RedrawAll();
                UpdateTimer();
                break;

            case Native.WM_INSTALL_STAGE:
                _scene.Stage = (InstallStage)(int)wParam;
                Native.SetWindowText(_controls[SetupElement.Status], _scene.Text(SetupElement.Status));
                Native.InvalidateRect(_controls[SetupElement.Status], IntPtr.Zero, false);
                return IntPtr.Zero;

            case Native.DM_GETDEFID:
                return new IntPtr((Native.DC_HASDEFID << 16) | IdInstall);

            case Native.WM_COMMAND:
                var id = (int)(wParam.ToInt64() & 0xFFFF);
                if (id == Native.IDCANCEL || id == IdClose)
                {
                    Native.PostMessage(hWnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                    return IntPtr.Zero;
                }
                if (id == IdMinimize)
                {
                    Native.ShowWindow(hWnd, Native.SW_MINIMIZE);
                    return IntPtr.Zero;
                }
                if (id == IdInstall)
                {
                    if (_installing || _finishing) return IntPtr.Zero;
                    if (Exit == SetupExitCode.Succeeded) { CompleteAndClose(); return IntPtr.Zero; }
                    if (Exit == SetupExitCode.Failed) { Native.DestroyWindow(hWnd); return IntPtr.Zero; }
                    StartInstall();
                    return IntPtr.Zero;
                }
                if (id == IdCancel && !_installing)
                {
                    // Cancelling before Install changes nothing: no app was stopped, no file
                    // was written, and no setting was touched.
                    Exit = SetupExitCode.Cancelled;
                    SetupState.Report(SetupState.Cancelled);
                    Native.DestroyWindow(hWnd);
                    return IntPtr.Zero;
                }
                return IntPtr.Zero;

            case Native.WM_INSTALL_DONE:
                FinishInstall();
                return IntPtr.Zero;

            case Native.WM_CLOSE:
                // The close box during an install is ignored rather than leaving a half-install.
                if (_installing) return IntPtr.Zero;
                if (Exit != SetupExitCode.Succeeded && Exit != SetupExitCode.Failed)
                {
                    Exit = SetupExitCode.Cancelled;
                    SetupState.Report(SetupState.Cancelled);
                }
                Native.DestroyWindow(hWnd);
                return IntPtr.Zero;

            case Native.WM_DESTROY:
                StopTimer();
                Native.PostQuitMessage(0);
                return IntPtr.Zero;
        }

        return Native.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void CompleteAndClose()
    {
        var wantsRun = Native.SendMessage(_controls[SetupElement.Run], Native.BM_GETCHECK, IntPtr.Zero, IntPtr.Zero).ToInt64()
            == Native.BST_CHECKED;
        if (wantsRun && !EngineRunner.TryLaunch(_target.Directory, out var error))
        {
            Native.MessageBox(_window, Strings.LaunchFailedPrefix + "\n" + error,
                Strings.WindowTitle, Native.MB_OK | Native.MB_ICONERROR);
        }

        Native.DestroyWindow(_window);
    }
}

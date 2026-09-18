using System.Runtime.InteropServices;

namespace CycleArc.Setup;

/// <summary>
/// The installer's three screens in one window: confirm, progress, done. Opening the file
/// changes nothing; only the Install button starts the engine.
/// </summary>
internal sealed class SetupWindow
{
    private const int IdInstall = 100;
    private const int IdCancel = 101;
    private const int IdRun = 102;

    private readonly InstallTarget _target;
    private readonly Native.WndProc _proc;
    private IntPtr _window;
    private IntPtr _heading, _body, _locationLabel, _location, _progress, _status, _detail, _runCheck, _primary, _secondary;
    private IntPtr _titleFont, _bodyFont;
    private EngineResult? _result;
    private bool _installing;

    public SetupExitCode Exit { get; private set; } = SetupExitCode.Cancelled;

    public SetupWindow(InstallTarget target)
    {
        _target = target;
        _proc = WindowProc;
    }

    public SetupExitCode Run()
    {
        var instance = Native.GetModuleHandle(null);
        var common = new Native.INITCOMMONCONTROLSEX
        {
            dwSize = Marshal.SizeOf<Native.INITCOMMONCONTROLSEX>(),
            dwICC = Native.ICC_PROGRESS_CLASS | Native.ICC_STANDARD_CLASSES,
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
            hbrBackground = Native.COLOR_WINDOW + 1,
            lpszClassName = classNamePtr,
        };
        if (Native.RegisterClassEx(ref wcx) == 0)
            return SetupExitCode.Failed;

        const int width = 560, height = 340;
        var x = Math.Max(0, (Native.GetSystemMetrics(Native.SM_CXSCREEN) - width) / 2);
        var y = Math.Max(0, (Native.GetSystemMetrics(Native.SM_CYSCREEN) - height) / 2);
        _window = Native.CreateWindowEx(0, className, Strings.WindowTitle,
            Native.WS_OVERLAPPED | Native.WS_CAPTION | Native.WS_SYSMENU | Native.WS_MINIMIZEBOX,
            x, y, width, height, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        if (_window == IntPtr.Zero) return SetupExitCode.Failed;

        BuildControls(instance);
        ShowConfirm();
        Native.ShowWindow(_window, Native.SW_SHOW);

        while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            Native.TranslateMessage(ref msg);
            Native.DispatchMessage(ref msg);
        }

        if (_titleFont != IntPtr.Zero) Native.DeleteObject(_titleFont);
        if (_bodyFont != IntPtr.Zero) Native.DeleteObject(_bodyFont);
        Marshal.FreeHGlobal(classNamePtr);
        return Exit;
    }

    private void BuildControls(IntPtr instance)
    {
        var dpi = Native.GetDpiForWindow(_window);
        if (dpi == 0) dpi = 96;
        int Scaled(int value) => (int)Math.Round(value * dpi / 96.0);

        _titleFont = Native.CreateFont(-Scaled(19), 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        _bodyFont = Native.CreateFont(-Scaled(13), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");

        IntPtr Child(string cls, string text, int style, int cx, int cy, int cw, int ch, int id = 0) =>
            Native.CreateWindowEx(0, cls, text, Native.WS_CHILD | style,
                Scaled(cx), Scaled(cy), Scaled(cw), Scaled(ch), _window, id, instance, IntPtr.Zero);

        _heading = Child("STATIC", "", Native.SS_LEFT, 24, 22, 490, 30);
        _body = Child("STATIC", "", Native.SS_LEFT, 24, 58, 490, 44);
        _locationLabel = Child("STATIC", Strings.LocationLabel, Native.SS_LEFT, 24, 112, 490, 18);
        _location = Child("EDIT", _target.Directory,
            Native.ES_READONLY | Native.ES_AUTOHSCROLL | Native.WS_BORDER, 24, 132, 490, 24);
        _progress = Child("msctls_progress32", "", Native.PBS_MARQUEE, 24, 132, 490, 18);
        _status = Child("STATIC", "", Native.SS_LEFT | Native.SS_PATHELLIPSIS, 24, 158, 490, 20);
        _detail = Child("EDIT", "",
            Native.ES_READONLY | Native.ES_MULTILINE | Native.WS_BORDER | Native.WS_VSCROLL, 24, 112, 490, 100);
        _runCheck = Child("BUTTON", Strings.RunCheckbox, Native.BS_AUTOCHECKBOX, 24, 132, 300, 24, IdRun);
        _primary = Child("BUTTON", Strings.InstallButton,
            Native.BS_DEFPUSHBUTTON | Native.WS_TABSTOP, 318, 244, 96, 32, IdInstall);
        _secondary = Child("BUTTON", Strings.CancelButton,
            Native.BS_PUSHBUTTON | Native.WS_TABSTOP, 420, 244, 96, 32, IdCancel);

        foreach (var control in new[] { _body, _locationLabel, _location, _status, _detail, _runCheck, _primary, _secondary })
            Native.SendMessage(control, Native.WM_SETFONT, _bodyFont, 1);
        Native.SendMessage(_heading, Native.WM_SETFONT, _titleFont, 1);
        Native.SendMessage(_runCheck, Native.BM_SETCHECK, Native.BST_CHECKED, IntPtr.Zero);
    }

    private static void Show(IntPtr control, bool visible) =>
        Native.ShowWindow(control, visible ? Native.SW_SHOW : Native.SW_HIDE);

    private void ShowConfirm()
    {
        Native.SetWindowText(_heading, _target.IsExistingInstall
            ? Strings.ConfirmHeadingUpdate : Strings.ConfirmHeadingInstall);
        Native.SetWindowText(_body, _target.IsExistingInstall
            ? Strings.ConfirmBodyUpdate : Strings.ConfirmBodyInstall);
        Native.SetWindowText(_location, _target.Directory);
        Native.SetWindowText(_primary, Strings.InstallButton);
        Native.SetWindowText(_secondary, Strings.CancelButton);
        Show(_heading, true); Show(_body, true); Show(_locationLabel, true); Show(_location, true);
        Show(_progress, false); Show(_status, false); Show(_detail, false); Show(_runCheck, false);
        Show(_primary, true); Show(_secondary, true);
        Native.EnableWindow(_primary, true);
        Native.EnableWindow(_secondary, true);
    }

    private void ShowProgress()
    {
        Native.SetWindowText(_heading, Strings.ProgressHeading);
        Native.SetWindowText(_body, Strings.ProgressBody);
        Native.SetWindowText(_status, Strings.ProgressCopying);
        Show(_locationLabel, false); Show(_location, false); Show(_detail, false); Show(_runCheck, false);
        Show(_progress, true); Show(_status, true);
        // Indeterminate: the engine reports completion, not a percentage, and a fake
        // percentage would be a worse answer than an honest "working".
        Native.SendMessage(_progress, Native.PBM_SETMARQUEE, 1, 30);
        Native.EnableWindow(_primary, false);
        Native.EnableWindow(_secondary, false);
    }

    private void ShowDone()
    {
        Native.SetWindowText(_heading, Strings.DoneHeading);
        Native.SetWindowText(_body, Strings.DoneBody);
        Show(_progress, false); Show(_status, false); Show(_detail, false);
        Show(_locationLabel, true); Show(_location, true);
        Native.SetWindowText(_location, _target.Directory);
        Show(_runCheck, false);
        // The checkbox sits under the location on this page.
        Native.SetWindowPos(_runCheck, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0004);
        Show(_runCheck, true);
        Native.SetWindowText(_primary, Strings.FinishButton);
        Show(_primary, true); Show(_secondary, false);
        Native.EnableWindow(_primary, true);
    }

    private void ShowFailed(EngineResult result)
    {
        Native.SetWindowText(_heading, Strings.FailedHeading);
        Native.SetWindowText(_body, Strings.FailedBodyPrefix);
        var text = (result.Detail ?? "").Replace("\n", "\r\n", StringComparison.Ordinal);
        var log = EngineRunner.KeptLogPath(_target.Directory);
        if (!File.Exists(log)) log = result.LogPath;
        Native.SetWindowText(_detail,
            text + (text.Length > 0 ? "\r\n\r\n" : "") + Strings.LogLabel + ": " + log);
        Show(_progress, false); Show(_status, false); Show(_locationLabel, false); Show(_location, false);
        Show(_runCheck, false); Show(_detail, true);
        Native.SetWindowText(_primary, Strings.CloseButton);
        Show(_primary, true); Show(_secondary, false);
        Native.EnableWindow(_primary, true);
    }

    private void StartInstall()
    {
        if (_installing) return;
        _installing = true;
        ShowProgress();
        var window = _window;
        // Off the message loop, so the window keeps painting while the engine runs.
        var worker = new Thread(() =>
        {
            var result = EngineRunner.Install(_target.Directory, CancellationToken.None);
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
            ShowFailed(result ?? new EngineResult(EngineOutcome.Failed, -1, "", null));
            return;
        }

        Exit = SetupExitCode.Succeeded;
        ShowDone();
    }

    private IntPtr WindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Native.WM_COMMAND:
                var id = (int)(wParam.ToInt64() & 0xFFFF);
                if (id == IdInstall)
                {
                    if (_installing) return IntPtr.Zero;
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
                    Exit = SetupExitCode.Cancelled;
                Native.DestroyWindow(hWnd);
                return IntPtr.Zero;

            case Native.WM_DESTROY:
                Native.PostQuitMessage(0);
                return IntPtr.Zero;
        }

        return Native.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void CompleteAndClose()
    {
        var wantsRun = Native.SendMessage(_runCheck, Native.BM_GETCHECK, IntPtr.Zero, IntPtr.Zero).ToInt64()
            == Native.BST_CHECKED;
        if (wantsRun && !EngineRunner.TryLaunch(_target.Directory, out var error))
        {
            Native.MessageBox(_window, Strings.LaunchFailedPrefix + "\n" + error,
                Strings.WindowTitle, Native.MB_OK | Native.MB_ICONERROR);
        }

        Native.DestroyWindow(_window);
    }
}

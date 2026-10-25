using System.Runtime.InteropServices;

namespace CycleArc.Setup;

/// <summary>The Win32 surface this installer uses. Nothing here is CycleArc-specific.</summary>
internal static partial class Native
{
    public const int WS_OVERLAPPED = 0x00000000;
    public const int WS_CAPTION = 0x00C00000;
    public const int WS_SYSMENU = 0x00080000;
    public const int WS_MINIMIZEBOX = 0x00020000;
    public const int WS_VISIBLE = 0x10000000;
    public const int WS_CHILD = 0x40000000;
    public const int WS_TABSTOP = 0x00010000;
    public const int WS_DISABLED = 0x08000000;

    public const int BS_PUSHBUTTON = 0x00000000;
    public const int BS_DEFPUSHBUTTON = 0x00000001;
    public const int BS_AUTOCHECKBOX = 0x00000003;
    public const int SS_LEFT = 0x00000000;
    public const int SS_PATHELLIPSIS = 0x00008000;
    public const int ES_READONLY = 0x00000800;
    public const int ES_AUTOHSCROLL = 0x00000080;
    public const int ES_MULTILINE = 0x00000004;
    public const int WS_BORDER = 0x00800000;
    public const int WS_VSCROLL = 0x00200000;

    public const int PBS_MARQUEE = 0x08;
    public const int PBM_SETMARQUEE = 0x040A;

    public const int WM_DESTROY = 0x0002;
    public const int WM_CLOSE = 0x0010;
    public const int WM_COMMAND = 0x0111;
    public const int WM_SETFONT = 0x0030;
    public const int WM_CTLCOLORSTATIC = 0x0138;
    public const int WM_APP = 0x8000;
    public const int WM_INSTALL_DONE = WM_APP + 1;
    public const int DM_GETDEFID = 0x0400;
    public const int DC_HASDEFID = 0x534B;
    public const int IDCANCEL = 2;

    public const int BM_SETCHECK = 0x00F1;
    public const int BM_GETCHECK = 0x00F0;
    public const int BST_CHECKED = 1;
    public const int BST_UNCHECKED = 0;

    public const int SW_SHOW = 5;
    public const int SW_HIDE = 0;

    public const int MB_OK = 0x0;
    public const int MB_ICONERROR = 0x10;

    public const int COLOR_WINDOW = 5;
    public const int IDC_ARROW = 32512;

    public const int SM_CXSCREEN = 0;
    public const int SM_CYSCREEN = 1;

    public const int SWP_NOSIZE = 0x0001;
    public const int SWP_NOMOVE = 0x0002;
    public const int SWP_NOZORDER = 0x0004;
    public const int SWP_NOACTIVATE = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WNDCLASSEX
    {
        public int cbSize;
        public int style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public IntPtr lpszMenuName;
        public IntPtr lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public int message;
        public IntPtr wParam;
        public IntPtr lParam;
        public int time;
        public int ptX;
        public int ptY;
    }

    public delegate IntPtr WndProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW")]
    public static partial ushort RegisterClassEx(ref WNDCLASSEX wcx);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr CreateWindowEx(int exStyle, string className, string? windowName, int style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    public static partial IntPtr DefWindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
    public static partial int GetMessage(out MSG msg, IntPtr hWnd, int min, int max);

    [LibraryImport("user32.dll", EntryPoint = "TranslateMessage")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TranslateMessage(ref MSG msg);

    [LibraryImport("user32.dll", EntryPoint = "IsDialogMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsDialogMessage(IntPtr hWnd, ref MSG msg);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    public static partial IntPtr DispatchMessage(ref MSG msg);

    [LibraryImport("user32.dll", EntryPoint = "PostQuitMessage")]
    public static partial void PostQuitMessage(int exitCode);

    [LibraryImport("user32.dll", EntryPoint = "DestroyWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyWindow(IntPtr hWnd);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowText(IntPtr hWnd, string text);

    [LibraryImport("user32.dll", EntryPoint = "ShowWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(IntPtr hWnd, int cmd);

    [LibraryImport("user32.dll", EntryPoint = "EnableWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnableWindow(IntPtr hWnd, [MarshalAs(UnmanagedType.Bool)] bool enable);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    public static partial IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", EntryPoint = "LoadCursorW")]
    public static partial IntPtr LoadCursor(IntPtr instance, IntPtr cursorName);

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int MessageBox(IntPtr hWnd, string text, string caption, int type);

    [LibraryImport("user32.dll", EntryPoint = "GetSystemMetrics")]
    public static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, int flags);

    [LibraryImport("user32.dll", EntryPoint = "GetDpiForWindow")]
    public static partial uint GetDpiForWindow(IntPtr hWnd);

    // Windows 10 1607 and later. Falls back to the unscaled call when unavailable.
    [LibraryImport("user32.dll", EntryPoint = "AdjustWindowRectExForDpi", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AdjustWindowRectExForDpi(ref RECT rect,
        int style, [MarshalAs(UnmanagedType.Bool)] bool menu, int exStyle, uint dpi);

    [LibraryImport("user32.dll", EntryPoint = "AdjustWindowRect")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AdjustWindowRect(ref RECT rect,
        int style, [MarshalAs(UnmanagedType.Bool)] bool menu);

    [LibraryImport("gdi32.dll", EntryPoint = "CreateFontW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr CreateFont(int height, int width, int escapement, int orientation, int weight,
        uint italic, uint underline, uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision,
        uint quality, uint pitchAndFamily, string faceName);

    [LibraryImport("gdi32.dll", EntryPoint = "DeleteObject")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(IntPtr handle);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr GetModuleHandle(string? moduleName);

    [LibraryImport("kernel32.dll", EntryPoint = "GetUserDefaultUILanguage")]
    public static partial ushort GetUserDefaultUILanguage();

    [LibraryImport("comctl32.dll", EntryPoint = "InitCommonControlsEx")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool InitCommonControlsEx(ref INITCOMMONCONTROLSEX icc);

    [StructLayout(LayoutKind.Sequential)]
    public struct INITCOMMONCONTROLSEX
    {
        public int dwSize;
        public int dwICC;
    }

    public const int ICC_PROGRESS_CLASS = 0x20;
    public const int ICC_STANDARD_CLASSES = 0x4000;

    // --- The custom-drawn window. --------------------------------------------------------

    public const int WS_CLIPCHILDREN = 0x02000000;
    public const int SS_OWNERDRAW = 0x0000000D;
    public const int SW_MINIMIZE = 6;

    public const int WM_PAINT = 0x000F;
    public const int WM_ERASEBKGND = 0x0014;
    public const int WM_SYSCOLORCHANGE = 0x0015;
    public const int WM_SETTINGCHANGE = 0x001A;
    public const int WM_DRAWITEM = 0x002B;
    public const int WM_NOTIFY = 0x004E;
    public const int WM_SETICON = 0x0080;
    public const int WM_NCCALCSIZE = 0x0083;
    public const int WM_NCHITTEST = 0x0084;
    public const int WM_NCACTIVATE = 0x0086;
    public const int WM_TIMER = 0x0113;
    public const int WM_QUERYUISTATE = 0x0129;
    public const int WM_CTLCOLOREDIT = 0x0133;
    public const int WM_CTLCOLORBTN = 0x0135;
    public const int WM_DPICHANGED = 0x02E0;
    public const int WM_THEMECHANGED = 0x031A;
    public const int WM_INSTALL_STAGE = WM_APP + 2;

    public const int HTCLIENT = 1;
    public const int HTCAPTION = 2;
    public const int UISF_HIDEFOCUS = 0x1;

    public const int NM_CUSTOMDRAW = -12;
    public const int CDDS_PREPAINT = 0x1;
    public const int CDRF_DODEFAULT = 0x0;
    public const int CDRF_SKIPDEFAULT = 0x4;

    public const int BM_GETSTATE = 0x00F2;
    public const int BST_PUSHED = 0x0004;
    public const int BST_HOT = 0x0200;

    public const int EM_SETMARGINS = 0x00D3;
    public const int EC_LEFTMARGIN = 0x1;
    public const int EC_RIGHTMARGIN = 0x2;

    public const uint DT_CENTER = 0x1;
    public const uint DT_VCENTER = 0x4;
    public const uint DT_WORDBREAK = 0x10;
    public const uint DT_SINGLELINE = 0x20;
    public const uint DT_CALCRECT = 0x400;
    public const uint DT_NOPREFIX = 0x800;
    public const uint DT_EDITCONTROL = 0x2000;
    public const uint DT_PATH_ELLIPSIS = 0x4000;
    public const uint DT_END_ELLIPSIS = 0x8000;

    public const uint RDW_INVALIDATE = 0x0001;
    public const uint RDW_ALLCHILDREN = 0x0080;

    public const uint MONITOR_DEFAULTTONEAREST = 2;
    public const uint SPI_GETHIGHCONTRAST = 0x0042;
    public const uint SPI_GETCLIENTAREAANIMATION = 0x1042;
    public const uint HCF_HIGHCONTRASTON = 0x1;

    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWA_BORDER_COLOR = 34;
    public const int DWMWCP_ROUND = 2;

    public const int COLOR_WINDOWTEXT = 8;
    public const int COLOR_HIGHLIGHT = 13;
    public const int COLOR_HIGHLIGHTTEXT = 14;
    public const int COLOR_BTNFACE = 15;
    public const int COLOR_GRAYTEXT = 17;

    public const int IMAGE_ICON = 1;
    public const int SM_CXICON = 11;
    public const int SM_CXSMICON = 49;

    public const int TRANSPARENT = 1;
    public const uint SRCCOPY = 0x00CC0020;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct PAINTSTRUCT
    {
        public IntPtr hdc;
        public int fErase;
        public RECT rcPaint;
        public int fRestore;
        public int fIncUpdate;
        public fixed byte rgbReserved[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NMHDR
    {
        public IntPtr hwndFrom;
        public nuint idFrom;
        public int code;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NMCUSTOMDRAW
    {
        public NMHDR hdr;
        public int dwDrawStage;
        public IntPtr hdc;
        public RECT rc;
        public nuint dwItemSpec;
        public uint uItemState;
        public IntPtr lItemlParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DRAWITEMSTRUCT
    {
        public uint CtlType;
        public uint CtlID;
        public uint itemID;
        public uint itemAction;
        public uint itemState;
        public IntPtr hwndItem;
        public IntPtr hDC;
        public RECT rcItem;
        public nuint itemData;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HIGHCONTRAST
    {
        public int cbSize;
        public uint dwFlags;
        public IntPtr lpszDefaultScheme;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MARGINS
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [LibraryImport("user32.dll", EntryPoint = "BeginPaint")]
    public static partial IntPtr BeginPaint(IntPtr hWnd, out PAINTSTRUCT paint);

    [LibraryImport("user32.dll", EntryPoint = "EndPaint")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT paint);

    [LibraryImport("user32.dll", EntryPoint = "InvalidateRect")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool InvalidateRect(IntPtr hWnd, IntPtr rect, [MarshalAs(UnmanagedType.Bool)] bool erase);

    [LibraryImport("user32.dll", EntryPoint = "InvalidateRect")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool InvalidateRect(IntPtr hWnd, ref RECT rect, [MarshalAs(UnmanagedType.Bool)] bool erase);

    [LibraryImport("user32.dll", EntryPoint = "RedrawWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RedrawWindow(IntPtr hWnd, IntPtr rect, IntPtr region, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "SetTimer")]
    public static partial nuint SetTimer(IntPtr hWnd, nuint id, uint elapse, IntPtr callback);

    [LibraryImport("user32.dll", EntryPoint = "KillTimer")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool KillTimer(IntPtr hWnd, nuint id);

    [LibraryImport("user32.dll", EntryPoint = "DrawTextW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int DrawText(IntPtr hdc, string text, int count, ref RECT rect, uint format);

    [LibraryImport("user32.dll", EntryPoint = "SetFocus")]
    public static partial IntPtr SetFocus(IntPtr hWnd);

    [LibraryImport("user32.dll", EntryPoint = "IsWindowEnabled")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowEnabled(IntPtr hWnd);

    [LibraryImport("user32.dll", EntryPoint = "IsWindowVisible")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(IntPtr hWnd);

    [LibraryImport("user32.dll", EntryPoint = "GetCursorPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out POINT point);

    [LibraryImport("user32.dll", EntryPoint = "MonitorFromPoint")]
    public static partial IntPtr MonitorFromPoint(POINT point, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SystemParametersInfo(uint action, uint param, ref int value, uint winIni);

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SystemParametersInfo(uint action, uint param, ref HIGHCONTRAST value, uint winIni);

    [LibraryImport("user32.dll", EntryPoint = "GetSysColor")]
    public static partial uint GetSysColor(int index);

    [LibraryImport("user32.dll", EntryPoint = "LoadImageW")]
    public static partial IntPtr LoadImage(IntPtr instance, IntPtr name, int type, int cx, int cy, uint load);

    [LibraryImport("user32.dll", EntryPoint = "DestroyIcon")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyIcon(IntPtr icon);

    [LibraryImport("gdi32.dll", EntryPoint = "CreateCompatibleDC")]
    public static partial IntPtr CreateCompatibleDC(IntPtr hdc);

    [LibraryImport("gdi32.dll", EntryPoint = "DeleteDC")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteDC(IntPtr hdc);

    [LibraryImport("gdi32.dll", EntryPoint = "SelectObject")]
    public static partial IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [LibraryImport("gdi32.dll", EntryPoint = "CreateDIBSection")]
    public static partial IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER info, uint usage,
        out IntPtr bits, IntPtr section, uint offset);

    [LibraryImport("gdi32.dll", EntryPoint = "BitBlt")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool BitBlt(IntPtr destination, int x, int y, int width, int height,
        IntPtr source, int sourceX, int sourceY, uint rop);

    [LibraryImport("gdi32.dll", EntryPoint = "SetTextColor")]
    public static partial uint SetTextColor(IntPtr hdc, uint color);

    [LibraryImport("gdi32.dll", EntryPoint = "SetBkColor")]
    public static partial uint SetBkColor(IntPtr hdc, uint color);

    [LibraryImport("gdi32.dll", EntryPoint = "SetBkMode")]
    public static partial int SetBkMode(IntPtr hdc, int mode);

    [LibraryImport("gdi32.dll", EntryPoint = "GdiFlush")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GdiFlush();

    [LibraryImport("gdi32.dll", EntryPoint = "CreateSolidBrush")]
    public static partial IntPtr CreateSolidBrush(uint color);

    [LibraryImport("gdi32.dll", EntryPoint = "GetTextFaceW")]
    public static unsafe partial int GetTextFace(IntPtr hdc, int count, char* face);

    [LibraryImport("dwmapi.dll", EntryPoint = "DwmSetWindowAttribute")]
    public static partial int DwmSetWindowAttribute(IntPtr hWnd, int attribute, ref int value, int size);

    [LibraryImport("dwmapi.dll", EntryPoint = "DwmExtendFrameIntoClientArea")]
    public static partial int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS margins);

    [LibraryImport("uxtheme.dll", EntryPoint = "SetWindowTheme", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int SetWindowTheme(IntPtr hWnd, string? subAppName, string? subIdList);
}

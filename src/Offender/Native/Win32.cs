using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Offender.Native;

/// <summary>
/// user32 / gdi32 / kernel32 / dwmapi / shell32 interop.
/// Every signature here is deliberately all-blittable (nint, int, uint, pointers)
/// so LibraryImport generates zero marshalling code and NativeAOT stays happy.
/// BOOL-returning calls come back as int; nonzero means success.
/// </summary>
internal static unsafe partial class Win32
{
    // ---- window styles -------------------------------------------------
    public const uint WS_POPUP = 0x80000000;
    public const uint WS_VISIBLE = 0x10000000;
    public const uint WS_CLIPCHILDREN = 0x02000000;
    public const uint WS_CHILD = 0x40000000;

    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;

    public const uint WS_EX_TOOLWINDOW = 0x00000080;
    public const uint WS_EX_TOPMOST = 0x00000008;
    public const uint WS_EX_LAYERED = 0x00080000;
    public const uint WS_EX_NOACTIVATE = 0x08000000;

    public const uint CS_HREDRAW = 0x0002;
    public const uint CS_VREDRAW = 0x0001;
    public const uint CS_DBLCLKS = 0x0008;

    // ---- messages ------------------------------------------------------
    public const uint WM_DESTROY = 0x0002;
    public const uint WM_SIZE = 0x0005;
    public const uint WM_PAINT = 0x000F;
    public const uint WM_CLOSE = 0x0010;
    public const uint WM_ERASEBKGND = 0x0014;
    public const uint WM_SETTINGCHANGE = 0x001A;
    public const uint WM_NCHITTEST = 0x0084;
    public const uint WM_NCRBUTTONUP = 0x00A5;
    public const uint WM_COMMAND = 0x0111;
    public const uint WM_TIMER = 0x0113;
    public const uint WM_KEYDOWN = 0x0100;
    public const uint WM_ACTIVATE = 0x0006;
    public const uint WM_KILLFOCUS = 0x0008;
    public const uint WM_SETCURSOR = 0x0020;
    public const uint WM_LBUTTONDOWN = 0x0201;
    public const uint WM_LBUTTONUP = 0x0202;
    public const uint WM_RBUTTONUP = 0x0205;
    public const uint WM_MOUSEMOVE = 0x0200;
    public const uint WM_MOUSELEAVE = 0x02A3;

    public const uint TME_LEAVE = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    public struct TRACKMOUSEEVENT
    {
        public uint cbSize;
        public uint dwFlags;
        public nint hwndTrack;
        public uint dwHoverTime;
    }
    public const uint WM_EXITSIZEMOVE = 0x0232;
    public const uint WM_DISPLAYCHANGE = 0x007E;
    public const uint WM_DPICHANGED = 0x02E0;
    public const uint WM_APP = 0x8000;

    // ---- hit test ------------------------------------------------------
    public const int HTCLIENT = 1;
    public const int HTCAPTION = 2;

    // ---- ShowWindow ----------------------------------------------------
    public const int SW_HIDE = 0;
    public const int SW_SHOWNOACTIVATE = 4;

    // ---- SetWindowPos --------------------------------------------------
    public static readonly nint HWND_TOPMOST = -1;
    public static readonly nint HWND_NOTOPMOST = -2;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;

    // ---- layered windows ----------------------------------------------
    public const uint LWA_ALPHA = 0x00000002;
    public const uint ULW_ALPHA = 0x00000002;
    public const byte AC_SRC_OVER = 0x00;
    public const byte AC_SRC_ALPHA = 0x01;

    [StructLayout(LayoutKind.Sequential)]
    public struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    // ---- undocumented acrylic/blur accent (Win10 1803+) ----------------
    // There is no public API for acrylic on a plain HWND; this is the same private
    // entry point every Windows theming tool uses. It is wrapped in a feature check and
    // degrades to a solid surface if the call fails.
    public const int WCA_ACCENT_POLICY = 19;
    public const int ACCENT_ENABLE_BLURBEHIND = 3;
    public const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;

    [StructLayout(LayoutKind.Sequential)]
    public struct ACCENT_POLICY
    {
        public int AccentState;
        public int AccentFlags;
        public uint GradientColor;   // 0xAABBGGRR
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWCOMPOSITIONATTRIBDATA
    {
        public int Attribute;
        public void* Data;
        public uint SizeOfData;
    }

    // ---- DWM -----------------------------------------------------------
    public const uint DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWCP_ROUND = 2;

    // ---- menus ---------------------------------------------------------
    public const uint MF_STRING = 0x0000;
    public const uint MF_SEPARATOR = 0x0800;
    public const uint MF_CHECKED = 0x0008;
    public const uint MF_POPUP = 0x0010;
    public const uint TPM_RIGHTBUTTON = 0x0002;
    public const uint TPM_RETURNCMD = 0x0100;

    // ---- GDI -----------------------------------------------------------
    public const int SRCCOPY = 0x00CC0020;
    public const uint DIB_RGB_COLORS = 0;
    public const int TRANSPARENT = 1;

    public const int DEFAULT_CHARSET = 1;
    public const int OUT_TT_PRECIS = 4;
    public const int CLIP_DEFAULT_PRECIS = 0;
    public const int CLEARTYPE_QUALITY = 5;
    // ClearType writes colour-fringed subpixels, which turn into visible rainbow edges
    // once the surface is composited with per-pixel alpha. Greyscale AA is the right
    // choice for a layered window.
    public const int ANTIALIASED_QUALITY = 4;
    public const int DEFAULT_PITCH = 0;
    public const int FW_NORMAL = 400;
    public const int FW_SEMIBOLD = 600;

    public const int PS_SOLID = 0;

    public const int LOGPIXELSY = 90;

    public const uint MONITOR_DEFAULTTONEAREST = 2;

    // ---- system metrics ------------------------------------------------
    public const int SM_CXSMICON = 49;
    public const int SM_CYSMICON = 50;

    // ---- cursors -------------------------------------------------------
    public static readonly nint IDC_ARROW = 32512;

    // =====================================================================
    // structs
    // =====================================================================

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct SIZE { public int cx; public int cy; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public nint hwnd;
        public uint message;
        public nuint wParam;
        public nint lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public char* lpszMenuName;
        public char* lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PAINTSTRUCT
    {
        public nint hdc;
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
    public struct ICONINFO
    {
        public int fIcon;
        public int xHotspot;
        public int yHotspot;
        public nint hbmMask;
        public nint hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    // NOTIFYICONDATAW, V3 size (Vista+). Inline char buffers keep it blittable.
    [StructLayout(LayoutKind.Sequential)]
    public struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        public fixed char szTip[128];
        public uint dwState;
        public uint dwStateMask;
        public fixed char szInfo[256];
        public uint uVersionOrTimeout;
        public fixed char szInfoTitle[64];
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    public const uint NIM_ADD = 0x00000000;
    public const uint NIM_MODIFY = 0x00000001;
    public const uint NIM_DELETE = 0x00000002;
    public const uint NIF_MESSAGE = 0x00000001;
    public const uint NIF_ICON = 0x00000002;
    public const uint NIF_TIP = 0x00000004;

    // =====================================================================
    // user32
    // =====================================================================

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
    public static partial ushort RegisterClassExW(WNDCLASSEXW* lpwcx);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true)]
    public static partial nint CreateWindowExW(
        uint dwExStyle, char* lpClassName, char* lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    public static partial nint DefWindowProcW(nint hWnd, uint msg, nuint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    public static partial int DestroyWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    public static partial void PostQuitMessage(int nExitCode);

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
    public static partial int GetMessageW(MSG* lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [LibraryImport("user32.dll")]
    public static partial int TranslateMessage(MSG* lpMsg);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    public static partial nint DispatchMessageW(MSG* lpMsg);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    public static partial int PostMessageW(nint hWnd, uint msg, nuint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    public static partial int ShowWindow(nint hWnd, int nCmdShow);

    [LibraryImport("user32.dll")]
    public static partial int IsWindowVisible(nint hWnd);

    [LibraryImport("user32.dll")]
    public static partial int SetWindowPos(nint hWnd, nint hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [LibraryImport("user32.dll")]
    public static partial int InvalidateRect(nint hWnd, RECT* lpRect, int bErase);

    [LibraryImport("user32.dll")]
    public static partial nint BeginPaint(nint hWnd, PAINTSTRUCT* lpPaint);

    [LibraryImport("user32.dll")]
    public static partial int EndPaint(nint hWnd, PAINTSTRUCT* lpPaint);

    [LibraryImport("user32.dll")]
    public static partial int GetClientRect(nint hWnd, RECT* lpRect);

    [LibraryImport("user32.dll")]
    public static partial int GetWindowRect(nint hWnd, RECT* lpRect);

    [LibraryImport("user32.dll")]
    public static partial int SetLayeredWindowAttributes(nint hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [LibraryImport("user32.dll")]
    public static partial int UpdateLayeredWindow(
        nint hWnd, nint hdcDst, POINT* pptDst, SIZE* psize,
        nint hdcSrc, POINT* pptSrc, uint crKey, BLENDFUNCTION* pblend, uint dwFlags);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowCompositionAttribute", SetLastError = true)]
    public static partial int SetWindowCompositionAttribute(nint hwnd, WINDOWCOMPOSITIONATTRIBDATA* data);

    [LibraryImport("user32.dll", EntryPoint = "FindWindowExW")]
    public static partial nint FindWindowExW(nint hwndParent, nint hwndChildAfter, char* lpszClass, char* lpszWindow);

    [LibraryImport("user32.dll")]
    public static partial nint SetParent(nint hWndChild, nint hWndNewParent);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static partial nint GetWindowLongPtrW(nint hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static partial nint SetWindowLongPtrW(nint hWnd, int nIndex, nint dwNewLong);

    [LibraryImport("user32.dll")]
    public static partial int IsWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    public static partial int SetTimer(nint hWnd, nuint nIDEvent, uint uElapse, nint lpTimerFunc);

    [LibraryImport("user32.dll")]
    public static partial int KillTimer(nint hWnd, nuint uIDEvent);

    [LibraryImport("user32.dll")]
    public static partial nint SetCapture(nint hWnd);

    [LibraryImport("user32.dll", EntryPoint = "TrackMouseEvent")]
    public static partial int TrackMouseEvent(TRACKMOUSEEVENT* lpEventTrack);

    [LibraryImport("user32.dll")]
    public static partial int ScreenToClient(nint hWnd, POINT* lpPoint);

    [LibraryImport("user32.dll", EntryPoint = "LoadCursorW")]
    public static partial nint LoadCursorW(nint hInstance, nint lpCursorName);

    [LibraryImport("user32.dll")]
    public static partial int GetCursorPos(POINT* lpPoint);

    [LibraryImport("user32.dll")]
    public static partial nint CreatePopupMenu();

    [LibraryImport("user32.dll")]
    public static partial int DestroyMenu(nint hMenu);

    [LibraryImport("user32.dll", EntryPoint = "AppendMenuW")]
    public static partial int AppendMenuW(nint hMenu, uint uFlags, nuint uIDNewItem, char* lpNewItem);

    [LibraryImport("user32.dll")]
    public static partial int TrackPopupMenu(nint hMenu, uint uFlags, int x, int y, int nReserved, nint hWnd, nint prcRect);

    [LibraryImport("user32.dll")]
    public static partial int SetForegroundWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    public static partial nint MonitorFromWindow(nint hwnd, uint dwFlags);

    [LibraryImport("user32.dll")]
    public static partial nint MonitorFromPoint(POINT pt, uint dwFlags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    public static partial int GetMonitorInfoW(nint hMonitor, MONITORINFO* lpmi);

    [LibraryImport("user32.dll")]
    public static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    public static partial int ReleaseCapture();

    [LibraryImport("user32.dll")]
    public static partial int GetSystemMetrics(int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW")]
    public static partial uint RegisterWindowMessageW(char* lpString);

    [LibraryImport("user32.dll")]
    public static partial int DestroyIcon(nint hIcon);

    [LibraryImport("user32.dll")]
    public static partial nint CreateIconIndirect(ICONINFO* piconinfo);

    [LibraryImport("user32.dll")]
    public static partial nint GetDC(nint hWnd);

    [LibraryImport("user32.dll")]
    public static partial int ReleaseDC(nint hWnd, nint hDC);

    [LibraryImport("user32.dll")]
    public static partial int FillRect(nint hDC, RECT* lprc, nint hbr);

    // =====================================================================
    // gdi32
    // =====================================================================

    [LibraryImport("gdi32.dll")]
    public static partial nint CreateCompatibleDC(nint hdc);

    [LibraryImport("gdi32.dll")]
    public static partial nint CreateCompatibleBitmap(nint hdc, int cx, int cy);

    [LibraryImport("gdi32.dll")]
    public static partial nint CreateBitmap(int nWidth, int nHeight, uint nPlanes, uint nBitCount, void* lpBits);

    [LibraryImport("gdi32.dll")]
    public static partial int DeleteDC(nint hdc);

    [LibraryImport("gdi32.dll")]
    public static partial nint SelectObject(nint hdc, nint h);

    [LibraryImport("gdi32.dll")]
    public static partial int DeleteObject(nint ho);

    [LibraryImport("gdi32.dll")]
    public static partial nint CreateDIBSection(nint hdc, BITMAPINFOHEADER* pbmi, uint usage, void** ppvBits, nint hSection, uint offset);

    [LibraryImport("gdi32.dll")]
    public static partial int BitBlt(nint hdc, int x, int y, int cx, int cy, nint hdcSrc, int x1, int y1, int rop);

    public const int NULL_BRUSH = 5;
    public const int NULL_PEN = 8;

    [LibraryImport("gdi32.dll")]
    public static partial nint GetStockObject(int i);

    [LibraryImport("gdi32.dll")]
    public static partial nint CreateSolidBrush(uint color);

    [LibraryImport("gdi32.dll")]
    public static partial nint CreatePen(int style, int width, uint color);

    [LibraryImport("gdi32.dll")]
    public static partial uint SetTextColor(nint hdc, uint color);

    [LibraryImport("gdi32.dll")]
    public static partial int SetBkMode(nint hdc, int mode);

    [LibraryImport("gdi32.dll", EntryPoint = "ExtTextOutW")]
    public static partial int ExtTextOutW(nint hdc, int x, int y, uint options, RECT* lprect, char* lpString, uint c, int* lpDx);

    [LibraryImport("gdi32.dll", EntryPoint = "GetTextExtentPoint32W")]
    public static partial int GetTextExtentPoint32W(nint hdc, char* lpString, int c, SIZE* psizl);

    [LibraryImport("gdi32.dll", EntryPoint = "CreateFontW")]
    public static partial nint CreateFontW(
        int cHeight, int cWidth, int cEscapement, int cOrientation, int cWeight,
        uint bItalic, uint bUnderline, uint bStrikeOut, uint iCharSet,
        uint iOutPrecision, uint iClipPrecision, uint iQuality, uint iPitchAndFamily,
        char* pszFaceName);

    [LibraryImport("gdi32.dll")]
    public static partial int Polyline(nint hdc, POINT* apt, int cpt);

    [LibraryImport("gdi32.dll")]
    public static partial int Polygon(nint hdc, POINT* apt, int cpt);

    [LibraryImport("gdi32.dll")]
    public static partial int MoveToEx(nint hdc, int x, int y, POINT* lppt);

    [LibraryImport("gdi32.dll")]
    public static partial int LineTo(nint hdc, int x, int y);

    [LibraryImport("gdi32.dll")]
    public static partial int Rectangle(nint hdc, int left, int top, int right, int bottom);

    [LibraryImport("gdi32.dll")]
    public static partial int RoundRect(nint hdc, int left, int top, int right, int bottom, int width, int height);

    [LibraryImport("gdi32.dll")]
    public static partial int Ellipse(nint hdc, int left, int top, int right, int bottom);

    [LibraryImport("gdi32.dll")]
    public static partial nint CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);

    [LibraryImport("user32.dll")]
    public static partial int SetWindowRgn(nint hWnd, nint hRgn, int bRedraw);

    [LibraryImport("gdi32.dll")]
    public static partial int GetDeviceCaps(nint hdc, int index);

    // =====================================================================
    // kernel32 / dwmapi / shell32
    // =====================================================================

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW")]
    public static partial nint GetModuleHandleW(char* lpModuleName);

    [LibraryImport("kernel32.dll")]
    public static partial int SetProcessWorkingSetSize(nint hProcess, nint dwMin, nint dwMax);

    [LibraryImport("kernel32.dll")]
    public static partial nint GetCurrentProcess();

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmSetWindowAttribute(nint hwnd, uint dwAttribute, void* pvAttribute, uint cbAttribute);

    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW")]
    public static partial int Shell_NotifyIconW(uint dwMessage, NOTIFYICONDATAW* lpData);

    // =====================================================================
    // helpers
    // =====================================================================

    /// <summary>COLORREF is 0x00BBGGRR, the reverse of the usual RGB order.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Rgb(byte r, byte g, byte b) => (uint)(r | (g << 8) | (b << 16));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int LoWord(nint v) => (short)(v & 0xFFFF);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int HiWord(nint v) => (short)((v >> 16) & 0xFFFF);
}

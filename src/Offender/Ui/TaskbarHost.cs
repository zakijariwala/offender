using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Offender.Native;
using Offender.Sampling;

namespace Offender.Ui;

/// <summary>
/// The readout that sits in the taskbar.
///
/// It is deliberately NOT a child of Shell_TrayWnd. Reparenting a window into the shell's
/// taskbar is the usual trick, and it is the fragile part of every tool that does it: the
/// child is destroyed whenever Explorer restarts, it gets clipped by the shell's own
/// layout, and UpdateLayeredWindow does not composite reliably for a cross-process child.
///
/// Instead this is an ordinary top-level, always-on-top, non-activating layered window
/// parked over the taskbar strip and repositioned each tick. Visually identical, and it
/// owns its own lifetime -- an Explorer restart moves the taskbar, and the next tick
/// simply follows it.
///
/// Per-pixel alpha means only the glyphs composite onto the taskbar, so it works against
/// a light taskbar, a dark one, or an accent-coloured one without ever guessing a
/// background colour.
/// </summary>
internal sealed unsafe class TaskbarHost : IDisposable
{
    private const int WidthLogical = 96;

    private readonly Config _config;
    private Theme _theme;

    private nint _hwnd;
    private nint _memDc;
    private nint _dib;
    private nint _oldBitmap;
    private uint* _pixels;
    private nint _fontSmall;
    private nint _fontLarge;

    private int _w, _h;
    private int _x, _y;
    private float _scale = 1f;
    private readonly char[] _scratch = new char[64];

    public TaskbarHost(Theme theme, Config config)
    {
        _theme = theme;
        _config = config;
    }

    public bool Alive => _hwnd != 0 && Win32.IsWindow(_hwnd) != 0;

    public void SetTheme(Theme theme) => _theme = theme;

    private int S(int logical) => (int)MathF.Round(logical * _scale);

    // ---------------------------------------------------------------- lifecycle

    public bool Create()
    {
        nint tray = FindTaskbar();
        if (tray == 0) return false;

        nint hInstance = Win32.GetModuleHandleW(null);

        fixed (char* className = "OffenderTaskbar")
        fixed (char* windowName = "OffenderTaskbar")
        {
            var wc = new Win32.WNDCLASSEXW
            {
                cbSize = (uint)sizeof(Win32.WNDCLASSEXW),
                style = 0,
                lpfnWndProc = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint>)&StaticWndProc,
                hInstance = hInstance,
                hCursor = Win32.LoadCursorW(0, Win32.IDC_ARROW),
                hbrBackground = 0,
                lpszClassName = className,
            };
            Win32.RegisterClassExW(&wc);

            _hwnd = Win32.CreateWindowExW(
                Win32.WS_EX_LAYERED | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_TOPMOST,
                className, windowName,
                Win32.WS_POPUP,
                0, 0, 10, 10,
                0, 0, hInstance, 0);
        }

        if (_hwnd == 0) return false;

        _scale = Win32.GetDpiForWindow(tray) / 96f;
        if (_scale <= 0) _scale = 1f;

        if (!ComputePlacement()) { Destroy(); return false; }

        BuildSurface();
        if (_memDc == 0) { Destroy(); return false; }

        Win32.ShowWindow(_hwnd, Win32.SW_SHOWNOACTIVATE);
        return true;
    }

    private static nint FindTaskbar()
    {
        fixed (char* cls = "Shell_TrayWnd")
            return Win32.FindWindowExW(0, 0, cls, null);
    }

    /// <summary>
    /// Works out where to sit: immediately left of the notification area, vertically
    /// centred in the taskbar. Returns false when there is no usable horizontal taskbar,
    /// which is also how a vertical taskbar is handled -- there is nowhere sensible to put
    /// a wide readout, so the feature just stays off.
    /// </summary>
    private bool ComputePlacement()
    {
        nint tray = FindTaskbar();
        if (tray == 0) return false;

        Win32.RECT trayRect;
        if (Win32.GetWindowRect(tray, &trayRect) == 0) return false;
        if (trayRect.Height > trayRect.Width) return false;

        int w = S(WidthLogical);
        int h = Math.Max(S(16), trayRect.Height - S(6));

        int rightEdge = trayRect.Right;
        fixed (char* cls = "TrayNotifyWnd")
        {
            nint notify = Win32.FindWindowExW(tray, 0, cls, null);
            if (notify != 0)
            {
                Win32.RECT nr;
                if (Win32.GetWindowRect(notify, &nr) != 0 && nr.Left > trayRect.Left)
                    rightEdge = nr.Left;
            }
        }

        _x = rightEdge - w - S(8);
        _y = trayRect.Top + (trayRect.Height - h) / 2;

        if (w != _w || h != _h)
        {
            _w = w;
            _h = h;
            ReleaseSurface();
            BuildSurface();
        }
        return true;
    }

    // ---------------------------------------------------------------- painting

    public void Update(Snapshot? s)
    {
        if (!Alive) return;

        // The taskbar moves, resizes, auto-hides and changes DPI without telling us, so
        // the placement is re-derived every tick. It is two cheap rect queries.
        if (!ComputePlacement() || _memDc == 0) return;

        int px = _w * _h;
        for (int i = 0; i < px; i++) _pixels[i] = 0;

        Win32.SetBkMode(_memDc, Win32.TRANSPARENT);

        if (s is null)
        {
            DrawLine("--", _fontLarge, 0, _h, _theme.TextDim);
        }
        else if (_config.Tray == TrayMode.NetSpeed)
        {
            var t = new TextBuf(_scratch);
            t.Append('↓');
            t.Append(' ');
            AppendCompactRate(ref t, s.NetRx);
            DrawLine(t.Span, _fontSmall, 0, _h / 2, _theme.NetDown);

            t.Clear();
            t.Append('↑');
            t.Append(' ');
            AppendCompactRate(ref t, s.NetTx);
            DrawLine(t.Span, _fontSmall, _h / 2, _h / 2, _theme.NetUp);
        }
        else
        {
            bool cpu = _config.Tray == TrayMode.Cpu;
            double v = cpu ? s.CpuPercent : (s.MemTotal > 0 ? s.MemUsed * 100.0 / s.MemTotal : 0);

            var t = new TextBuf(_scratch);
            t.Append(cpu ? "CPU " : "RAM ");
            t.AppendPercent(v, "F0");

            uint color = v >= 90 ? _theme.Hot : v >= 70 ? _theme.Warn : _theme.TextPrimary;
            DrawLine(t.Span, _fontLarge, 0, _h, color);
        }

        RecoverAlpha();
        Push();
    }

    private void DrawLine(ReadOnlySpan<char> text, nint font, int y, int height, uint color)
    {
        if (text.Length == 0 || font == 0) return;

        nint old = Win32.SelectObject(_memDc, font);
        Win32.SetTextColor(_memDc, color);

        Win32.SIZE size;
        fixed (char* p = text)
        {
            Win32.GetTextExtentPoint32W(_memDc, p, text.Length, &size);
            int x = _w - size.cx - 2;          // right-aligned, like the clock beside it
            if (x < 0) x = 0;
            int ty = y + (height - size.cy) / 2;
            Win32.ExtTextOutW(_memDc, x, ty, 0, null, p, (uint)text.Length, null);
        }

        Win32.SelectObject(_memDc, old);
    }

    /// <summary>
    /// GDI writes no alpha channel, so it is recovered from the ink's own brightness --
    /// the same trick the tray icon and the Clear theme use, and the reason the taskbar
    /// text is drawn in bright theme colours.
    /// </summary>
    private void RecoverAlpha()
    {
        int px = _w * _h;
        for (int i = 0; i < px; i++)
        {
            uint p = _pixels[i];
            uint r = (p >> 16) & 0xFF, g = (p >> 8) & 0xFF, b = p & 0xFF;

            uint a = r > g ? r : g;
            if (b > a) a = b;

            // Drawn on black, so the channels are already premultiplied.
            _pixels[i] = a == 0 ? 0 : (a << 24) | (r << 16) | (g << 8) | b;
        }
    }

    private void Push()
    {
        nint screen = Win32.GetDC(0);
        if (screen == 0) return;

        // pptDst is in screen coordinates. Passing it here both moves and repaints the
        // window in one call, which is why there is no separate SetWindowPos for position.
        var pos = new Win32.POINT { X = _x, Y = _y };
        var size = new Win32.SIZE { cx = _w, cy = _h };
        var src = new Win32.POINT { X = 0, Y = 0 };
        var blend = new Win32.BLENDFUNCTION
        {
            BlendOp = Win32.AC_SRC_OVER,
            BlendFlags = 0,
            SourceConstantAlpha = 255,
            AlphaFormat = Win32.AC_SRC_ALPHA,
        };

        Win32.UpdateLayeredWindow(_hwnd, screen, &pos, &size, _memDc, &src, 0, &blend, Win32.ULW_ALPHA);
        Win32.ReleaseDC(0, screen);

        // The taskbar is itself topmost, so staying above it means re-asserting z-order.
        Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
    }

    private static void AppendCompactRate(ref TextBuf t, double bytesPerSecond)
    {
        double v = bytesPerSecond;
        char unit;

        if (v >= 1024d * 1024 * 1024) { v /= 1024d * 1024 * 1024; unit = 'G'; }
        else if (v >= 1024d * 1024) { v /= 1024d * 1024; unit = 'M'; }
        else if (v >= 1024d) { v /= 1024d; unit = 'K'; }
        else { unit = 'B'; }

        t.Append(v, v >= 10 ? "F0" : "F1");
        t.Append(unit);
    }

    // ---------------------------------------------------------------- resources

    private void BuildSurface()
    {
        if (_w <= 0 || _h <= 0 || _memDc != 0) return;

        nint screen = Win32.GetDC(0);
        _memDc = Win32.CreateCompatibleDC(screen);

        var bmi = new Win32.BITMAPINFOHEADER
        {
            biSize = (uint)sizeof(Win32.BITMAPINFOHEADER),
            biWidth = _w,
            biHeight = -_h,
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0,
        };

        void* bits;
        _dib = Win32.CreateDIBSection(_memDc, &bmi, Win32.DIB_RGB_COLORS, &bits, 0, 0);
        Win32.ReleaseDC(0, screen);

        if (_dib == 0) { Win32.DeleteDC(_memDc); _memDc = 0; return; }

        _pixels = (uint*)bits;
        _oldBitmap = Win32.SelectObject(_memDc, _dib);

        _fontSmall = MakeFont("Segoe UI", Math.Max(9, _h / 2 - 3), Win32.FW_SEMIBOLD);
        _fontLarge = MakeFont("Segoe UI", Math.Max(11, (int)(_h * 0.5)), Win32.FW_SEMIBOLD);
    }

    private static nint MakeFont(string face, int pixelHeight, int weight)
    {
        fixed (char* f = face)
        {
            return Win32.CreateFontW(
                -pixelHeight, 0, 0, 0, weight,
                0, 0, 0, Win32.DEFAULT_CHARSET,
                Win32.OUT_TT_PRECIS, Win32.CLIP_DEFAULT_PRECIS, Win32.ANTIALIASED_QUALITY,
                Win32.DEFAULT_PITCH, f);
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static nint StaticWndProc(nint hwnd, uint msg, nuint wParam, nint lParam)
        => Win32.DefWindowProcW(hwnd, msg, wParam, lParam);

    private void ReleaseSurface()
    {
        if (_fontSmall != 0) { Win32.DeleteObject(_fontSmall); _fontSmall = 0; }
        if (_fontLarge != 0) { Win32.DeleteObject(_fontLarge); _fontLarge = 0; }
        if (_memDc == 0) return;

        if (_oldBitmap != 0) Win32.SelectObject(_memDc, _oldBitmap);
        if (_dib != 0) Win32.DeleteObject(_dib);
        Win32.DeleteDC(_memDc);
        _memDc = 0; _dib = 0; _oldBitmap = 0; _pixels = null;
    }

    private void Destroy()
    {
        if (_hwnd != 0 && Win32.IsWindow(_hwnd) != 0) Win32.DestroyWindow(_hwnd);
        _hwnd = 0;
    }

    public void Dispose()
    {
        Destroy();
        ReleaseSurface();
    }
}

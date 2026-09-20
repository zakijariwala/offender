using Offender.Native;
using Offender.Sampling;

namespace Offender.Ui;

/// <summary>
/// Live numbers rendered straight into the notification area.
///
/// The icon is a 32bpp DIB redrawn each tick. GDI text does not write an alpha channel,
/// so the text is drawn white on black and the result is converted to premultiplied ARGB
/// afterwards, using the white coverage as the alpha. That keeps the glyphs antialiased
/// against whatever taskbar colour the user has, light or dark.
/// </summary>
internal sealed unsafe class TrayIcon : IDisposable
{
    public const uint CallbackMessage = Win32.WM_APP + 1;
    private const uint IconId = 1;

    private readonly nint _hwnd;
    private Win32.NOTIFYICONDATAW _nid;
    private bool _added;

    private readonly int _size;
    private nint _memDc;
    private nint _dib;
    private nint _oldBitmap;
    private uint* _pixels;
    private nint _font;
    private nint _fontSmall;
    private nint _currentIcon;

    private readonly char[] _scratch = new char[64];
    private readonly char[] _tip = new char[128];

    public TrayMode Mode { get; set; } = TrayMode.NetSpeed;

    public TrayIcon(nint hwnd)
    {
        _hwnd = hwnd;
        _size = Math.Max(16, Win32.GetSystemMetrics(Win32.SM_CXSMICON));
        BuildSurface();

        _nid = default;
        _nid.cbSize = (uint)sizeof(Win32.NOTIFYICONDATAW);
        _nid.hWnd = hwnd;
        _nid.uID = IconId;
        _nid.uFlags = Win32.NIF_MESSAGE | Win32.NIF_ICON | Win32.NIF_TIP;
        _nid.uCallbackMessage = CallbackMessage;
    }

    public void Update(Snapshot? s)
    {
        if (_memDc == 0) return;

        nint icon = RenderIcon(s);
        if (icon == 0) return;

        fixed (Win32.NOTIFYICONDATAW* nid = &_nid)
        {
            nid->hIcon = icon;
            WriteTip(nid->szTip, 128, s);

            int rc = Win32.Shell_NotifyIconW(_added ? Win32.NIM_MODIFY : Win32.NIM_ADD, nid);
            if (!_added && rc != 0) _added = true;
        }

        // The shell has copied the icon by now, so the previous one can go.
        if (_currentIcon != 0) Win32.DestroyIcon(_currentIcon);
        _currentIcon = icon;
    }

    // ---------------------------------------------------------------- drawing

    private nint RenderIcon(Snapshot? s)
    {
        // Clear to black; alpha is derived from coverage afterwards.
        int px = _size * _size;
        for (int i = 0; i < px; i++) _pixels[i] = 0;

        Win32.SetBkMode(_memDc, Win32.TRANSPARENT);
        Win32.SetTextColor(_memDc, Win32.Rgb(255, 255, 255));

        if (s is null)
        {
            DrawCentered("--", _font, (_size - _size / 2) / 2);
        }
        else if (Mode == TrayMode.NetSpeed)
        {
            // Two stacked lines: download above, upload below.
            var t = new TextBuf(_scratch);
            AppendCompactRate(ref t, s.NetRx);
            DrawCentered(t.Span, _fontSmall, 0);

            t.Clear();
            AppendCompactRate(ref t, s.NetTx);
            DrawCentered(t.Span, _fontSmall, _size / 2);
        }
        else
        {
            var t = new TextBuf(_scratch);
            double v = Mode == TrayMode.Cpu
                ? s.CpuPercent
                : (s.MemTotal > 0 ? s.MemUsed * 100.0 / s.MemTotal : 0);
            t.Append(v, "F0");
            DrawCentered(t.Span, _font, (_size - (int)(_size * 0.75)) / 2);
        }

        ApplyAlphaFromCoverage(TintFor(s));
        return MakeIcon();
    }

    private void DrawCentered(ReadOnlySpan<char> text, nint font, int y)
    {
        if (text.Length == 0) return;
        nint old = Win32.SelectObject(_memDc, font);

        Win32.SIZE size;
        fixed (char* p = text)
        {
            Win32.GetTextExtentPoint32W(_memDc, p, text.Length, &size);
            int x = (_size - size.cx) / 2;
            if (x < 0) x = 0;
            Win32.ExtTextOutW(_memDc, x, y, 0, null, p, (uint)text.Length, null);
        }

        Win32.SelectObject(_memDc, old);
    }

    /// <summary>Tray tint tracks the thing being displayed, so a red icon means "look at me".</summary>
    private uint TintFor(Snapshot? s)
    {
        if (s is null) return Win32.Rgb(0x8A, 0x90, 0x99);

        double load = Mode switch
        {
            TrayMode.Cpu => s.CpuPercent,
            TrayMode.Ram => s.MemTotal > 0 ? s.MemUsed * 100.0 / s.MemTotal : 0,
            _ => -1,
        };

        if (load < 0) return Win32.Rgb(0xE6, 0xE8, 0xEB);        // net mode: neutral
        if (load >= 90) return Win32.Rgb(0xEF, 0x44, 0x44);
        if (load >= 70) return Win32.Rgb(0xF5, 0x9E, 0x0B);
        return Win32.Rgb(0xE6, 0xE8, 0xEB);
    }

    /// <summary>
    /// Converts the white-on-black render into premultiplied ARGB in the requested colour.
    /// Coverage comes from the green channel, which is what GDI antialiasing varies.
    /// </summary>
    private void ApplyAlphaFromCoverage(uint colorRef)
    {
        // COLORREF is 0x00BBGGRR.
        uint r = colorRef & 0xFF;
        uint g = (colorRef >> 8) & 0xFF;
        uint b = (colorRef >> 16) & 0xFF;

        int px = _size * _size;
        for (int i = 0; i < px; i++)
        {
            uint coverage = (_pixels[i] >> 8) & 0xFF;
            if (coverage == 0) { _pixels[i] = 0; continue; }

            uint pr = r * coverage / 255;
            uint pg = g * coverage / 255;
            uint pb = b * coverage / 255;
            _pixels[i] = (coverage << 24) | (pr << 16) | (pg << 8) | pb;
        }
    }

    private nint MakeIcon()
    {
        // An all-zero monochrome mask means "use the colour bitmap's alpha".
        nint mask = Win32.CreateBitmap(_size, _size, 1, 1, null);
        if (mask == 0) return 0;

        // CreateIconIndirect reads the bitmap directly, so it must not be selected into
        // a DC at the time. Drop it out of the memory DC for the duration of the call.
        Win32.SelectObject(_memDc, _oldBitmap);

        var info = new Win32.ICONINFO
        {
            fIcon = 1,
            xHotspot = 0,
            yHotspot = 0,
            hbmMask = mask,
            hbmColor = _dib,
        };

        nint icon = Win32.CreateIconIndirect(&info);

        Win32.SelectObject(_memDc, _dib);
        Win32.DeleteObject(mask);
        return icon;
    }

    private static void AppendCompactRate(ref TextBuf t, double bytesPerSecond)
    {
        // Four characters at most: the tray has no room for "1023.4 KB/s".
        double v = bytesPerSecond;
        char unit;

        if (v >= 1024d * 1024 * 1024) { v /= 1024d * 1024 * 1024; unit = 'G'; }
        else if (v >= 1024d * 1024) { v /= 1024d * 1024; unit = 'M'; }
        else if (v >= 1024d) { v /= 1024d; unit = 'K'; }
        else { unit = ' '; }

        t.Append(v, v >= 100 ? "F0" : v >= 10 ? "F0" : "F1");
        if (unit != ' ') t.Append(unit);
    }

    private void WriteTip(char* dest, int capacity, Snapshot? s)
    {
        var t = new TextBuf(_tip);
        if (s is null)
        {
            t.Append("Offender - starting");
        }
        else
        {
            t.Append("CPU ");
            t.AppendPercent(s.CpuPercent, "F0");
            t.Append("  RAM ");
            t.AppendBytes(s.MemUsed);
            t.Append('\n');
            t.Append("↓ ");
            t.AppendRate(s.NetRx);
            t.Append("  ↑ ");
            t.AppendRate(s.NetTx);
            if (s.ProcCount > 0)
            {
                t.Append('\n');
                t.Append("Top: ");
                t.Append(s.Procs[0].Name);
            }
        }

        int n = Math.Min(t.Length, capacity - 1);
        t.Span[..n].CopyTo(new Span<char>(dest, capacity));
        dest[n] = '\0';
    }

    // ---------------------------------------------------------------- resources

    private void BuildSurface()
    {
        nint screen = Win32.GetDC(0);
        _memDc = Win32.CreateCompatibleDC(screen);

        var bmi = new Win32.BITMAPINFOHEADER
        {
            biSize = (uint)sizeof(Win32.BITMAPINFOHEADER),
            biWidth = _size,
            biHeight = -_size,   // top-down
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

        // Half-height for the stacked net readout, three-quarter for a single number.
        _fontSmall = MakeFont("Segoe UI", _size / 2, Win32.FW_SEMIBOLD);
        _font = MakeFont("Segoe UI", (int)(_size * 0.78), Win32.FW_SEMIBOLD);
    }

    private static nint MakeFont(string face, int pixelHeight, int weight)
    {
        fixed (char* f = face)
        {
            return Win32.CreateFontW(
                -pixelHeight, 0, 0, 0, weight,
                0, 0, 0, Win32.DEFAULT_CHARSET,
                Win32.OUT_TT_PRECIS, Win32.CLIP_DEFAULT_PRECIS, Win32.CLEARTYPE_QUALITY,
                Win32.DEFAULT_PITCH, f);
        }
    }

    public void Dispose()
    {
        if (_added)
        {
            fixed (Win32.NOTIFYICONDATAW* nid = &_nid) Win32.Shell_NotifyIconW(Win32.NIM_DELETE, nid);
            _added = false;
        }
        if (_currentIcon != 0) { Win32.DestroyIcon(_currentIcon); _currentIcon = 0; }
        if (_font != 0) { Win32.DeleteObject(_font); _font = 0; }
        if (_fontSmall != 0) { Win32.DeleteObject(_fontSmall); _fontSmall = 0; }
        if (_memDc != 0)
        {
            if (_oldBitmap != 0) Win32.SelectObject(_memDc, _oldBitmap);
            if (_dib != 0) Win32.DeleteObject(_dib);
            Win32.DeleteDC(_memDc);
            _memDc = 0; _dib = 0;
        }
    }
}

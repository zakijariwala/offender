using Offender.Native;

namespace Offender.Ui;

/// <summary>
/// A double-buffered GDI drawing surface with a cached pen/brush pool.
///
/// The settings window needs the same primitives the panel renderer uses but at a
/// different size, so the buffer and object cache live here. Fonts are deliberately not
/// owned -- they are borrowed from the renderer, since GDI font handles are shareable and
/// creating a second identical set would just be waste.
/// </summary>
internal sealed unsafe class GdiCanvas : IDisposable
{
    private nint _memDc;
    private nint _dib;
    private nint _oldBitmap;
    private int _w, _h;

    private readonly Dictionary<uint, nint> _brushes = new();
    private readonly Dictionary<uint, nint> _pens = new();

    public nint Dc => _memDc;
    public int Width => _w;
    public int Height => _h;

    public bool Ensure(nint refDc, int w, int h)
    {
        if (_memDc != 0 && w == _w && h == _h) return true;

        Release();
        if (w <= 0 || h <= 0) return false;

        _memDc = Win32.CreateCompatibleDC(refDc);
        if (_memDc == 0) return false;

        var bmi = new Win32.BITMAPINFOHEADER
        {
            biSize = (uint)sizeof(Win32.BITMAPINFOHEADER),
            biWidth = w,
            biHeight = -h,
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0,
        };

        void* bits;
        _dib = Win32.CreateDIBSection(_memDc, &bmi, Win32.DIB_RGB_COLORS, &bits, 0, 0);
        if (_dib == 0) { Win32.DeleteDC(_memDc); _memDc = 0; return false; }

        _oldBitmap = Win32.SelectObject(_memDc, _dib);
        _w = w; _h = h;
        return true;
    }

    public void Fill(uint color)
    {
        var r = new Win32.RECT { Left = 0, Top = 0, Right = _w, Bottom = _h };
        Win32.FillRect(_memDc, &r, Brush(color));
    }

    public void FillRect(int x, int y, int w, int h, uint color)
    {
        if (w <= 0 || h <= 0) return;
        var r = new Win32.RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };
        Win32.FillRect(_memDc, &r, Brush(color));
    }

    public void FillRoundRect(int x, int y, int w, int h, int radius, uint color)
    {
        nint oldBrush = Win32.SelectObject(_memDc, Brush(color));
        nint oldPen = Win32.SelectObject(_memDc, Pen(color));
        Win32.RoundRect(_memDc, x, y, x + w, y + h, radius * 2, radius * 2);
        Win32.SelectObject(_memDc, oldPen);
        Win32.SelectObject(_memDc, oldBrush);
    }

    public void StrokeRoundRect(int x, int y, int w, int h, int radius, uint color)
    {
        nint oldBrush = Win32.SelectObject(_memDc, Win32.GetStockObject(Win32.NULL_BRUSH));
        nint oldPen = Win32.SelectObject(_memDc, Pen(color));
        Win32.RoundRect(_memDc, x, y, x + w, y + h, radius * 2, radius * 2);
        Win32.SelectObject(_memDc, oldPen);
        Win32.SelectObject(_memDc, oldBrush);
    }

    public void FillCircle(int cx, int cy, int r, uint color)
    {
        nint oldBrush = Win32.SelectObject(_memDc, Brush(color));
        nint oldPen = Win32.SelectObject(_memDc, Pen(color));
        Win32.Ellipse(_memDc, cx - r, cy - r, cx + r, cy + r);
        Win32.SelectObject(_memDc, oldPen);
        Win32.SelectObject(_memDc, oldBrush);
    }

    public void StrokeCircle(int cx, int cy, int r, uint color)
    {
        nint oldBrush = Win32.SelectObject(_memDc, Win32.GetStockObject(Win32.NULL_BRUSH));
        nint oldPen = Win32.SelectObject(_memDc, Pen(color));
        Win32.Ellipse(_memDc, cx - r, cy - r, cx + r, cy + r);
        Win32.SelectObject(_memDc, oldPen);
        Win32.SelectObject(_memDc, oldBrush);
    }

    public void Text(int x, int y, ReadOnlySpan<char> text, uint color, nint font)
    {
        if (text.Length == 0) return;
        nint oldFont = Win32.SelectObject(_memDc, font);
        Win32.SetBkMode(_memDc, Win32.TRANSPARENT);
        Win32.SetTextColor(_memDc, color);
        fixed (char* p = text) Win32.ExtTextOutW(_memDc, x, y, 0, null, p, (uint)text.Length, null);
        Win32.SelectObject(_memDc, oldFont);
    }

    public int Measure(ReadOnlySpan<char> text, nint font)
    {
        if (_memDc == 0 || text.Length == 0) return 0;
        nint oldFont = Win32.SelectObject(_memDc, font);
        Win32.SIZE size;
        fixed (char* p = text) Win32.GetTextExtentPoint32W(_memDc, p, text.Length, &size);
        Win32.SelectObject(_memDc, oldFont);
        return size.cx;
    }

    public void BlitTo(nint destDc) => Win32.BitBlt(destDc, 0, 0, _w, _h, _memDc, 0, 0, Win32.SRCCOPY);

    private nint Brush(uint color)
    {
        if (_brushes.TryGetValue(color, out var b)) return b;
        b = Win32.CreateSolidBrush(color);
        _brushes[color] = b;
        return b;
    }

    private nint Pen(uint color)
    {
        if (_pens.TryGetValue(color, out var p)) return p;
        p = Win32.CreatePen(Win32.PS_SOLID, 1, color);
        _pens[color] = p;
        return p;
    }

    /// <summary>
    /// Frees the cached brushes and pens but keeps the back buffer. Used when the palette
    /// changes, since the pool is keyed by colour and the old entries are now unreachable.
    /// </summary>
    public void PurgeObjects()
    {
        foreach (var b in _brushes.Values) Win32.DeleteObject(b);
        foreach (var p in _pens.Values) Win32.DeleteObject(p);
        _brushes.Clear();
        _pens.Clear();
    }

    private void Release()
    {
        if (_memDc == 0) return;
        if (_oldBitmap != 0) Win32.SelectObject(_memDc, _oldBitmap);
        if (_dib != 0) Win32.DeleteObject(_dib);
        Win32.DeleteDC(_memDc);
        _memDc = 0; _dib = 0; _oldBitmap = 0; _w = 0; _h = 0;
    }

    public void Dispose()
    {
        Release();
        foreach (var b in _brushes.Values) Win32.DeleteObject(b);
        foreach (var p in _pens.Values) Win32.DeleteObject(p);
        _brushes.Clear();
        _pens.Clear();
    }
}

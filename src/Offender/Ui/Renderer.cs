using Offender.Native;
using Offender.Sampling;

namespace Offender.Ui;

/// <summary>
/// All painting. Owns a persistent memory DC + DIB section and every GDI object it uses,
/// so a repaint is: draw into the back buffer, then either one BitBlt or one
/// UpdateLayeredWindow. Nothing is allocated per frame.
///
/// Two compositing modes:
///
/// - Opaque themes (Glass, AMOLED, Light) draw a solid surface and let the window's
///   constant alpha handle translucency. Cheap, and the path that has been running.
/// - Clear draws on black with no surface at all, then recovers a per-pixel alpha from
///   the ink's own brightness. GDI never writes an alpha channel, so this is the standard
///   workaround -- the same one the tray icon uses. It is only accurate for bright ink,
///   which is exactly why the Clear palette is built out of whites and pastels.
/// </summary>
internal sealed unsafe class Renderer : IDisposable
{
    private nint _memDc;
    private nint _dib;
    private nint _oldBitmap;
    private uint* _pixels;
    private int _bufW, _bufH;

    private float _scale = 1f;
    private nint _fontLabel;    // Segoe UI, row labels and process names
    private nint _fontValue;    // Consolas, every number (stable column widths)
    private nint _fontHeader;   // Segoe UI semibold, section headers
    private bool _fontsAreForAlpha;

    private readonly Dictionary<uint, nint> _brushes = new();
    private readonly Dictionary<uint, nint> _pens = new();

    private readonly char[] _scratch = new char[160];
    private readonly Win32.POINT[] _points = new Win32.POINT[Metrics.HistorySamples];

    private readonly RingBuffer _cpuHist = new(Metrics.HistorySamples);
    private readonly RingBuffer _ramHist = new(Metrics.HistorySamples);
    private readonly RingBuffer _rxHist = new(Metrics.HistorySamples);
    private readonly RingBuffer _txHist = new(Metrics.HistorySamples);
    private readonly RingBuffer _dskHist = new(Metrics.HistorySamples);
    private readonly RingBuffer _gpuHist = new(Metrics.HistorySamples);

    private Theme _theme = Theme.Glass;

    public Theme Theme
    {
        get => _theme;
        set
        {
            if (ReferenceEquals(_theme, value)) return;
            _theme = value;

            // The brush and pen pools are keyed by colour, and every theme brings a new
            // palette, so the old theme's objects would otherwise sit in the cache for
            // the life of the process -- GDI objects nothing will ever ask for again.
            PurgeObjectCache();

            // Font AA quality depends on the compositing mode, so the fonts are rebuilt
            // whenever that changes.
            if (_fontsAreForAlpha != PerPixelAlpha) RebuildFonts();
        }
    }

    /// <summary>True when the active theme composites with a recovered per-pixel alpha.</summary>
    public bool PerPixelAlpha => _theme.Id == ThemeId.Clear;

    public bool ShowCpu { get; set; } = true;
    public bool ShowRam { get; set; } = true;
    public bool ShowNet { get; set; } = true;
    public bool ShowDisk { get; set; } = true;
    public bool ShowGpu { get; set; } = true;
    public bool ShowProcesses { get; set; } = true;

    /// <summary>The DC holding the finished frame, for BitBlt or UpdateLayeredWindow.</summary>
    public nint SurfaceDc => _memDc;

    // ---------------------------------------------------------------- sizing

    public int S(int logical) => (int)MathF.Round(logical * _scale);

    public void SetScale(float scale)
    {
        if (MathF.Abs(scale - _scale) < 0.001f && _fontLabel != 0) return;
        _scale = scale;
        RebuildFonts();
    }

    // Which elements the collapsed notch shows. Independent of the panel's own row
    // toggles: the notch is a glance, the panel is the detail.
    public bool NotchCpu { get; set; } = true;
    public bool NotchRam { get; set; } = true;
    public bool NotchGpu { get; set; }
    public bool NotchDisk { get; set; }
    public bool NotchNetDown { get; set; } = true;
    public bool NotchNetUp { get; set; } = true;

    /// <summary>Corner radius in logical pixels, shared by the notch and the panel.</summary>
    public int CornerRadius { get; set; } = Metrics.CornerRadiusDefault;

    public int PanelWidth => S(Metrics.PanelWidth);
    public int NotchHeight => S(Metrics.NotchHeight);

    /// <summary>
    /// Width of the enabled notch slots. Changes only when elements are toggled, which is
    /// the whole point of fixed slots.
    /// </summary>
    public int NotchWidth
    {
        get
        {
            int slots = 0;
            if (NotchCpu) slots += Metrics.NotchMetricSlot + Metrics.NotchGap;
            if (NotchRam) slots += Metrics.NotchMetricSlot + Metrics.NotchGap;
            if (NotchGpu) slots += Metrics.NotchMetricSlot + Metrics.NotchGap;
            if (NotchDisk) slots += Metrics.NotchMetricSlot + Metrics.NotchGap;
            if (NotchNetDown) slots += Metrics.NotchNetSlot + Metrics.NotchGap;
            if (NotchNetUp) slots += Metrics.NotchNetSlot + Metrics.NotchGap;

            // Everything off still needs a grab handle to right-click on.
            if (slots == 0) return S(Metrics.NotchMetricSlot + Metrics.NotchPadX * 2);

            slots -= Metrics.NotchGap;   // no trailing gap
            return S(slots + Metrics.NotchPadX * 2);
        }
    }

    /// <summary>
    /// Severity colour for a 0-100 load figure. This is what "colour coded" means in the
    /// notch: the label identifies the metric, the value says whether to care.
    /// </summary>
    private uint Severity(double percent) =>
        percent >= 90 ? _theme.Hot : percent >= 70 ? _theme.Warn : _theme.TextPrimary;

    /// <summary>Total window height for the currently enabled rows.</summary>
    public int MeasureHeight(Snapshot? s)
    {
        int rows = 0;
        if (ShowCpu) rows++;
        if (ShowRam) rows++;
        if (ShowNet) rows += 2;                          // down and up get a row each
        if (ShowDisk && (s?.DiskOk ?? true)) rows++;
        if (ShowGpu && (s?.GpuOk ?? true)) rows++;
        if (rows == 0) rows = 1;

        int h = S(Metrics.PadY) * 2 + rows * S(Metrics.MetricRowHeight);

        if (ShowProcesses)
        {
            int procRows = Math.Max(1, s?.ProcCount ?? Snapshot.MaxProcRows);
            h += S(Metrics.SectionGap) + S(Metrics.HeaderHeight) + procRows * S(Metrics.ProcRowHeight);
        }
        return h;
    }

    // ---------------------------------------------------------------- history

    /// <summary>Appends one sample per metric. Called once per published snapshot.</summary>
    public void PushHistory(Snapshot s)
    {
        _cpuHist.Add((float)s.CpuPercent);
        _ramHist.Add(s.MemTotal > 0 ? (float)(s.MemUsed * 100.0 / s.MemTotal) : 0);
        _rxHist.Add((float)s.NetRx);
        _txHist.Add((float)s.NetTx);
        _dskHist.Add((float)s.DiskActivePercent);
        _gpuHist.Add((float)s.GpuPercent);
    }

    // ---------------------------------------------------------------- painting

    /// <summary>
    /// Draws a complete frame into the back buffer. surfaceAlpha is the user's opacity
    /// setting and only matters in per-pixel mode; the opaque path applies it to the
    /// window instead.
    /// </summary>
    public void Render(nint refDc, int width, int height, Snapshot? s, byte surfaceAlpha)
    {
        EnsureBackBuffer(refDc, width, height);
        if (_memDc == 0) return;

        bool alphaMode = PerPixelAlpha;
        int radius = S(CornerRadius);

        if (alphaMode)
        {
            // Ink is recovered from brightness, so the ground must be pure black.
            ClearPixels(0x00000000);
        }
        else
        {
            var full = new Win32.RECT { Left = 0, Top = 0, Right = width, Bottom = height };
            Win32.FillRect(_memDc, &full, Brush(_theme.Background));
        }

        Win32.SetBkMode(_memDc, Win32.TRANSPARENT);

        DrawContent(width, height, s, alphaMode);

        if (alphaMode)
            CompositeAlpha(width, height, radius, surfaceAlpha);
    }

    /// <summary>Convenience for the opaque path: render, then blit to the window DC.</summary>
    public void PaintTo(nint destDc, int width, int height, Snapshot? s, byte surfaceAlpha)
    {
        Render(destDc, width, height, s, surfaceAlpha);
        if (_memDc != 0)
            Win32.BitBlt(destDc, 0, 0, width, height, _memDc, 0, 0, Win32.SRCCOPY);
    }

    /// <summary>
    /// Draws the collapsed notch: CPU and RAM with severity-coloured values, plus network
    /// throughput. Same two compositing modes as the full panel.
    /// </summary>
    public void RenderNotch(nint refDc, int width, int height, Snapshot? s, byte surfaceAlpha)
    {
        EnsureBackBuffer(refDc, width, height);
        if (_memDc == 0) return;

        bool alphaMode = PerPixelAlpha;

        if (alphaMode)
        {
            ClearPixels(0x00000000);
        }
        else
        {
            var full = new Win32.RECT { Left = 0, Top = 0, Right = width, Bottom = height };
            Win32.FillRect(_memDc, &full, Brush(_theme.Background));
        }

        Win32.SetBkMode(_memDc, Win32.TRANSPARENT);

        if (_theme.Border) DrawNotchBorder(width, height);

        int y = (height - S(14)) / 2;

        if (s is null)
        {
            DrawText(S(Metrics.NotchPadX), y, "starting…", _theme.TextDim, _fontLabel);
        }
        else
        {
            int x = S(Metrics.NotchPadX);

            if (NotchCpu)
                x = DrawNotchMetric(x, y, "CPU", _theme.Cpu, s.CpuPercent);

            if (NotchRam)
            {
                double ram = s.MemTotal > 0 ? s.MemUsed * 100.0 / s.MemTotal : 0;
                x = DrawNotchMetric(x, y, "RAM", _theme.Ram, ram);
            }

            if (NotchGpu)
                x = DrawNotchMetric(x, y, "GPU", _theme.Gpu, s.GpuOk ? s.GpuPercent : -1);

            if (NotchDisk)
                x = DrawNotchMetric(x, y, "DSK", _theme.Disk, s.DiskOk ? s.DiskActivePercent : -1);

            if (NotchNetDown)
                x = DrawNotchRate(x, y, '↓', s.NetRx, _theme.NetDown);

            if (NotchNetUp)
                x = DrawNotchRate(x, y, '↑', s.NetTx, _theme.NetUp);
        }

        if (alphaMode)
            CompositeAlpha(width, height, S(CornerRadius), surfaceAlpha);
    }

    public void PaintNotchTo(nint destDc, int width, int height, Snapshot? s, byte surfaceAlpha)
    {
        RenderNotch(destDc, width, height, s, surfaceAlpha);
        if (_memDc != 0)
            Win32.BitBlt(destDc, 0, 0, width, height, _memDc, 0, 0, Win32.SRCCOPY);
    }

    /// <summary>
    /// One labelled percentage in the notch. A negative value means the counter is not
    /// available on this machine, which is shown as a dash rather than a confident zero.
    /// Returns the x position of the next slot.
    /// </summary>
    private int DrawNotchMetric(int x, int y, ReadOnlySpan<char> label, uint accent, double percent)
    {
        DrawText(x, y, label, accent, _fontHeader);

        var t = new TextBuf(_scratch);
        uint color;
        if (percent < 0)
        {
            t.Append("--");
            color = _theme.TextFaint;
        }
        else
        {
            t.AppendPercent(percent, "F0");
            color = Severity(percent);
        }

        DrawText(x + S(Metrics.NotchValueOffset), y, t.Span, color, _fontValue);
        return x + S(Metrics.NotchMetricSlot + Metrics.NotchGap);
    }

    /// <summary>One throughput figure in the notch. Returns the x position of the next slot.</summary>
    private int DrawNotchRate(int x, int y, char arrow, double bytesPerSecond, uint color)
    {
        var t = new TextBuf(_scratch);
        t.Append(arrow);
        AppendCompactRate(ref t, bytesPerSecond);
        DrawText(x, y, t.Span, color, _fontValue);
        return x + S(Metrics.NotchNetSlot + Metrics.NotchGap);
    }

    /// <summary>Four characters at most; the notch has fixed columns to fit into.</summary>
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

    private void DrawNotchBorder(int width, int height)
    {
        int r = S(CornerRadius);
        nint oldPen = Win32.SelectObject(_memDc, Pen(_theme.BorderColor));
        nint oldBrush = Win32.SelectObject(_memDc, Win32.GetStockObject(Win32.NULL_BRUSH));
        Win32.RoundRect(_memDc, 0, 0, width, height, r * 2, r * 2);
        Win32.SelectObject(_memDc, oldBrush);
        Win32.SelectObject(_memDc, oldPen);
    }

    private void DrawContent(int width, int height, Snapshot? s, bool alphaMode)
    {
        int x = S(Metrics.PadX);
        int y = S(Metrics.PadY);
        int rowH = S(Metrics.MetricRowHeight);
        int sparkW = S(Metrics.SparkWidth);
        int sparkX = width - S(Metrics.PadX) - sparkW;

        if (_theme.Border) DrawBorder(width, height);

        if (s is null)
        {
            DrawText(x, y, "starting...", _theme.TextDim, _fontLabel);
            return;
        }

        var t = new TextBuf(_scratch);

        if (ShowCpu)
        {
            t.Clear();
            t.AppendPercent(s.CpuPercent, "F0");
            DrawMetricRow(x, y, sparkX, sparkW, "CPU", t.Span, _theme.Cpu, _cpuHist, 100, false, alphaMode);
            y += rowH;
        }

        if (ShowRam)
        {
            t.Clear();
            t.AppendBytes(s.MemUsed);
            t.Append(" / ");
            t.AppendBytes(s.MemTotal);
            DrawMetricRow(x, y, sparkX, sparkW, "RAM", t.Span, _theme.Ram, _ramHist, 100, false, alphaMode);
            y += rowH;
        }

        if (ShowNet)
        {
            // Down and up get their own row so both stay readable, and they share a
            // sparkline scale so the two are visually comparable.
            float netScale = MathF.Max(_rxHist.Max(), _txHist.Max());

            t.Clear();
            t.AppendRate(s.NetRx);
            DrawMetricRow(x, y, sparkX, sparkW, "NET↓", t.Span, _theme.NetDown, _rxHist, netScale, true, alphaMode);
            y += rowH;

            t.Clear();
            t.AppendRate(s.NetTx);
            DrawMetricRow(x, y, sparkX, sparkW, "NET↑", t.Span, _theme.NetUp, _txHist, netScale, true, alphaMode);
            y += rowH;
        }

        if (ShowDisk && s.DiskOk)
        {
            t.Clear();
            t.AppendPercent(s.DiskActivePercent, "F0");
            t.Append("  ");
            t.AppendRate(s.DiskRead + s.DiskWrite);
            DrawMetricRow(x, y, sparkX, sparkW, "DSK", t.Span, _theme.Disk, _dskHist, 100, false, alphaMode);
            y += rowH;
        }

        if (ShowGpu && s.GpuOk)
        {
            t.Clear();
            t.AppendPercent(s.GpuPercent, "F0");
            if (s.VramOk)
            {
                t.Append("  ");
                t.AppendBytes(s.VramUsed);
            }
            DrawMetricRow(x, y, sparkX, sparkW, "GPU", t.Span, _theme.Gpu, _gpuHist, 100, false, alphaMode);
            y += rowH;
        }

        if (!ShowProcesses) return;

        y += S(Metrics.SectionGap) / 2;
        DrawHLine(x, y, width - S(Metrics.PadX), _theme.Divider);
        y += S(Metrics.SectionGap) / 2;

        DrawText(x, y, "SLOWING YOU DOWN", _theme.TextFaint, _fontHeader);
        y += S(Metrics.HeaderHeight);

        if (s.ProcCount == 0)
        {
            DrawText(x, y, "measuring...", _theme.TextFaint, _fontLabel);
            return;
        }

        for (int i = 0; i < s.ProcCount; i++)
        {
            DrawProcRow(x, y, width, in s.Procs[i]);
            y += S(Metrics.ProcRowHeight);
        }
    }

    private void DrawMetricRow(
        int x, int y, int sparkX, int sparkW,
        ReadOnlySpan<char> label, ReadOnlySpan<char> value,
        uint accent, RingBuffer history, float scaleMax, bool rateScale, bool alphaMode)
    {
        int textY = y + S(4);
        DrawText(x, textY, label, accent, _fontLabel);
        DrawText(x + S(Metrics.LabelWidth), textY, value, _theme.TextPrimary, _fontValue);

        int sparkY = y + S(5);
        DrawSparkline(sparkX, sparkY, sparkW, S(Metrics.SparkHeight), history, accent, scaleMax, rateScale, alphaMode);
    }

    private void DrawProcRow(int x, int y, int width, in ProcRow p)
    {
        int right = width - S(Metrics.PadX);

        // Memory, right edge.
        var t = new TextBuf(_scratch);
        t.AppendBytes(p.PrivateBytes);
        int memW = MeasureText(t.Span, _fontValue);
        DrawText(right - memW, y, t.Span, _theme.TextDim, _fontValue);

        // CPU percent, right-aligned in its own column.
        int cpuRight = right - memW - S(10);
        t.Clear();
        t.AppendPercent(p.CpuPercent, p.CpuPercent >= 10 ? "F0" : "F1");
        int cpuW = MeasureText(t.Span, _fontValue);
        DrawText(cpuRight - cpuW, y, t.Span, _theme.SeverityColor(p.Score), _fontValue);

        // Name fills whatever is left, ellipsised.
        int nameMax = cpuRight - cpuW - S(8) - x;
        DrawTextEllipsised(x, y, nameMax, p.Name, _theme.TextPrimary, _fontLabel);
    }

    // ---------------------------------------------------------------- primitives

    private void DrawBorder(int width, int height)
    {
        int r = S(CornerRadius);
        nint pen = Pen(_theme.BorderColor);
        nint oldPen = Win32.SelectObject(_memDc, pen);
        nint oldBrush = Win32.SelectObject(_memDc, Win32.GetStockObject(Win32.NULL_BRUSH));

        Win32.RoundRect(_memDc, 0, 0, width, height, r * 2, r * 2);

        Win32.SelectObject(_memDc, oldBrush);
        Win32.SelectObject(_memDc, oldPen);
    }

    private void DrawSparkline(
        int x, int y, int w, int h, RingBuffer hist, uint color,
        float scaleMax, bool rateScale, bool alphaMode)
    {
        // The track is surface decoration, not data. Clear has no surface, so it has no
        // track either -- drawing one would make the panel look like it has a background.
        if (!alphaMode)
        {
            var track = new Win32.RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };
            Win32.FillRect(_memDc, &track, Brush(_theme.Track));
        }

        int n = hist.Count;
        if (n < 2) return;

        float max = scaleMax;
        if (rateScale)
        {
            // Rate sparklines auto-scale, with a floor so an idle link is a flat line at
            // the bottom rather than noise amplified to full height.
            max = MathF.Max(max, 64 * 1024);
        }
        if (max <= 0) max = 1;

        int take = Math.Min(n, w);
        int first = n - take;

        for (int i = 0; i < take; i++)
        {
            float v = hist[first + i] / max;
            if (v < 0) v = 0; else if (v > 1) v = 1;
            _points[i].X = x + (int)(i * (w - 1) / (float)Math.Max(1, take - 1));
            _points[i].Y = y + h - 1 - (int)(v * (h - 2));
        }

        nint pen = Pen(color);
        nint oldPen = Win32.SelectObject(_memDc, pen);
        fixed (Win32.POINT* p = _points) Win32.Polyline(_memDc, p, take);
        Win32.SelectObject(_memDc, oldPen);
    }

    private void DrawHLine(int x1, int y, int x2, uint color)
    {
        var r = new Win32.RECT { Left = x1, Top = y, Right = x2, Bottom = y + 1 };
        Win32.FillRect(_memDc, &r, Brush(color));
    }

    private void DrawText(int x, int y, ReadOnlySpan<char> text, uint color, nint font)
    {
        if (text.Length == 0) return;
        nint oldFont = Win32.SelectObject(_memDc, font);
        Win32.SetTextColor(_memDc, color);
        fixed (char* p = text) Win32.ExtTextOutW(_memDc, x, y, 0, null, p, (uint)text.Length, null);
        Win32.SelectObject(_memDc, oldFont);
    }

    /// <summary>
    /// Draws text clipped to maxWidth, trimming and appending an ellipsis when it does not
    /// fit. Process names are the only variable-width text on the panel.
    /// </summary>
    private void DrawTextEllipsised(int x, int y, int maxWidth, ReadOnlySpan<char> text, uint color, nint font)
    {
        if (maxWidth <= 0 || text.Length == 0) return;

        if (MeasureText(text, font) <= maxWidth)
        {
            DrawText(x, y, text, color, font);
            return;
        }

        var t = new TextBuf(_scratch);
        int len = text.Length;
        while (len > 1)
        {
            len--;
            t.Clear();
            t.Append(text[..len]);
            t.Append('…');
            if (MeasureText(t.Span, font) <= maxWidth) break;
        }
        DrawText(x, y, t.Span, color, font);
    }

    public int MeasureText(ReadOnlySpan<char> text, nint font)
    {
        if (_memDc == 0 || text.Length == 0) return 0;
        nint oldFont = Win32.SelectObject(_memDc, font);
        Win32.SIZE size;
        fixed (char* p = text) Win32.GetTextExtentPoint32W(_memDc, p, text.Length, &size);
        Win32.SelectObject(_memDc, oldFont);
        return size.cx;
    }

    public nint FontLabel => _fontLabel;
    public nint FontValue => _fontValue;
    public nint FontHeader => _fontHeader;

    // ---------------------------------------------------------------- alpha compositing

    private void ClearPixels(uint value)
    {
        int n = _bufW * _bufH;
        for (int i = 0; i < n; i++) _pixels[i] = value;
    }

    /// <summary>
    /// Turns the black-ground render into premultiplied ARGB.
    ///
    /// Ink alpha is the pixel's own brightness, which is exact for white and close enough
    /// for the bright palette Clear uses. The theme surface is then composited underneath
    /// procedurally -- no second buffer needed, since it is a flat colour inside a
    /// rounded rectangle.
    /// </summary>
    private void CompositeAlpha(int width, int height, int radius, byte surfaceAlpha)
    {
        uint bg = _theme.Background;
        uint bgR = bg & 0xFF, bgG = (bg >> 8) & 0xFF, bgB = (bg >> 16) & 0xFF;

        for (int yy = 0; yy < height; yy++)
        {
            // Horizontal extent of the rounded rectangle on this scanline.
            int inset = RoundedInset(yy, height, radius);
            uint* row = _pixels + (long)yy * width;

            for (int xx = 0; xx < width; xx++)
            {
                uint px = row[xx];
                uint r = (px >> 16) & 0xFF, g = (px >> 8) & 0xFF, b = px & 0xFF;

                uint ink = r > g ? r : g;
                if (b > ink) ink = b;

                bool inside = surfaceAlpha > 0 && xx >= inset && xx < width - inset;
                uint sa = inside ? surfaceAlpha : 0u;

                if (ink == 0 && sa == 0) { row[xx] = 0; continue; }

                // Ink is already premultiplied: it was drawn on black, so its channels
                // are colour x coverage.
                uint inv = 255 - ink;
                uint outA = ink + sa * inv / 255;
                uint outR = r + bgR * sa / 255 * inv / 255;
                uint outG = g + bgG * sa / 255 * inv / 255;
                uint outB = b + bgB * sa / 255 * inv / 255;

                if (outR > outA) outR = outA;
                if (outG > outA) outG = outA;
                if (outB > outA) outB = outA;

                row[xx] = (outA << 24) | (outR << 16) | (outG << 8) | outB;
            }
        }
    }

    /// <summary>How far in from each edge the rounded rectangle starts on a given scanline.</summary>
    private static int RoundedInset(int y, int height, int radius)
    {
        if (radius <= 0) return 0;

        int dy;
        if (y < radius) dy = radius - y;
        else if (y >= height - radius) dy = y - (height - radius - 1);
        else return 0;

        int sq = radius * radius - dy * dy;
        if (sq <= 0) return radius;
        return radius - (int)MathF.Sqrt(sq);
    }

    // ---------------------------------------------------------------- resources

    private void EnsureBackBuffer(nint refDc, int w, int h)
    {
        if (_memDc != 0 && w == _bufW && h == _bufH) return;

        ReleaseBackBuffer();
        if (w <= 0 || h <= 0) return;

        _memDc = Win32.CreateCompatibleDC(refDc);
        if (_memDc == 0) return;

        var bmi = new Win32.BITMAPINFOHEADER
        {
            biSize = (uint)sizeof(Win32.BITMAPINFOHEADER),
            biWidth = w,
            biHeight = -h,          // top-down
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0,      // BI_RGB
        };

        void* bits;
        _dib = Win32.CreateDIBSection(_memDc, &bmi, Win32.DIB_RGB_COLORS, &bits, 0, 0);
        if (_dib == 0) { Win32.DeleteDC(_memDc); _memDc = 0; return; }

        _pixels = (uint*)bits;
        _oldBitmap = Win32.SelectObject(_memDc, _dib);
        _bufW = w;
        _bufH = h;
    }

    private void RebuildFonts()
    {
        DeleteFonts();
        _fontsAreForAlpha = PerPixelAlpha;

        // ClearType's colour fringing survives the alpha recovery as rainbow edges, so
        // the layered path uses greyscale antialiasing instead.
        int quality = _fontsAreForAlpha ? Win32.ANTIALIASED_QUALITY : Win32.CLEARTYPE_QUALITY;

        _fontLabel = MakeFont("Segoe UI", S(12), Win32.FW_NORMAL, quality);
        _fontValue = MakeFont("Consolas", S(12), Win32.FW_NORMAL, quality);
        _fontHeader = MakeFont("Segoe UI", S(10), Win32.FW_SEMIBOLD, quality);
    }

    private static nint MakeFont(string face, int pixelHeight, int weight, int quality)
    {
        fixed (char* f = face)
        {
            return Win32.CreateFontW(
                -pixelHeight, 0, 0, 0, weight,
                0, 0, 0, Win32.DEFAULT_CHARSET,
                Win32.OUT_TT_PRECIS, Win32.CLIP_DEFAULT_PRECIS, (uint)quality,
                Win32.DEFAULT_PITCH, f);
        }
    }

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

    /// <summary>Frees every cached brush and pen. They are recreated lazily on next use.</summary>
    private void PurgeObjectCache()
    {
        foreach (var b in _brushes.Values) Win32.DeleteObject(b);
        foreach (var p in _pens.Values) Win32.DeleteObject(p);
        _brushes.Clear();
        _pens.Clear();
    }

    private void DeleteFonts()
    {
        if (_fontLabel != 0) { Win32.DeleteObject(_fontLabel); _fontLabel = 0; }
        if (_fontValue != 0) { Win32.DeleteObject(_fontValue); _fontValue = 0; }
        if (_fontHeader != 0) { Win32.DeleteObject(_fontHeader); _fontHeader = 0; }
    }

    private void ReleaseBackBuffer()
    {
        if (_memDc == 0) return;
        if (_oldBitmap != 0) Win32.SelectObject(_memDc, _oldBitmap);
        if (_dib != 0) Win32.DeleteObject(_dib);
        Win32.DeleteDC(_memDc);
        _memDc = 0; _dib = 0; _oldBitmap = 0; _pixels = null; _bufW = 0; _bufH = 0;
    }

    public void Dispose()
    {
        ReleaseBackBuffer();
        DeleteFonts();
        foreach (var b in _brushes.Values) Win32.DeleteObject(b);
        foreach (var p in _pens.Values) Win32.DeleteObject(p);
        _brushes.Clear();
        _pens.Clear();
    }
}

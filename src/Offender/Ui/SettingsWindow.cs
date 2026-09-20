using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Offender.Native;

namespace Offender.Ui;

/// <summary>
/// The settings panel, drawn with the same GDI primitives as the monitor itself so it
/// wears whatever theme is active -- an AMOLED panel does not open a light-grey system
/// dialog.
///
/// Controls are a flat list of records laid out top to bottom. Layout, painting and hit
/// testing all walk the same list, so a control can never be drawn somewhere it cannot be
/// clicked. Every change applies immediately; there is no OK button to forget to press.
/// </summary>
internal sealed unsafe class SettingsWindow : IDisposable
{
    private enum Kind { Header, Toggle, Slider, Segmented, Button, Spacer }

    private const int IdThemeRow = 1;
    private const int IdShowCpu = 10;
    private const int IdShowRam = 11;
    private const int IdShowNet = 12;
    private const int IdShowDisk = 13;
    private const int IdShowGpu = 14;
    private const int IdShowProcs = 15;
    private const int IdOpacity = 20;
    private const int IdInterval = 21;
    private const int IdRadius = 22;
    private const int IdNotchCpu = 70;
    private const int IdNotchRam = 71;
    private const int IdNotchGpu = 72;
    private const int IdNotchDisk = 73;
    private const int IdNotchNetDown = 74;
    private const int IdNotchNetUp = 75;
    private const int IdStartup = 30;
    private const int IdEmbed = 31;
    private const int IdExpandOnHover = 32;
    private const int IdAlwaysOnTop = 33;
    private const int IdStartCollapsed = 34;
    private const int IdTrayMode = 40;
    private const int IdPinShortcut = 50;
    private const int IdClose = 60;

    private struct Item
    {
        public Kind Kind;
        public int Id;
        public string Label;
        public int X;        // left edge of the column this item sits in
        public int Y;
        public int Width;    // column content width
        public int Height;
        public int SegmentCount;
    }

    /// <summary>
    /// A header plus the controls under it. Sections are packed into columns whole: a
    /// header stranded at the foot of one column with its controls at the head of the
    /// next reads as two broken groups, which is worse than an uneven column.
    /// </summary>
    private sealed class Section
    {
        public readonly List<Item> Items = new();
        public int Height;
    }

    private static SettingsWindow? _instance;

    private readonly PanelWindow _panel;
    private readonly Config _config;
    private readonly GdiCanvas _canvas = new();
    private readonly List<Item> _items = new(24);
    private readonly char[] _scratch = new char[96];

    private nint _hwnd;
    private int _width, _height;
    private float _scale = 1f;

    private int _hoverId = -1;
    private int _dragId = -1;

    public SettingsWindow(PanelWindow panel)
    {
        _panel = panel;
        _config = panel.Config;
        _instance = this;
    }

    public bool Alive => _hwnd != 0 && Win32.IsWindow(_hwnd) != 0;

    private Theme Theme => _panel.Theme;

    private int S(int logical) => (int)MathF.Round(logical * _scale);

    // ---------------------------------------------------------------- lifecycle

    public bool Create()
    {
        nint hInstance = Win32.GetModuleHandleW(null);

        fixed (char* className = "OffenderSettings")
        fixed (char* windowName = "Offender Settings")
        {
            var wc = new Win32.WNDCLASSEXW
            {
                cbSize = (uint)sizeof(Win32.WNDCLASSEXW),
                style = Win32.CS_HREDRAW | Win32.CS_VREDRAW,
                lpfnWndProc = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint>)&StaticWndProc,
                hInstance = hInstance,
                hCursor = Win32.LoadCursorW(0, Win32.IDC_ARROW),
                hbrBackground = 0,
                lpszClassName = className,
            };

            // Re-registering the same class is harmless; ignore the failure that causes.
            Win32.RegisterClassExW(&wc);

            _hwnd = Win32.CreateWindowExW(
                Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_TOPMOST,
                className, windowName,
                Win32.WS_POPUP,
                0, 0, 100, 100,
                0, 0, hInstance, 0);
        }

        if (_hwnd == 0) return false;

        _scale = Win32.GetDpiForWindow(_hwnd) / 96f;
        Layout();

        // Open next to the panel, nudged onto the work area if it would hang off.
        Win32.RECT pr;
        Win32.GetWindowRect(_panel.Handle, &pr);
        var work = WorkArea(_hwnd);

        int x = pr.Left - _width - S(8);
        if (x < work.Left) x = Math.Min(pr.Right + S(8), work.Right - _width);
        int y = Math.Clamp(pr.Top, work.Top, Math.Max(work.Top, work.Bottom - _height));

        int round = Win32.DWMWCP_ROUND;
        Win32.DwmSetWindowAttribute(_hwnd, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, &round, sizeof(int));

        Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, x, y, _width, _height, Win32.SWP_SHOWWINDOW);
        Win32.SetForegroundWindow(_hwnd);
        return true;
    }

    public void Focus()
    {
        Win32.ShowWindow(_hwnd, Win32.SW_SHOWNOACTIVATE);
        Win32.SetForegroundWindow(_hwnd);
    }

    /// <summary>The panel calls this when the theme changes so the dialog restyles itself.</summary>
    public void OnThemeChanged()
    {
        if (!Alive) return;

        // Same reasoning as the renderer: the canvas pools brushes and pens by colour, and
        // the outgoing theme's are now dead weight.
        _canvas.PurgeObjects();
        Win32.InvalidateRect(_hwnd, null, 1);
    }

    // ---------------------------------------------------------------- layout

    private void Layout()
    {
        var sections = BuildSections();

        int colW = S(268);
        int gutter = S(18);
        int pad = S(12);

        // Pick the fewest columns that fit the monitor. A single column reads best, so
        // widen only when the content genuinely does not fit vertically.
        int available = UsableHeight();
        int columns = 1;
        while (columns < MaxColumns && PackedHeight(sections, columns, pad) > available) columns++;

        _items.Clear();
        _height = Pack(sections, columns, colW, gutter, pad);
        _width = pad * 2 + columns * colW + (columns - 1) * gutter;
    }

    private const int MaxColumns = 3;

    private int SectionGap => S(12);

    /// <summary>Height the monitor can actually show, leaving a margin for the taskbar.</summary>
    private int UsableHeight()
    {
        var work = WorkArea(_hwnd);
        return Math.Max(S(200), work.Bottom - work.Top - S(24));
    }

    /// <summary>
    /// Greedy column packing: fill a column until it has had its share of the total
    /// height, then move on. Returns the window height.
    /// </summary>
    private int Pack(List<Section> sections, int columns, int colW, int gutter, int pad)
    {
        int target = TotalHeight(sections) / columns;

        int col = 0;
        int y = pad;
        int tallest = 0;

        foreach (var sec in sections)
        {
            if (col < columns - 1 && y > pad && y - pad + sec.Height > target)
            {
                tallest = Math.Max(tallest, y);
                col++;
                y = pad;
            }
            else if (y > pad)
            {
                // Breathing room between stacked sections. Not applied to the first
                // section in a column, which would just indent the whole column.
                y += SectionGap;
            }

            int x = pad + col * (colW + gutter);
            foreach (var item in sec.Items)
            {
                var placed = item;
                placed.X = x;
                placed.Y = y + item.Y;      // item.Y is section-relative until now
                placed.Width = colW;
                _items.Add(placed);
            }
            y += sec.Height;
        }

        return Math.Max(tallest, y) + pad;
    }

    /// <summary>Dry run of Pack, used to choose the column count.</summary>
    private int PackedHeight(List<Section> sections, int columns, int pad)
    {
        int target = TotalHeight(sections) / columns;
        int col = 0, y = pad, tallest = 0;

        foreach (var sec in sections)
        {
            if (col < columns - 1 && y > pad && y - pad + sec.Height > target)
            {
                tallest = Math.Max(tallest, y);
                col++;
                y = pad;
            }
            else if (y > pad)
            {
                // Breathing room between stacked sections. Not applied to the first
                // section in a column, which would just indent the whole column.
                y += SectionGap;
            }
            y += sec.Height;
        }
        return Math.Max(tallest, y) + pad;
    }

    private static int TotalHeight(List<Section> sections)
    {
        int total = 0;
        foreach (var sec in sections) total += sec.Height;
        return total;
    }

    private List<Section> BuildSections()
    {
        var list = new List<Section>();
        Section cur = new();

        void Begin(string header)
        {
            cur = new Section();
            list.Add(cur);
            AddTo(cur, Kind.Header, -1, header, S(26));
        }

        Begin("APPEARANCE");
        AddTo(cur, Kind.Segmented, IdThemeRow, "Theme", SegmentedHeight, Theme.All.Length);
        AddTo(cur, Kind.Slider, IdOpacity, "Surface opacity", SliderHeight);
        AddTo(cur, Kind.Slider, IdRadius, "Corner radius", SliderHeight);

        Begin("BEHAVIOUR");
        AddTo(cur, Kind.Toggle, IdStartCollapsed, "Idle as notch", ToggleHeight);
        AddTo(cur, Kind.Toggle, IdExpandOnHover, "Expand on hover", ToggleHeight);
        AddTo(cur, Kind.Toggle, IdAlwaysOnTop, "Always on top (expanded)", ToggleHeight);

        Begin("NOTCH SHOWS");
        AddTo(cur, Kind.Toggle, IdNotchCpu, "CPU", ToggleHeight);
        AddTo(cur, Kind.Toggle, IdNotchRam, "Memory", ToggleHeight);
        AddTo(cur, Kind.Toggle, IdNotchGpu, "GPU", ToggleHeight);
        AddTo(cur, Kind.Toggle, IdNotchDisk, "Disk", ToggleHeight);
        AddTo(cur, Kind.Toggle, IdNotchNetDown, "Download", ToggleHeight);
        AddTo(cur, Kind.Toggle, IdNotchNetUp, "Upload", ToggleHeight);

        Begin("EXPANDED ROWS");
        AddTo(cur, Kind.Toggle, IdShowCpu, "CPU", ToggleHeight);
        AddTo(cur, Kind.Toggle, IdShowRam, "Memory", ToggleHeight);
        AddTo(cur, Kind.Toggle, IdShowNet, "Network", ToggleHeight);
        AddTo(cur, Kind.Toggle, IdShowDisk, "Disk", ToggleHeight);
        AddTo(cur, Kind.Toggle, IdShowGpu, "GPU", ToggleHeight);
        AddTo(cur, Kind.Toggle, IdShowProcs, "Slowing-you-down list", ToggleHeight);

        Begin("SAMPLING");
        AddTo(cur, Kind.Slider, IdInterval, "Update interval", SliderHeight);

        Begin("TRAY & TASKBAR");
        AddTo(cur, Kind.Segmented, IdTrayMode, "Tray shows", SegmentedHeight, 3);
        AddTo(cur, Kind.Toggle, IdEmbed, "Show in taskbar", ToggleHeight);
        AddTo(cur, Kind.Button, IdPinShortcut, "Create pinnable shortcut", S(34));

        Begin("SYSTEM");
        AddTo(cur, Kind.Toggle, IdStartup, "Start with Windows", ToggleHeight);
        AddTo(cur, Kind.Button, IdClose, "Close", S(34));

        return list;
    }

    private void AddTo(Section sec, Kind kind, int id, string label, int height, int segments = 0)
    {
        sec.Items.Add(new Item
        {
            Kind = kind,
            Id = id,
            Label = label,
            Y = sec.Height,       // section-relative; Pack turns this absolute
            Height = height,
            SegmentCount = segments,
        });
        sec.Height += height;
    }

    // Controls that stack a label above the control need room for both; a single row
    // height that fits one line of text clips the label at any scale above 100%.
    private int ToggleHeight => S(26);
    private int SegmentedHeight => S(46);
    private int SliderHeight => S(44);
    private int LabelBaseline => S(3);
    private int SegmentTop => S(24);
    private int SegmentHeight => S(18);
    private int SliderTrackTop => S(30);

    // ---------------------------------------------------------------- wndproc

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static nint StaticWndProc(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        var self = _instance;
        if (self is null) return Win32.DefWindowProcW(hwnd, msg, wParam, lParam);

        try { return self.WndProc(hwnd, msg, wParam, lParam); }
        catch { return Win32.DefWindowProcW(hwnd, msg, wParam, lParam); }
    }

    private nint WndProc(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        switch (msg)
        {
            case Win32.WM_PAINT:
            {
                Win32.PAINTSTRUCT ps;
                nint dc = Win32.BeginPaint(hwnd, &ps);
                if (dc != 0) { Paint(dc); Win32.EndPaint(hwnd, &ps); }
                return 0;
            }

            case Win32.WM_ERASEBKGND:
                return 1;

            case Win32.WM_LBUTTONDOWN:
                OnMouseDown(Win32.LoWord(lParam), Win32.HiWord(lParam));
                return 0;

            case Win32.WM_MOUSEMOVE:
                OnMouseMove(Win32.LoWord(lParam), Win32.HiWord(lParam));
                return 0;

            case Win32.WM_LBUTTONUP:
                if (_dragId >= 0) { _dragId = -1; Win32.ReleaseCapture(); }
                return 0;

            case Win32.WM_KEYDOWN:
                if ((uint)wParam == 0x1B /* VK_ESCAPE */) Close();
                return 0;

            case Win32.WM_CLOSE:
                Close();
                return 0;

            case Win32.WM_DESTROY:
                _hwnd = 0;
                // Release the back buffer and object pool now rather than at Dispose.
                // The canvas holds a DC, a multi-megabyte DIB and every brush and pen the
                // dialog used; keeping all of that alive for a window that no longer
                // exists is pure waste, and it is rebuilt on demand if reopened.
                _canvas.Dispose();
                return 0;
        }

        return Win32.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void Close()
    {
        if (_hwnd != 0) Win32.DestroyWindow(_hwnd);
        _hwnd = 0;
    }

    // ---------------------------------------------------------------- input

    private void OnMouseDown(int mx, int my)
    {
        foreach (var item in _items)
        {
            if (!Hits(item, mx, my)) continue;

            switch (item.Kind)
            {
                case Kind.Toggle:
                    ToggleValue(item.Id);
                    Apply();
                    return;

                case Kind.Segmented:
                {
                    // Only the buttons themselves are clickable, not the label above them.
                    if (my < item.Y + SegmentTop) return;
                    int seg = SegmentAt(item, mx);
                    if (seg >= 0) { SetSegment(item.Id, seg); Apply(); }
                    return;
                }

                case Kind.Slider:
                    _dragId = item.Id;
                    Win32.SetCapture(_hwnd);
                    SetSliderFromX(item, mx);
                    Apply();
                    return;

                case Kind.Button:
                    if (item.Id == IdClose) Close();
                    else if (item.Id == IdPinShortcut) Startup.CreateStartMenuShortcut();
                    return;
            }
            return;
        }
    }

    private void OnMouseMove(int mx, int my)
    {
        if (_dragId >= 0)
        {
            foreach (var item in _items)
                if (item.Id == _dragId) { SetSliderFromX(item, mx); Apply(); return; }
            return;
        }

        int hover = -1;
        foreach (var item in _items)
        {
            if (!Hits(item, mx, my)) continue;
            if (item.Kind is Kind.Toggle or Kind.Button or Kind.Segmented or Kind.Slider) hover = item.Id;
            break;
        }

        if (hover != _hoverId)
        {
            _hoverId = hover;
            Win32.InvalidateRect(_hwnd, null, 0);
        }
    }

    /// <summary>
    /// Hit test against the item's own column. With one column an x check was redundant;
    /// with several it is what stops a click landing on the item at the same height in a
    /// neighbouring column.
    /// </summary>
    private static bool Hits(in Item item, int mx, int my) =>
        mx >= item.X && mx < item.X + item.Width &&
        my >= item.Y && my < item.Y + item.Height;

    private void ToggleValue(int id)
    {
        switch (id)
        {
            case IdShowCpu: _config.ShowCpu = !_config.ShowCpu; break;
            case IdShowRam: _config.ShowRam = !_config.ShowRam; break;
            case IdShowNet: _config.ShowNet = !_config.ShowNet; break;
            case IdShowDisk: _config.ShowDisk = !_config.ShowDisk; break;
            case IdShowGpu: _config.ShowGpu = !_config.ShowGpu; break;
            case IdShowProcs: _config.ShowProcesses = !_config.ShowProcesses; break;
            case IdEmbed: _config.EmbedInTaskbar = !_config.EmbedInTaskbar; break;
            case IdExpandOnHover: _config.ExpandOnHover = !_config.ExpandOnHover; break;
            case IdAlwaysOnTop: _config.AlwaysOnTop = !_config.AlwaysOnTop; break;
            case IdStartCollapsed: _panel.ToggleCollapsed(); break;
            case IdNotchCpu: _config.NotchCpu = !_config.NotchCpu; break;
            case IdNotchRam: _config.NotchRam = !_config.NotchRam; break;
            case IdNotchGpu: _config.NotchGpu = !_config.NotchGpu; break;
            case IdNotchDisk: _config.NotchDisk = !_config.NotchDisk; break;
            case IdNotchNetDown: _config.NotchNetDown = !_config.NotchNetDown; break;
            case IdNotchNetUp: _config.NotchNetUp = !_config.NotchNetUp; break;
            case IdStartup:
                _config.StartWithWindows = !_config.StartWithWindows;
                Startup.Set(_config.StartWithWindows);
                break;
        }
    }

    private bool ValueOf(int id) => id switch
    {
        IdShowCpu => _config.ShowCpu,
        IdShowRam => _config.ShowRam,
        IdShowNet => _config.ShowNet,
        IdShowDisk => _config.ShowDisk,
        IdShowGpu => _config.ShowGpu,
        IdShowProcs => _config.ShowProcesses,
        IdEmbed => _config.EmbedInTaskbar,
        IdStartup => _config.StartWithWindows,
        IdExpandOnHover => _config.ExpandOnHover,
        IdAlwaysOnTop => _config.AlwaysOnTop,
        IdStartCollapsed => _config.Collapsed,
        IdNotchCpu => _config.NotchCpu,
        IdNotchRam => _config.NotchRam,
        IdNotchGpu => _config.NotchGpu,
        IdNotchDisk => _config.NotchDisk,
        IdNotchNetDown => _config.NotchNetDown,
        IdNotchNetUp => _config.NotchNetUp,
        _ => false,
    };

    private void SetSegment(int id, int index)
    {
        if (id == IdThemeRow) { _panel.SetTheme(Theme.All[index].Id); return; }
        if (id == IdTrayMode) _config.Tray = (TrayMode)index;
    }

    private int SelectedSegment(int id) => id switch
    {
        IdThemeRow => Array.FindIndex(Theme.All, t => t.Id == _config.Theme),
        IdTrayMode => (int)_config.Tray,
        _ => -1,
    };

    private string SegmentLabel(int id, int index) => id switch
    {
        IdThemeRow => Theme.All[index].Name,
        IdTrayMode => index switch { 0 => "Network", 1 => "CPU", _ => "RAM" },
        _ => "",
    };

    private void SetSliderFromX(in Item item, int mx)
    {
        int x0 = item.X;
        int x1 = item.X + item.Width;
        float t = Math.Clamp((mx - x0) / (float)Math.Max(1, x1 - x0), 0f, 1f);

        if (item.Id == IdOpacity)
        {
            _config.Opacity = (int)MathF.Round(t * 255);
        }
        else if (item.Id == IdInterval)
        {
            // 250 ms .. 3000 ms in 250 ms steps.
            int steps = (int)MathF.Round(t * 11);
            _config.IntervalMs = 250 + steps * 250;
        }
        else if (item.Id == IdRadius)
        {
            _config.CornerRadius = Metrics.CornerRadiusMin +
                (int)MathF.Round(t * (Metrics.CornerRadiusMax - Metrics.CornerRadiusMin));
        }
    }

    private float SliderFraction(int id) => id switch
    {
        IdOpacity => Math.Clamp(_config.Opacity / 255f, 0f, 1f),
        IdInterval => Math.Clamp((_config.IntervalMs - 250) / 2750f, 0f, 1f),
        IdRadius => Math.Clamp(
            (_config.CornerRadius - Metrics.CornerRadiusMin) /
            (float)(Metrics.CornerRadiusMax - Metrics.CornerRadiusMin), 0f, 1f),
        _ => 0f,
    };

    private void Apply()
    {
        _panel.ApplySettings();
        Win32.InvalidateRect(_hwnd, null, 0);
    }

    // ---------------------------------------------------------------- painting

    private void Paint(nint destDc)
    {
        if (!_canvas.Ensure(destDc, _width, _height)) return;

        var th = Theme;

        // The dialog is always solid, even when the panel theme is transparent -- a
        // settings surface you can see the desktop through is not usable.
        uint bg = th.Id == ThemeId.Clear ? Win32.Rgb(0x1A, 0x1D, 0x22) : th.Background;
        _canvas.Fill(bg);
        _canvas.StrokeRoundRect(0, 0, _width - 1, _height - 1, S(Metrics.CornerRadiusDefault), th.BorderColor);

        foreach (var item in _items)
        {
            switch (item.Kind)
            {
                case Kind.Header:
                    _canvas.Text(item.X, item.Y + S(8), item.Label, th.TextFaint, _panel.Renderer.FontHeader);
                    break;

                case Kind.Toggle:
                    PaintToggle(item, th);
                    break;

                case Kind.Slider:
                    PaintSlider(item, th);
                    break;

                case Kind.Segmented:
                    PaintSegmented(item, th);
                    break;

                case Kind.Button:
                    PaintButton(item, th);
                    break;
            }
        }

        _canvas.BlitTo(destDc);
    }

    private void PaintToggle(in Item item, Theme th)
    {
        bool on = ValueOf(item.Id);
        int cy = item.Y + item.Height / 2;

        if (_hoverId == item.Id)
            _canvas.FillRoundRect(item.X - S(4), item.Y + S(1), item.Width + S(8), item.Height - S(2), S(5), th.Hover);

        _canvas.Text(item.X, cy - S(8), item.Label, th.TextPrimary, _panel.Renderer.FontLabel);

        // Pill switch, right-aligned in the column.
        int pillW = S(34), pillH = S(18);
        int px = item.X + item.Width - pillW;
        int py = cy - pillH / 2;

        _canvas.FillRoundRect(px, py, pillW, pillH, pillH / 2, on ? th.ControlOn : th.ControlTrack);

        int knobR = pillH / 2 - S(3);
        int knobX = on ? px + pillW - pillH / 2 : px + pillH / 2;
        _canvas.FillCircle(knobX, cy, knobR, th.ControlKnob);
    }

    private void PaintSlider(in Item item, Theme th)
    {
        _canvas.Text(item.X, item.Y + LabelBaseline, item.Label, th.TextPrimary, _panel.Renderer.FontLabel);

        // Current value, right-aligned on the label line.
        var t = new TextBuf(_scratch);
        if (item.Id == IdOpacity)
        {
            t.Append((int)MathF.Round(_config.Opacity * 100f / 255f));
            t.Append('%');
        }
        else if (item.Id == IdRadius)
        {
            t.Append(_config.CornerRadius);
            t.Append("px");
        }
        else
        {
            t.Append(_config.IntervalMs / 1000.0, "F2");
            t.Append('s');
        }
        int tw = _canvas.Measure(t.Span, _panel.Renderer.FontValue);
        _canvas.Text(item.X + item.Width - tw, item.Y + LabelBaseline, t.Span, th.TextDim, _panel.Renderer.FontValue);

        int x0 = item.X;
        int x1 = item.X + item.Width;
        int ty = item.Y + SliderTrackTop;
        int track = S(4);

        _canvas.FillRoundRect(x0, ty, x1 - x0, track, track / 2, th.ControlTrack);

        float f = SliderFraction(item.Id);
        int fill = (int)((x1 - x0) * f);
        if (fill > 0) _canvas.FillRoundRect(x0, ty, fill, track, track / 2, th.ControlOn);

        _canvas.FillCircle(x0 + fill, ty + track / 2, S(6), th.ControlKnob);
        _canvas.StrokeCircle(x0 + fill, ty + track / 2, S(6), th.ControlOn);
    }

    private void PaintSegmented(in Item item, Theme th)
    {
        _canvas.Text(item.X, item.Y + LabelBaseline, item.Label, th.TextPrimary, _panel.Renderer.FontLabel);

        int selected = SelectedSegment(item.Id);
        int y = item.Y + SegmentTop;
        int h = SegmentHeight;
        int gap = S(4);
        int segW = (item.Width - gap * (item.SegmentCount - 1)) / item.SegmentCount;

        for (int i = 0; i < item.SegmentCount; i++)
        {
            int x = item.X + i * (segW + gap);
            bool sel = i == selected;

            _canvas.FillRoundRect(x, y, segW, h, S(4), sel ? th.ControlOn : th.ControlTrack);

            string label = SegmentLabel(item.Id, i);
            int lw = _canvas.Measure(label, _panel.Renderer.FontHeader);
            uint fg = sel ? SegmentTextOn(th) : th.TextDim;
            _canvas.Text(x + (segW - lw) / 2, y + S(1), label, fg, _panel.Renderer.FontHeader);
        }
    }

    /// <summary>
    /// Text on a filled segment. Light's accent is dark enough to need white on it, while
    /// the dark themes' accents need near-black.
    /// </summary>
    private static uint SegmentTextOn(Theme th) =>
        th.Id == ThemeId.Light ? Win32.Rgb(0xFF, 0xFF, 0xFF) : Win32.Rgb(0x0C, 0x0E, 0x12);

    private void PaintButton(in Item item, Theme th)
    {
        int h = item.Height - S(8);
        int y = item.Y + S(4);
        bool hover = _hoverId == item.Id;

        _canvas.FillRoundRect(item.X, y, item.Width, h, S(5), hover ? th.Hover : th.ControlTrack);
        _canvas.StrokeRoundRect(item.X, y, item.Width - 1, h - 1, S(5), th.BorderColor);

        int lw = _canvas.Measure(item.Label, _panel.Renderer.FontLabel);
        _canvas.Text(item.X + (item.Width - lw) / 2, y + (h - S(16)) / 2, item.Label, th.TextPrimary, _panel.Renderer.FontLabel);
    }

    private int SegmentAt(in Item item, int mx)
    {
        int gap = S(4);
        int segW = (item.Width - gap * (item.SegmentCount - 1)) / item.SegmentCount;

        for (int i = 0; i < item.SegmentCount; i++)
        {
            int x = item.X + i * (segW + gap);
            if (mx >= x && mx < x + segW) return i;
        }
        return -1;
    }

    private static Win32.RECT WorkArea(nint hwnd)
    {
        nint mon = Win32.MonitorFromWindow(hwnd, Win32.MONITOR_DEFAULTTONEAREST);
        var mi = new Win32.MONITORINFO { cbSize = (uint)sizeof(Win32.MONITORINFO) };
        if (Win32.GetMonitorInfoW(mon, &mi) != 0) return mi.rcWork;
        return new Win32.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
    }

    public void Dispose()
    {
        Close();
        _canvas.Dispose();
        if (ReferenceEquals(_instance, this)) _instance = null;
    }
}

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Offender.Native;
using Offender.Sampling;

namespace Offender.Ui;

/// <summary>
/// The desktop panel: a frameless, always-on-top, non-activating tool window that paints
/// itself from the latest snapshot and hosts the tray icon and the taskbar readout.
///
/// Two compositing paths, chosen by theme:
///
/// - Opaque themes use ordinary WM_PAINT + BitBlt with a constant window alpha.
/// - Clear uses UpdateLayeredWindow with a per-pixel alpha recovered from ink brightness.
///
/// They are not unified on purpose. Recovering alpha from brightness only works for light
/// ink -- Light's near-black text would come out at roughly 11% alpha and vanish -- so the
/// per-pixel path is reserved for the one theme whose palette is built for it.
/// </summary>
internal sealed unsafe class PanelWindow : IDisposable
{
    public const uint WM_SNAPSHOT = Win32.WM_APP + 2;

    private const int SnapDistance = 16;

    // Menu command ids.
    private const uint IdSettings = 99;
    private const uint IdToggle = 100;
    private const uint IdExit = 101;
    private const uint IdExpand = 102;
    private const uint IdAlwaysOnTop = 103;
    private const uint IdThemeBase = 200;   // + ThemeId

    private static PanelWindow? _instance;
    private static uint _taskbarCreatedMessage;

    private readonly Config _config;
    private readonly Sampler _sampler;
    private readonly Renderer _renderer = new();
    private TrayIcon? _tray;
    private TaskbarHost? _taskbar;
    private SettingsWindow? _settings;

    private nint _hwnd;
    private int _width, _height;

    // Display state. Collapsed is the notch; expanded is the full panel.
    private bool _collapsed = true;

    // Set when the user clicks to expand, which suppresses hover-collapse until they
    // click again. Without it, a deliberate expand would vanish the moment the pointer
    // moved away.
    private bool _pinnedExpanded;

    private bool _hoverTracking;

    // Manual drag. The window reports HTCLIENT so that hover and click reach the client
    // area at all, which means dragging has to be implemented rather than inherited.
    private bool _dragging;
    private bool _dragMoved;
    private Win32.POINT _dragCursor;
    private int _dragWinX, _dragWinY;

    public PanelWindow(Config config, Sampler sampler)
    {
        _config = config;
        _sampler = sampler;
        _instance = this;
    }

    public nint Handle => _hwnd;
    public Config Config => _config;
    public Renderer Renderer => _renderer;
    public Theme Theme => _renderer.Theme;

    // ---------------------------------------------------------------- lifecycle

    public bool Create()
    {
        nint hInstance = Win32.GetModuleHandleW(null);

        fixed (char* className = "OffenderPanel")
        fixed (char* windowName = "Offender")
        fixed (char* taskbarCreated = "TaskbarCreated")
        {
            var wc = new Win32.WNDCLASSEXW
            {
                cbSize = (uint)sizeof(Win32.WNDCLASSEXW),
                style = Win32.CS_HREDRAW | Win32.CS_VREDRAW | Win32.CS_DBLCLKS,
                lpfnWndProc = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint>)&StaticWndProc,
                hInstance = hInstance,
                hCursor = Win32.LoadCursorW(0, Win32.IDC_ARROW),
                hbrBackground = 0,
                lpszClassName = className,
            };

            if (Win32.RegisterClassExW(&wc) == 0) return false;

            // Explorer restarts drop every tray icon; this message tells us to re-add ours.
            _taskbarCreatedMessage = Win32.RegisterWindowMessageW(taskbarCreated);

            ApplyConfigToRenderer();
            _renderer.SetScale(1f);
            _collapsed = _config.Collapsed;
            _width = _renderer.NotchWidth;
            _height = _renderer.NotchHeight;

            _hwnd = Win32.CreateWindowExW(
                Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_TOPMOST | Win32.WS_EX_LAYERED | Win32.WS_EX_NOACTIVATE,
                className, windowName,
                Win32.WS_POPUP | Win32.WS_CLIPCHILDREN,
                0, 0, _width, _height,
                0, 0, hInstance, 0);
        }

        if (_hwnd == 0) return false;

        // DPI is only knowable once the window exists and has landed on a monitor.
        _renderer.SetScale(Win32.GetDpiForWindow(_hwnd) / 96f);
        MeasureForState(null);

        ApplySurfaceMode();
        PositionInitial();
        ApplyCornerShape();
        ApplyTopmost();

        _tray = new TrayIcon(_hwnd) { Mode = _config.Tray };
        _tray.Update(null);

        if (_config.EmbedInTaskbar) EnableTaskbarEmbed(true);

        if (_config.ShowPanel) Win32.ShowWindow(_hwnd, Win32.SW_SHOWNOACTIVATE);
        _sampler.Idle = !_config.ShowPanel;

        return true;
    }

    /// <summary>Blocks on the message loop until the window is closed.</summary>
    public void RunMessageLoop()
    {
        Win32.MSG msg;
        while (Win32.GetMessageW(&msg, 0, 0, 0) > 0)
        {
            Win32.TranslateMessage(&msg);
            Win32.DispatchMessageW(&msg);
        }
    }

    /// <summary>Called from the sampler thread; hands the work to the UI thread.</summary>
    public void NotifySnapshot()
    {
        if (_hwnd != 0) Win32.PostMessageW(_hwnd, WM_SNAPSHOT, 0, 0);
    }

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
        if (msg == _taskbarCreatedMessage && _taskbarCreatedMessage != 0)
        {
            // Explorer restarted: the tray icon is gone and any taskbar child was
            // destroyed with its parent. Rebuild both.
            _tray?.Dispose();
            _tray = new TrayIcon(hwnd) { Mode = _config.Tray };
            _tray.Update(_sampler.Current);

            if (_config.EmbedInTaskbar)
            {
                _taskbar?.Dispose();
                _taskbar = null;
                EnableTaskbarEmbed(true);
            }
            return 0;
        }

        switch (msg)
        {
            case WM_SNAPSHOT:
                OnSnapshot();
                return 0;

            case Win32.WM_PAINT:
            {
                Win32.PAINTSTRUCT ps;
                nint dc = Win32.BeginPaint(hwnd, &ps);
                if (dc != 0)
                {
                    if (_renderer.PerPixelAlpha) PushLayeredFrame();
                    else if (_collapsed) _renderer.PaintNotchTo(dc, _width, _height, _sampler.Current, SurfaceAlpha());
                    else _renderer.PaintTo(dc, _width, _height, _sampler.Current, SurfaceAlpha());
                    Win32.EndPaint(hwnd, &ps);
                }
                return 0;
            }

            case Win32.WM_ERASEBKGND:
                return 1; // fully repainted every time; erasing would only flicker

            case Win32.WM_NCHITTEST:
                // Reporting HTCAPTION would give free dragging but would also route every
                // mouse message to the non-client area, where hover-to-expand never sees
                // them. Dragging is done by hand instead.
                return Win32.HTCLIENT;

            case Win32.WM_LBUTTONDOWN:
                BeginDrag();
                return 0;

            case Win32.WM_MOUSEMOVE:
                OnMouseMove();
                return 0;

            case Win32.WM_LBUTTONUP:
                EndDrag();
                return 0;

            case Win32.WM_MOUSELEAVE:
                OnMouseLeave();
                return 0;

            case Win32.WM_RBUTTONUP:
            case Win32.WM_NCRBUTTONUP:
                ShowContextMenu();
                return 0;

            case Win32.WM_EXITSIZEMOVE:
                SnapAndSave();
                return 0;

            case Win32.WM_DPICHANGED:
                OnDpiChanged(lParam);
                return 0;

            case Win32.WM_COMMAND:
                OnCommand((uint)(wParam & 0xFFFF));
                return 0;

            case TrayIcon.CallbackMessage:
                OnTrayMessage((uint)(lParam & 0xFFFF));
                return 0;

            case Win32.WM_DESTROY:
                Win32.PostQuitMessage(0);
                return 0;
        }

        return Win32.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    // ---------------------------------------------------------------- handlers

    private void OnSnapshot()
    {
        var s = _sampler.Current;
        if (s is null) return;

        _renderer.PushHistory(s);
        _tray?.Update(s);
        _taskbar?.Update(s);

        if (!IsPanelVisible()) return;

        // The panel grows and shrinks as rows are toggled, or as optional rows appear --
        // a GPU counter that only resolves once the driver warms up, say. The notch is a
        // fixed size and never reflows.
        int wanted = _collapsed ? _renderer.NotchHeight : _renderer.MeasureHeight(s);
        if (wanted != _height)
        {
            _height = wanted;
            Win32.SetWindowPos(_hwnd, 0, 0, 0, _width, _height,
                Win32.SWP_NOMOVE | Win32.SWP_NOACTIVATE | Win32.SWP_NOZORDER);
            ApplyCornerShape();
        }

        if (_renderer.PerPixelAlpha) PushLayeredFrame();
        else Win32.InvalidateRect(_hwnd, null, 0);
    }

    // ---------------------------------------------------------------- notch state

    /// <summary>Sets width/height for the current state without moving the window.</summary>
    private void MeasureForState(Snapshot? s)
    {
        if (_collapsed)
        {
            _width = _renderer.NotchWidth;
            _height = _renderer.NotchHeight;
        }
        else
        {
            _width = _renderer.PanelWidth;
            _height = _renderer.MeasureHeight(s);
        }
    }

    /// <summary>
    /// Switches between notch and panel, growing from the notch's own centre so the panel
    /// appears to unfold out of it rather than jumping somewhere else.
    /// </summary>
    private void SetCollapsed(bool collapsed, bool pinned = false)
    {
        if (_collapsed == collapsed)
        {
            if (pinned && !collapsed) Pin();
            return;
        }

        Win32.RECT before;
        Win32.GetWindowRect(_hwnd, &before);
        var work = WorkAreaFor(_hwnd);

        _collapsed = collapsed;
        _pinnedExpanded = pinned && !collapsed;
        _config.Collapsed = collapsed;

        MeasureForState(_sampler.Current);

        int x = AnchorAxis(before.Left, before.Right, work.Left, work.Right, _width);
        int y = AnchorAxis(before.Top, before.Bottom, work.Top, work.Bottom, _height);

        // Keep the whole thing on the monitor it is already on.
        if (x < work.Left) x = work.Left;
        if (x + _width > work.Right) x = work.Right - _width;
        if (y < work.Top) y = work.Top;
        if (y + _height > work.Bottom) y = Math.Max(work.Top, work.Bottom - _height);

        Win32.SetWindowPos(_hwnd, 0, x, y, _width, _height,
            Win32.SWP_NOACTIVATE | Win32.SWP_NOZORDER);

        ApplyCornerShape();
        ApplyTopmost();
        Refresh();
        _config.Save();
    }

    /// <summary>A window within this many pixels of both edges counts as centred.</summary>
    private const int AnchorTolerance = 24;

    /// <summary>
    /// Decides where one axis lands after a resize, by holding whichever edge the window
    /// is already nearest and letting it grow inward.
    ///
    /// Always holding the leading edge only works for a top-left-ish widget. A panel
    /// docked to the bottom would expand downward past the screen, get clamped back up,
    /// and then collapse to wherever the clamp left it instead of returning to its corner.
    /// Holding the near edge keeps a corner-docked panel in its corner and a centred one
    /// centred.
    /// </summary>
    private static int AnchorAxis(int near, int far, int workNear, int workFar, int size)
    {
        int gapNear = near - workNear;
        int gapFar = workFar - far;

        if (Math.Abs(gapNear - gapFar) <= AnchorTolerance * 2)
            return near + (far - near) / 2 - size / 2;   // centred: stay centred

        return gapFar < gapNear ? far - size : near;
    }

    /// <summary>
    /// Keeps an already-expanded panel open after the pointer leaves, and re-applies
    /// z-order since a pinned panel is the one case that honours the always-on-top setting.
    /// </summary>
    private void Pin()
    {
        if (_collapsed || _pinnedExpanded) return;
        _pinnedExpanded = true;
        ApplyTopmost();
        Refresh();
    }

    /// <summary>
    /// The notch is always topmost -- a tiny screen-edge readout buried under a window
    /// would be pointless. A full panel that refuses to go behind anything is obnoxious,
    /// so the expanded overlay honours the user's setting.
    ///
    /// The exception is a hover-expansion, which stays topmost regardless: it unfolds over
    /// whatever window the pointer is already on, so dropping it behind that window would
    /// make it vanish the instant it appeared. Only a deliberately pinned panel goes
    /// behind other windows when the setting is off.
    /// </summary>
    private void ApplyTopmost()
    {
        bool topmost = _collapsed || !_pinnedExpanded || _config.AlwaysOnTop;
        Win32.SetWindowPos(_hwnd, topmost ? Win32.HWND_TOPMOST : Win32.HWND_NOTOPMOST,
            0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
    }

    private void BeginDrag()
    {
        Win32.RECT r;
        Win32.GetWindowRect(_hwnd, &r);
        Win32.POINT cursor;
        Win32.GetCursorPos(&cursor);

        _dragging = true;
        _dragMoved = false;
        _dragCursor = cursor;
        _dragWinX = r.Left;
        _dragWinY = r.Top;
        Win32.SetCapture(_hwnd);
    }

    private void OnMouseMove()
    {
        if (_dragging)
        {
            Win32.POINT cursor;
            Win32.GetCursorPos(&cursor);
            int dx = cursor.X - _dragCursor.X;
            int dy = cursor.Y - _dragCursor.Y;

            // A few pixels of slop, so a click with a twitch in it is still a click.
            if (!_dragMoved && Math.Abs(dx) + Math.Abs(dy) > 4) _dragMoved = true;
            if (_dragMoved)
            {
                Win32.SetWindowPos(_hwnd, 0, _dragWinX + dx, _dragWinY + dy, 0, 0,
                    Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | Win32.SWP_NOZORDER);
                if (_renderer.PerPixelAlpha) PushLayeredFrame();
            }
            return;
        }

        ArmMouseLeave();

        if (_config.ExpandOnHover && _collapsed) SetCollapsed(false);
    }

    private void EndDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        Win32.ReleaseCapture();

        if (_dragMoved) { SnapAndSave(); return; }

        // A click with no movement cycles: notch -> pinned open -> notch.
        //
        // The middle step matters when hover-expansion is on. Hovering has already
        // expanded the panel by the time the click lands, so "click toggles collapsed"
        // would collapse it and hover would immediately expand it again -- the pinned
        // state would be unreachable. Clicking an unpinned panel pins it instead.
        if (_collapsed) SetCollapsed(false, pinned: true);
        else if (!_pinnedExpanded) Pin();
        else SetCollapsed(true);
    }

    /// <summary>
    /// Collapses a hover-expanded panel, but only if the pointer really has left.
    ///
    /// Expanding resizes the window, and a resize under the pointer makes Windows deliver
    /// WM_MOUSELEAVE even though the pointer never moved out. Acting on that message
    /// blindly produces a collapse/expand oscillation -- the panel appears to flicker, and
    /// which state you observe depends on timing. Re-checking the cursor against the
    /// current window rect makes the collapse trustworthy.
    /// </summary>
    private void OnMouseLeave()
    {
        _hoverTracking = false;

        Win32.POINT cursor;
        Win32.RECT r;
        if (Win32.GetCursorPos(&cursor) != 0 && Win32.GetWindowRect(_hwnd, &r) != 0)
        {
            bool stillInside = cursor.X >= r.Left && cursor.X < r.Right
                            && cursor.Y >= r.Top && cursor.Y < r.Bottom;
            if (stillInside) { ArmMouseLeave(); return; }
        }

        // Only hover-expansion collapses on leave; a click-pinned panel stays.
        if (_config.ExpandOnHover && !_collapsed && !_pinnedExpanded) SetCollapsed(true);
    }

    /// <summary>
    /// Asks for a single WM_MOUSELEAVE. It is one-shot, so it has to be re-armed on every
    /// move once it has fired.
    /// </summary>
    private void ArmMouseLeave()
    {
        if (_hoverTracking) return;

        var tme = new Win32.TRACKMOUSEEVENT
        {
            cbSize = (uint)sizeof(Win32.TRACKMOUSEEVENT),
            dwFlags = Win32.TME_LEAVE,
            hwndTrack = _hwnd,
            dwHoverTime = 0,
        };
        if (Win32.TrackMouseEvent(&tme) != 0) _hoverTracking = true;
    }

    private void OnTrayMessage(uint mouseMessage)
    {
        switch (mouseMessage)
        {
            case Win32.WM_LBUTTONUP: TogglePanel(); break;
            case Win32.WM_RBUTTONUP: ShowContextMenu(); break;
        }
    }

    private void OnCommand(uint id)
    {
        if (id >= IdThemeBase && id < IdThemeBase + 16)
        {
            SetTheme((ThemeId)(id - IdThemeBase));
            return;
        }

        switch (id)
        {
            case IdSettings: OpenSettings(); return;
            case IdToggle: TogglePanel(); break;
            case IdExpand: SetCollapsed(!_collapsed, pinned: _collapsed); return;
            case IdAlwaysOnTop:
                _config.AlwaysOnTop = !_config.AlwaysOnTop;
                ApplyTopmost();
                break;
            case IdExit: Win32.DestroyWindow(_hwnd); return;
            default: return;
        }

        _config.Save();
    }

    private void OnDpiChanged(nint lParam)
    {
        // lParam points at the rect Windows wants the window moved to.
        var suggested = (Win32.RECT*)lParam;
        _renderer.SetScale(Win32.GetDpiForWindow(_hwnd) / 96f);
        MeasureForState(_sampler.Current);

        Win32.SetWindowPos(_hwnd, 0, suggested->Left, suggested->Top, _width, _height,
            Win32.SWP_NOACTIVATE | Win32.SWP_NOZORDER);
        ApplyCornerShape();
        Refresh();
    }

    // ---------------------------------------------------------------- rendering

    /// <summary>Effective surface opacity, 0-255. Clamped away from invisible on opaque themes.</summary>
    private byte SurfaceAlpha()
    {
        int a = Math.Clamp(_config.Opacity, 0, 255);
        if (!_renderer.PerPixelAlpha && a < 60) a = 60;
        return (byte)a;
    }

    /// <summary>Renders and hands the whole surface to the compositor, alpha and all.</summary>
    private void PushLayeredFrame()
    {
        nint screen = Win32.GetDC(0);
        if (screen == 0) return;

        if (_collapsed)
            _renderer.RenderNotch(screen, _width, _height, _sampler.Current, SurfaceAlpha());
        else
            _renderer.Render(screen, _width, _height, _sampler.Current, SurfaceAlpha());

        if (_renderer.SurfaceDc != 0)
        {
            Win32.RECT wr;
            Win32.GetWindowRect(_hwnd, &wr);

            var pos = new Win32.POINT { X = wr.Left, Y = wr.Top };
            var size = new Win32.SIZE { cx = _width, cy = _height };
            var src = new Win32.POINT { X = 0, Y = 0 };
            var blend = new Win32.BLENDFUNCTION
            {
                BlendOp = Win32.AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = Win32.AC_SRC_ALPHA,
            };

            Win32.UpdateLayeredWindow(_hwnd, screen, &pos, &size,
                _renderer.SurfaceDc, &src, 0, &blend, Win32.ULW_ALPHA);
        }

        Win32.ReleaseDC(0, screen);
    }

    /// <summary>Repaints through whichever path the active theme uses.</summary>
    public void Refresh()
    {
        if (!IsPanelVisible()) return;
        if (_renderer.PerPixelAlpha) PushLayeredFrame();
        else Win32.InvalidateRect(_hwnd, null, 1);
    }

    /// <summary>
    /// Configures the window for the active theme's compositing mode: constant alpha and
    /// optional acrylic for the opaque themes, nothing for the per-pixel one (its alpha
    /// arrives with every frame).
    /// </summary>
    private void ApplySurfaceMode()
    {
        if (_renderer.PerPixelAlpha)
        {
            SetAcrylic(false);
            // Corner rounding is drawn into the alpha channel ourselves.
            SetDwmRounding(false);
        }
        else
        {
            Win32.SetLayeredWindowAttributes(_hwnd, 0, SurfaceAlpha(), Win32.LWA_ALPHA);
            SetDwmRounding(true);
            SetAcrylic(_renderer.Theme.Acrylic);
        }
    }

    private void SetDwmRounding(bool on)
    {
        // Win11 only; harmlessly ignored on Win10.
        int pref = on ? Win32.DWMWCP_ROUND : 1 /* DWMWCP_DONOTROUND */;
        Win32.DwmSetWindowAttribute(_hwnd, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, &pref, sizeof(int));
    }

    /// <summary>
    /// Applies the configured corner radius.
    ///
    /// The two compositing paths round corners very differently, and it is worth being
    /// explicit about the tradeoff:
    ///
    /// - Clear composites its own alpha, so the radius is exact and antialiased.
    /// - The opaque themes are a rectangular window whose corners DWM rounds for us, and
    ///   DWM exposes three states, not a radius. To honour an arbitrary number the window
    ///   is clipped with a region instead. That is exact but hard-edged, because regions
    ///   are 1-bit masks -- there is no antialiasing to be had.
    ///
    /// DWM rounding is left on only when the radius is the default, so the common case
    /// keeps the smooth native corner and a custom radius gets exactness instead.
    /// </summary>
    private void ApplyCornerShape()
    {
        if (_hwnd == 0) return;

        int radius = _renderer.S(Math.Clamp(_config.CornerRadius,
            Metrics.CornerRadiusMin, Metrics.CornerRadiusMax));

        if (_renderer.PerPixelAlpha)
        {
            // The alpha channel already carries the shape; a region would only fight it.
            Win32.SetWindowRgn(_hwnd, 0, 1);
            SetDwmRounding(false);
            return;
        }

        bool useDwm = _config.CornerRadius == Metrics.CornerRadiusDefault;
        if (useDwm)
        {
            Win32.SetWindowRgn(_hwnd, 0, 1);
            SetDwmRounding(true);
            return;
        }

        SetDwmRounding(false);

        if (radius <= 0)
        {
            Win32.SetWindowRgn(_hwnd, 0, 1);   // square
            return;
        }

        // CreateRoundRectRgn's bottom-right is exclusive, hence the +1s.
        nint rgn = Win32.CreateRoundRectRgn(0, 0, _width + 1, _height + 1, radius * 2, radius * 2);
        if (rgn == 0) return;

        // The window owns the region once this succeeds, so it must not be deleted here.
        if (Win32.SetWindowRgn(_hwnd, rgn, 1) == 0) Win32.DeleteObject(rgn);
    }

    /// <summary>
    /// Asks DWM for an acrylic blur behind the window. There is no public API for this on
    /// a plain HWND, so a failure here is expected on some builds and simply leaves the
    /// theme looking like a solid panel.
    /// </summary>
    private void SetAcrylic(bool on)
    {
        var accent = new Win32.ACCENT_POLICY
        {
            AccentState = on ? Win32.ACCENT_ENABLE_ACRYLICBLURBEHIND : 0,
            AccentFlags = 2,
            // 0xAABBGGRR: the tint DWM mixes into the blur.
            GradientColor = on ? 0x99141614u : 0,
            AnimationId = 0,
        };

        var data = new Win32.WINDOWCOMPOSITIONATTRIBDATA
        {
            Attribute = Win32.WCA_ACCENT_POLICY,
            Data = &accent,
            SizeOfData = (uint)sizeof(Win32.ACCENT_POLICY),
        };

        Win32.SetWindowCompositionAttribute(_hwnd, &data);
    }

    // ---------------------------------------------------------------- settings plumbing

    private void ApplyConfigToRenderer()
    {
        _renderer.Theme = Theme.ById(_config.Theme);
        _renderer.ShowCpu = _config.ShowCpu;
        _renderer.ShowRam = _config.ShowRam;
        _renderer.ShowNet = _config.ShowNet;
        _renderer.ShowDisk = _config.ShowDisk;
        _renderer.ShowGpu = _config.ShowGpu;
        _renderer.ShowProcesses = _config.ShowProcesses;

        _renderer.NotchCpu = _config.NotchCpu;
        _renderer.NotchRam = _config.NotchRam;
        _renderer.NotchGpu = _config.NotchGpu;
        _renderer.NotchDisk = _config.NotchDisk;
        _renderer.NotchNetDown = _config.NotchNetDown;
        _renderer.NotchNetUp = _config.NotchNetUp;

        _renderer.CornerRadius = Math.Clamp(_config.CornerRadius,
            Metrics.CornerRadiusMin, Metrics.CornerRadiusMax);
    }

    /// <summary>
    /// Re-reads every setting and rebuilds whatever it affects. The settings window calls
    /// this after any change, so there is one path for applying configuration rather than
    /// one per control.
    /// </summary>
    public void ApplySettings()
    {
        bool wasPerPixel = _renderer.PerPixelAlpha;

        ApplyConfigToRenderer();

        _sampler.IntervalMs = _config.IntervalMs;
        _sampler.ProcessEveryNTicks = Math.Max(1, 2000 / Math.Max(250, _config.IntervalMs));
        _sampler.PinnedInterface = string.IsNullOrWhiteSpace(_config.PinnedInterface) ? null : _config.PinnedInterface;

        // The GPU wildcard counters are the single most expensive thing sampled, so they
        // are only kept alive while something on screen actually shows a GPU figure.
        _sampler.GpuWanted = _config.ShowGpu || _config.NotchGpu;

        if (_tray is not null) _tray.Mode = _config.Tray;
        _tray?.Update(_sampler.Current);

        if (_config.EmbedInTaskbar && _taskbar is null) EnableTaskbarEmbed(true);
        else if (!_config.EmbedInTaskbar && _taskbar is not null) EnableTaskbarEmbed(false);
        _taskbar?.SetTheme(_renderer.Theme);

        // Switching between compositing modes leaves stale window state behind, so the
        // window has to be re-prepared and fully redrawn.
        ApplySurfaceMode();
        ApplyTopmost();
        if (wasPerPixel != _renderer.PerPixelAlpha) ResetLayeredSurface();

        ResizeToContent();
        _settings?.OnThemeChanged();
        _config.Save();
    }

    /// <summary>
    /// Leaving the per-pixel path leaves the last UpdateLayeredWindow surface on screen
    /// until something repaints it. Hiding and reshowing the window clears it.
    /// </summary>
    private void ResetLayeredSurface()
    {
        if (!IsPanelVisible()) return;
        Win32.ShowWindow(_hwnd, Win32.SW_HIDE);
        Win32.ShowWindow(_hwnd, Win32.SW_SHOWNOACTIVATE);
        Refresh();
    }

    /// <summary>Flips between notch and panel. Used by the settings window and the menu.</summary>
    public void ToggleCollapsed() => SetCollapsed(!_collapsed, pinned: _collapsed);

    public void SetTheme(ThemeId id)
    {
        _config.Theme = id;
        // Each theme carries the surface opacity it was designed around; switching adopts
        // it, and the slider then moves from there.
        _config.Opacity = Theme.ById(id).SurfaceAlpha;
        ApplySettings();
    }

    private void OpenSettings()
    {
        if (_settings is { Alive: true }) { _settings.Focus(); return; }

        _settings?.Dispose();
        _settings = new SettingsWindow(this);
        if (!_settings.Create()) { _settings.Dispose(); _settings = null; }
    }

    private void EnableTaskbarEmbed(bool on)
    {
        if (!on)
        {
            _taskbar?.Dispose();
            _taskbar = null;
            return;
        }

        _taskbar = new TaskbarHost(_renderer.Theme, _config);
        if (!_taskbar.Create())
        {
            // No taskbar to attach to, or the shell refused. Fall back silently to the
            // tray icon, which always works.
            _taskbar.Dispose();
            _taskbar = null;
            _config.EmbedInTaskbar = false;
        }
        else
        {
            _taskbar.Update(_sampler.Current);
        }
    }

    // ---------------------------------------------------------------- actions

    private bool IsPanelVisible() => _hwnd != 0 && Win32.IsWindowVisible(_hwnd) != 0;

    private void TogglePanel()
    {
        bool show = !IsPanelVisible();
        _config.ShowPanel = show;
        Win32.ShowWindow(_hwnd, show ? Win32.SW_SHOWNOACTIVATE : Win32.SW_HIDE);

        // Hidden means only the tray and taskbar need feeding, so the sampler can relax.
        _sampler.Idle = !show;

        if (show) Refresh();
        _config.Save();
    }

    /// <summary>
    /// Re-measures for the current state and repositions. Unlike the per-snapshot resize
    /// this also handles width, because toggling notch elements changes it -- and it
    /// re-anchors, so a corner-docked notch stays in its corner as it grows or shrinks.
    /// </summary>
    private void ResizeToContent()
    {
        Win32.RECT before;
        if (Win32.GetWindowRect(_hwnd, &before) == 0) { Refresh(); return; }

        int oldW = _width, oldH = _height;
        MeasureForState(_sampler.Current);

        if (_width == oldW && _height == oldH) { Refresh(); return; }

        var work = WorkAreaFor(_hwnd);
        int x = AnchorAxis(before.Left, before.Right, work.Left, work.Right, _width);
        int y = AnchorAxis(before.Top, before.Bottom, work.Top, work.Bottom, _height);

        if (x < work.Left) x = work.Left;
        if (x + _width > work.Right) x = work.Right - _width;
        if (y < work.Top) y = work.Top;
        if (y + _height > work.Bottom) y = Math.Max(work.Top, work.Bottom - _height);

        Win32.SetWindowPos(_hwnd, 0, x, y, _width, _height,
            Win32.SWP_NOACTIVATE | Win32.SWP_NOZORDER);

        _config.X = x;
        _config.Y = y;

        ApplyCornerShape();
        Refresh();
    }

    private void PositionInitial()
    {
        int x = _config.X, y = _config.Y;

        if (x < 0 || y < 0 || !IsOnAnyMonitor(x, y))
        {
            // Default position is the lower-right corner of the work area, flush against
            // both edges. The work area excludes the taskbar, so this sits directly above
            // it rather than underneath.
            var work = WorkAreaFor(_hwnd);
            x = work.Right - _width;
            y = work.Bottom - _height;
        }

        Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, x, y, _width, _height,
            Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
        _config.X = x;
        _config.Y = y;
    }

    private void SnapAndSave()
    {
        Win32.RECT r;
        if (Win32.GetWindowRect(_hwnd, &r) == 0) return;

        var work = WorkAreaFor(_hwnd);
        int x = r.Left, y = r.Top;

        if (Math.Abs(x - work.Left) <= SnapDistance) x = work.Left;
        if (Math.Abs(r.Right - work.Right) <= SnapDistance) x = work.Right - r.Width;
        if (Math.Abs(y - work.Top) <= SnapDistance) y = work.Top;
        if (Math.Abs(r.Bottom - work.Bottom) <= SnapDistance) y = work.Bottom - r.Height;

        if (x != r.Left || y != r.Top)
            Win32.SetWindowPos(_hwnd, 0, x, y, 0, 0,
                Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | Win32.SWP_NOZORDER);

        _config.X = x;
        _config.Y = y;
        _config.Save();

        // A layered frame is positioned by the frame itself, so it has to be re-pushed.
        if (_renderer.PerPixelAlpha) PushLayeredFrame();
    }

    /// <summary>
    /// Guards against restoring a position that belonged to a monitor that is no longer
    /// attached -- MonitorFromPoint always returns the nearest monitor, so the saved point
    /// has to be checked against that monitor's actual work area.
    /// </summary>
    private static bool IsOnAnyMonitor(int x, int y)
    {
        var pt = new Win32.POINT { X = x + 8, Y = y + 8 };
        nint mon = Win32.MonitorFromPoint(pt, Win32.MONITOR_DEFAULTTONEAREST);
        if (mon == 0) return false;

        var mi = new Win32.MONITORINFO { cbSize = (uint)sizeof(Win32.MONITORINFO) };
        if (Win32.GetMonitorInfoW(mon, &mi) == 0) return false;

        return pt.X >= mi.rcWork.Left && pt.X < mi.rcWork.Right
            && pt.Y >= mi.rcWork.Top && pt.Y < mi.rcWork.Bottom;
    }

    private static Win32.RECT WorkAreaFor(nint hwnd)
    {
        nint mon = Win32.MonitorFromWindow(hwnd, Win32.MONITOR_DEFAULTTONEAREST);
        var mi = new Win32.MONITORINFO { cbSize = (uint)sizeof(Win32.MONITORINFO) };
        if (Win32.GetMonitorInfoW(mon, &mi) != 0) return mi.rcWork;
        return new Win32.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
    }

    // ---------------------------------------------------------------- menu

    private void ShowContextMenu()
    {
        nint menu = Win32.CreatePopupMenu();
        if (menu == 0) return;

        AddItem(menu, IdSettings, "Settings…", false);
        AddItem(menu, IdExpand, _collapsed ? "Expand" : "Collapse to notch", false);
        AddItem(menu, IdAlwaysOnTop, "Always on top", _config.AlwaysOnTop);
        AddSeparator(menu);

        foreach (var t in Theme.All)
            AddItem(menu, IdThemeBase + (uint)t.Id, t.Name, t.Id == _config.Theme);

        AddSeparator(menu);
        AddItem(menu, IdToggle, IsPanelVisible() ? "Hide panel" : "Show panel", false);
        AddItem(menu, IdExit, "Exit", false);

        Win32.POINT pt;
        Win32.GetCursorPos(&pt);

        // Required so the menu dismisses when the user clicks elsewhere.
        Win32.SetForegroundWindow(_hwnd);

        int cmd = Win32.TrackPopupMenu(menu, Win32.TPM_RIGHTBUTTON | Win32.TPM_RETURNCMD,
            pt.X, pt.Y, 0, _hwnd, 0);

        Win32.DestroyMenu(menu);
        if (cmd != 0) OnCommand((uint)cmd);
    }

    private static void AddItem(nint menu, uint id, string text, bool check)
    {
        fixed (char* p = text)
            Win32.AppendMenuW(menu, Win32.MF_STRING | (check ? Win32.MF_CHECKED : 0), id, p);
    }

    private static void AddSeparator(nint menu) => Win32.AppendMenuW(menu, Win32.MF_SEPARATOR, 0, null);

    public void Dispose()
    {
        _settings?.Dispose();
        _taskbar?.Dispose();
        _tray?.Dispose();
        _renderer.Dispose();
        _instance = null;
    }
}

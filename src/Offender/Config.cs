namespace Offender;

/// <summary>What the tray icon renders.</summary>
internal enum TrayMode { NetSpeed = 0, Cpu = 1, Ram = 2 }

/// <summary>
/// Settings, stored as flat key=value text in %APPDATA%\Offender\config.ini.
///
/// Deliberately not JSON: a hand-rolled parser is a few dozen lines, has no serializer
/// to keep alive under trimming, and produces a file a user can edit in Notepad.
/// </summary>
internal sealed class Config
{
    public int X = -1;              // -1 means "not positioned yet"; snapped on first show
    public int Y = -1;
    public int IntervalMs = 1000;
    // Defaults to Glass's own surface alpha. A mismatch here shows up as the panel
    // opening at an opacity its theme was not designed around.
    public int Opacity = 190;       // 0..255
    public bool ShowCpu = true;
    public bool ShowRam = true;
    public bool ShowNet = true;
    public bool ShowDisk = true;
    public bool ShowGpu = true;
    public bool ShowProcesses = true;
    public bool ShowPanel = true;

    /// <summary>Idle state. The notch is the default; the full panel is the expanded state.</summary>
    public bool Collapsed = true;

    /// <summary>Expand the notch on hover, collapse again when the pointer leaves.</summary>
    public bool ExpandOnHover = true;

    // Which elements the collapsed notch shows. Separate from the panel's row toggles --
    // the notch is a glance, the panel is the detail, and they rarely want the same set.
    public bool NotchCpu = true;
    public bool NotchRam = true;
    public bool NotchGpu;
    public bool NotchDisk;
    public bool NotchNetDown = true;
    public bool NotchNetUp = true;

    /// <summary>Corner radius in logical pixels, shared by the notch and the panel.</summary>
    public int CornerRadius = Ui.Metrics.CornerRadiusDefault;

    /// <summary>
    /// Applies to the expanded overlay only. The notch is always topmost -- it is a
    /// screen-edge HUD, and one buried behind a window is just wasted pixels.
    /// </summary>
    public bool AlwaysOnTop;
    public bool StartWithWindows;
    public bool EmbedInTaskbar;
    public string PinnedInterface = "";
    public TrayMode Tray = TrayMode.NetSpeed;
    public Ui.ThemeId Theme = Ui.ThemeId.Glass;

    public static string Path
    {
        get
        {
            string dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Offender");
            return System.IO.Path.Combine(dir, "config.ini");
        }
    }

    public static Config Load()
    {
        var c = new Config();
        string path = Path;
        if (!File.Exists(path)) return c;

        try
        {
            foreach (string raw in File.ReadLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] is '#' or ';') continue;

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;

                string key = line[..eq].Trim();
                string val = line[(eq + 1)..].Trim();

                switch (key)
                {
                    case "x": c.X = ParseInt(val, c.X); break;
                    case "y": c.Y = ParseInt(val, c.Y); break;
                    case "interval_ms": c.IntervalMs = Math.Clamp(ParseInt(val, c.IntervalMs), 250, 10000); break;
                    case "opacity": c.Opacity = Math.Clamp(ParseInt(val, c.Opacity), 60, 255); break;
                    case "show_cpu": c.ShowCpu = ParseBool(val, c.ShowCpu); break;
                    case "show_ram": c.ShowRam = ParseBool(val, c.ShowRam); break;
                    case "show_net": c.ShowNet = ParseBool(val, c.ShowNet); break;
                    case "show_disk": c.ShowDisk = ParseBool(val, c.ShowDisk); break;
                    case "show_gpu": c.ShowGpu = ParseBool(val, c.ShowGpu); break;
                    case "show_processes": c.ShowProcesses = ParseBool(val, c.ShowProcesses); break;
                    case "show_panel": c.ShowPanel = ParseBool(val, c.ShowPanel); break;
                    case "collapsed": c.Collapsed = ParseBool(val, c.Collapsed); break;
                    case "expand_on_hover": c.ExpandOnHover = ParseBool(val, c.ExpandOnHover); break;
                    case "always_on_top": c.AlwaysOnTop = ParseBool(val, c.AlwaysOnTop); break;
                    case "notch_cpu": c.NotchCpu = ParseBool(val, c.NotchCpu); break;
                    case "notch_ram": c.NotchRam = ParseBool(val, c.NotchRam); break;
                    case "notch_gpu": c.NotchGpu = ParseBool(val, c.NotchGpu); break;
                    case "notch_disk": c.NotchDisk = ParseBool(val, c.NotchDisk); break;
                    case "notch_net_down": c.NotchNetDown = ParseBool(val, c.NotchNetDown); break;
                    case "notch_net_up": c.NotchNetUp = ParseBool(val, c.NotchNetUp); break;
                    case "corner_radius":
                        c.CornerRadius = Math.Clamp(ParseInt(val, c.CornerRadius),
                            Ui.Metrics.CornerRadiusMin, Ui.Metrics.CornerRadiusMax);
                        break;
                    case "start_with_windows": c.StartWithWindows = ParseBool(val, c.StartWithWindows); break;
                    case "embed_in_taskbar": c.EmbedInTaskbar = ParseBool(val, c.EmbedInTaskbar); break;
                    case "pin_interface": c.PinnedInterface = val; break;
                    case "tray": c.Tray = (TrayMode)Math.Clamp(ParseInt(val, (int)c.Tray), 0, 2); break;
                    case "theme": c.Theme = (Ui.ThemeId)Math.Clamp(ParseInt(val, (int)c.Theme), 0, 3); break;
                }
            }
        }
        catch (IOException) { /* unreadable config is not worth failing startup over */ }
        catch (UnauthorizedAccessException) { }

        return c;
    }

    public void Save()
    {
        try
        {
            string path = Path;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);

            using var w = new StreamWriter(path, append: false);
            w.WriteLine("# Offender settings");
            w.WriteLine($"x={X}");
            w.WriteLine($"y={Y}");
            w.WriteLine($"interval_ms={IntervalMs}");
            w.WriteLine($"opacity={Opacity}");
            w.WriteLine($"show_cpu={(ShowCpu ? 1 : 0)}");
            w.WriteLine($"show_ram={(ShowRam ? 1 : 0)}");
            w.WriteLine($"show_net={(ShowNet ? 1 : 0)}");
            w.WriteLine($"show_disk={(ShowDisk ? 1 : 0)}");
            w.WriteLine($"show_gpu={(ShowGpu ? 1 : 0)}");
            w.WriteLine($"show_processes={(ShowProcesses ? 1 : 0)}");
            w.WriteLine($"show_panel={(ShowPanel ? 1 : 0)}");
            w.WriteLine($"collapsed={(Collapsed ? 1 : 0)}");
            w.WriteLine($"expand_on_hover={(ExpandOnHover ? 1 : 0)}");
            w.WriteLine($"always_on_top={(AlwaysOnTop ? 1 : 0)}");
            w.WriteLine($"notch_cpu={(NotchCpu ? 1 : 0)}");
            w.WriteLine($"notch_ram={(NotchRam ? 1 : 0)}");
            w.WriteLine($"notch_gpu={(NotchGpu ? 1 : 0)}");
            w.WriteLine($"notch_disk={(NotchDisk ? 1 : 0)}");
            w.WriteLine($"notch_net_down={(NotchNetDown ? 1 : 0)}");
            w.WriteLine($"notch_net_up={(NotchNetUp ? 1 : 0)}");
            w.WriteLine($"corner_radius={CornerRadius}");
            w.WriteLine($"start_with_windows={(StartWithWindows ? 1 : 0)}");
            w.WriteLine($"embed_in_taskbar={(EmbedInTaskbar ? 1 : 0)}");
            w.WriteLine($"pin_interface={PinnedInterface}");
            w.WriteLine($"tray={(int)Tray}");
            w.WriteLine($"theme={(int)Theme}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static int ParseInt(string s, int fallback) => int.TryParse(s, out int v) ? v : fallback;

    private static bool ParseBool(string s, bool fallback) =>
        s switch { "1" or "true" or "yes" => true, "0" or "false" or "no" => false, _ => fallback };
}

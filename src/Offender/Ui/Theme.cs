using Offender.Native;

namespace Offender.Ui;

internal enum ThemeId { Glass = 0, Amoled = 1, Light = 2, Clear = 3 }

/// <summary>
/// Layout metrics, in logical pixels at 96 DPI. Scaled once per DPI change so nothing in
/// the paint path does DPI maths.
/// </summary>
internal static class Metrics
{
    public const int PanelWidth = 264;
    public const int PadX = 12;
    public const int PadY = 10;
    public const int MetricRowHeight = 26;
    public const int SparkWidth = 84;
    public const int SparkHeight = 16;
    public const int LabelWidth = 34;
    public const int SectionGap = 8;
    public const int HeaderHeight = 18;
    public const int ProcRowHeight = 19;

    public const int HistorySamples = 84;   // one sample per sparkline pixel column

    // The collapsed "notch".
    //
    // Each element occupies a fixed-width slot rather than being fitted to its current
    // text. A container that resizes as the digits change jitters constantly at 1 Hz,
    // which is exactly the kind of motion an ambient readout must not have. The notch
    // therefore changes width only when elements are toggled on or off.
    public const int NotchHeight = 26;
    public const int NotchPadX = 12;
    public const int NotchGap = 12;

    /// <summary>A labelled percentage, e.g. "CPU  97%".</summary>
    public const int NotchMetricSlot = 62;

    /// <summary>A bare throughput figure, e.g. "↓9.9M".</summary>
    public const int NotchNetSlot = 40;

    /// <summary>Label-to-value offset inside a metric slot.</summary>
    public const int NotchValueOffset = 30;

    public const int CornerRadiusMin = 0;
    public const int CornerRadiusMax = 16;
    public const int CornerRadiusDefault = 10;
}

/// <summary>
/// A colour scheme.
///
/// Surface alpha is per-pixel, not whole-window: the background fades while text stays at
/// full alpha, so lowering opacity never makes the numbers harder to read. Clear is simply
/// the bottom of that range -- surface alpha 0, text untouched.
/// </summary>
internal sealed class Theme
{
    public required ThemeId Id { get; init; }
    public required string Name { get; init; }

    /// <summary>Base opacity of the panel surface, 0-255, before the user's opacity setting.</summary>
    public required byte SurfaceAlpha { get; init; }

    /// <summary>Ask DWM for an acrylic blur behind the window. Silently ignored if unsupported.</summary>
    public bool Acrylic { get; init; }

    /// <summary>Draw a hairline border around the surface. Keeps Clear legible over busy wallpaper.</summary>
    public bool Border { get; init; }

    public required uint Background { get; init; }
    public required uint BorderColor { get; init; }
    public required uint Divider { get; init; }
    public required uint Track { get; init; }

    public required uint TextPrimary { get; init; }
    public required uint TextDim { get; init; }
    public required uint TextFaint { get; init; }

    public required uint Cpu { get; init; }
    public required uint Ram { get; init; }
    public required uint NetDown { get; init; }
    public required uint NetUp { get; init; }
    public required uint Disk { get; init; }
    public required uint Gpu { get; init; }

    public required uint Warn { get; init; }
    public required uint Hot { get; init; }

    /// <summary>Settings-panel chrome.</summary>
    public required uint ControlTrack { get; init; }
    public required uint ControlOn { get; init; }
    public required uint ControlKnob { get; init; }
    public required uint Hover { get; init; }

    public uint SeverityColor(double score) =>
        score >= 0.30 ? Hot : score >= 0.12 ? Warn : TextPrimary;

    // =====================================================================
    // palettes
    // =====================================================================

    /// <summary>
    /// Acrylic blur behind a mostly-transparent dark surface. Degrades to the same slate
    /// colour as a solid panel when the composition API is unavailable.
    /// </summary>
    public static readonly Theme Glass = new()
    {
        Id = ThemeId.Glass,
        Name = "Glass",
        SurfaceAlpha = 190,
        Acrylic = true,
        Border = true,
        Background = Win32.Rgb(0x14, 0x16, 0x1A),
        BorderColor = Win32.Rgb(0x3A, 0x41, 0x4B),
        Divider = Win32.Rgb(0x2C, 0x32, 0x3A),
        Track = Win32.Rgb(0x22, 0x26, 0x2C),
        TextPrimary = Win32.Rgb(0xF0, 0xF2, 0xF5),
        TextDim = Win32.Rgb(0x9A, 0xA1, 0xAB),
        TextFaint = Win32.Rgb(0x6C, 0x74, 0x7F),
        Cpu = Win32.Rgb(0x5C, 0xAD, 0xFF),
        Ram = Win32.Rgb(0xB1, 0x97, 0xFC),
        NetDown = Win32.Rgb(0x3D, 0xDD, 0xA3),
        NetUp = Win32.Rgb(0xFB, 0xA9, 0x1B),
        Disk = Win32.Rgb(0xF9, 0x7D, 0xBE),
        Gpu = Win32.Rgb(0x2E, 0xDC, 0xF5),
        Warn = Win32.Rgb(0xFB, 0xA9, 0x1B),
        Hot = Win32.Rgb(0xF4, 0x51, 0x51),
        ControlTrack = Win32.Rgb(0x2E, 0x34, 0x3D),
        ControlOn = Win32.Rgb(0x5C, 0xAD, 0xFF),
        ControlKnob = Win32.Rgb(0xF0, 0xF2, 0xF5),
        Hover = Win32.Rgb(0x25, 0x2B, 0x33),
    };

    /// <summary>
    /// True black. On an OLED panel these pixels are switched off, so the accents are
    /// pushed to high chroma to stay vivid against them.
    /// </summary>
    public static readonly Theme Amoled = new()
    {
        Id = ThemeId.Amoled,
        Name = "AMOLED",
        SurfaceAlpha = 255,
        Border = false,
        Background = Win32.Rgb(0x00, 0x00, 0x00),
        BorderColor = Win32.Rgb(0x1A, 0x1A, 0x1A),
        Divider = Win32.Rgb(0x1C, 0x1C, 0x1C),
        Track = Win32.Rgb(0x10, 0x10, 0x10),
        TextPrimary = Win32.Rgb(0xFF, 0xFF, 0xFF),
        TextDim = Win32.Rgb(0x8E, 0x8E, 0x93),
        TextFaint = Win32.Rgb(0x5A, 0x5A, 0x5E),
        Cpu = Win32.Rgb(0x0A, 0x84, 0xFF),
        Ram = Win32.Rgb(0xBF, 0x5A, 0xF2),
        NetDown = Win32.Rgb(0x30, 0xD1, 0x58),
        NetUp = Win32.Rgb(0xFF, 0x9F, 0x0A),
        Disk = Win32.Rgb(0xFF, 0x37, 0x5F),
        Gpu = Win32.Rgb(0x64, 0xD2, 0xFF),
        Warn = Win32.Rgb(0xFF, 0x9F, 0x0A),
        Hot = Win32.Rgb(0xFF, 0x45, 0x3A),
        ControlTrack = Win32.Rgb(0x24, 0x24, 0x26),
        ControlOn = Win32.Rgb(0x0A, 0x84, 0xFF),
        ControlKnob = Win32.Rgb(0xFF, 0xFF, 0xFF),
        Hover = Win32.Rgb(0x16, 0x16, 0x18),
    };

    /// <summary>
    /// Warm off-white. The dark themes' accents wash out badly on a light ground, so this
    /// is a genuinely different accent set -- darker and more saturated, not the same hues.
    /// </summary>
    public static readonly Theme Light = new()
    {
        Id = ThemeId.Light,
        Name = "Light",
        SurfaceAlpha = 240,
        Border = true,
        Background = Win32.Rgb(0xFA, 0xF9, 0xF7),
        BorderColor = Win32.Rgb(0xD8, 0xD4, 0xCE),
        Divider = Win32.Rgb(0xE4, 0xE0, 0xDA),
        Track = Win32.Rgb(0xEC, 0xE9, 0xE4),
        TextPrimary = Win32.Rgb(0x1C, 0x1B, 0x19),
        TextDim = Win32.Rgb(0x6B, 0x67, 0x61),
        TextFaint = Win32.Rgb(0x96, 0x91, 0x8A),
        Cpu = Win32.Rgb(0x1D, 0x63, 0xC4),
        Ram = Win32.Rgb(0x6D, 0x3C, 0xC9),
        NetDown = Win32.Rgb(0x0F, 0x7A, 0x52),
        NetUp = Win32.Rgb(0xB5, 0x6A, 0x02),
        Disk = Win32.Rgb(0xC0, 0x35, 0x7D),
        Gpu = Win32.Rgb(0x0B, 0x74, 0x92),
        Warn = Win32.Rgb(0xB5, 0x6A, 0x02),
        Hot = Win32.Rgb(0xC0, 0x28, 0x28),
        ControlTrack = Win32.Rgb(0xDD, 0xD9, 0xD3),
        ControlOn = Win32.Rgb(0x1D, 0x63, 0xC4),
        ControlKnob = Win32.Rgb(0xFF, 0xFF, 0xFF),
        Hover = Win32.Rgb(0xEE, 0xEB, 0xE6),
    };

    /// <summary>
    /// No surface at all -- readings float directly on the wallpaper. Text keeps full
    /// alpha, and a hairline border gives the eye an edge to latch onto. If the wallpaper
    /// is busy, raising the opacity slider fades a surface back in.
    /// </summary>
    public static readonly Theme Clear = new()
    {
        Id = ThemeId.Clear,
        Name = "Clear",
        SurfaceAlpha = 0,
        Border = true,
        Background = Win32.Rgb(0x00, 0x00, 0x00),
        BorderColor = Win32.Rgb(0x80, 0x88, 0x92),
        Divider = Win32.Rgb(0x70, 0x78, 0x82),
        Track = Win32.Rgb(0x3A, 0x3E, 0x44),
        TextPrimary = Win32.Rgb(0xFF, 0xFF, 0xFF),
        TextDim = Win32.Rgb(0xC8, 0xCD, 0xD4),
        TextFaint = Win32.Rgb(0xA2, 0xA9, 0xB2),
        Cpu = Win32.Rgb(0x7C, 0xC2, 0xFF),
        Ram = Win32.Rgb(0xC4, 0xAD, 0xFF),
        NetDown = Win32.Rgb(0x5A, 0xEF, 0xB8),
        NetUp = Win32.Rgb(0xFF, 0xC1, 0x4D),
        Disk = Win32.Rgb(0xFF, 0x9E, 0xD2),
        Gpu = Win32.Rgb(0x6A, 0xE8, 0xFF),
        Warn = Win32.Rgb(0xFF, 0xC1, 0x4D),
        Hot = Win32.Rgb(0xFF, 0x7B, 0x7B),
        ControlTrack = Win32.Rgb(0x50, 0x56, 0x5E),
        ControlOn = Win32.Rgb(0x7C, 0xC2, 0xFF),
        ControlKnob = Win32.Rgb(0xFF, 0xFF, 0xFF),
        Hover = Win32.Rgb(0x3A, 0x3E, 0x44),
    };

    public static readonly Theme[] All = [Glass, Amoled, Light, Clear];

    public static Theme ById(ThemeId id) =>
        All.FirstOrDefault(t => t.Id == id) ?? Glass;
}

using System.Globalization;

namespace Offender.Ui;

/// <summary>
/// A tiny fixed-capacity char buffer used for every label the panel draws.
///
/// The point is that painting must not allocate: string interpolation on a 1 Hz repaint
/// is a steady drip of gen0 garbage, which is exactly what makes lightweight monitors
/// stop looking lightweight after a few hours. Everything here writes into a reused
/// char[] via TryFormat and hands the renderer a span.
/// </summary>
internal struct TextBuf
{
    private readonly char[] _chars;
    private int _len;

    public TextBuf(char[] storage) { _chars = storage; _len = 0; }

    public readonly int Length => _len;
    public readonly ReadOnlySpan<char> Span => _chars.AsSpan(0, _len);
    public readonly char[] Array => _chars;

    public void Clear() => _len = 0;

    public void Append(char c)
    {
        if (_len < _chars.Length) _chars[_len++] = c;
    }

    public void Append(ReadOnlySpan<char> s)
    {
        int n = Math.Min(s.Length, _chars.Length - _len);
        if (n <= 0) return;
        s[..n].CopyTo(_chars.AsSpan(_len));
        _len += n;
    }

    public void Append(int value)
    {
        if (value.TryFormat(_chars.AsSpan(_len), out int written, default, CultureInfo.InvariantCulture))
            _len += written;
    }

    public void Append(double value, ReadOnlySpan<char> format)
    {
        if (value.TryFormat(_chars.AsSpan(_len), out int written, format, CultureInfo.InvariantCulture))
            _len += written;
    }

    /// <summary>
    /// Formats a byte count with a sensible number of significant digits:
    /// 3 digits below 10, 2 below 100, 0 above. Keeps column widths stable.
    /// </summary>
    public void AppendBytes(double bytes, bool space = true)
    {
        ReadOnlySpan<char> unit;
        double v = bytes;

        if (v >= 1024d * 1024 * 1024 * 1024) { v /= 1024d * 1024 * 1024 * 1024; unit = "TB"; }
        else if (v >= 1024d * 1024 * 1024) { v /= 1024d * 1024 * 1024; unit = "GB"; }
        else if (v >= 1024d * 1024) { v /= 1024d * 1024; unit = "MB"; }
        else if (v >= 1024d) { v /= 1024d; unit = "KB"; }
        else { unit = "B"; }

        Append(v, v < 10 ? "F2" : v < 100 ? "F1" : "F0");
        if (space) Append(' ');
        Append(unit);
    }

    /// <summary>Byte count per second, e.g. "4.20 MB/s".</summary>
    public void AppendRate(double bytesPerSecond)
    {
        AppendBytes(bytesPerSecond);
        Append("/s");
    }

    public void AppendPercent(double value, ReadOnlySpan<char> format)
    {
        Append(value, format);
        Append('%');
    }
}

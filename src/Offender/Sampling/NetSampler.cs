using System.Runtime.InteropServices;
using System.Text;
using Offender.Native;

namespace Offender.Sampling;

/// <summary>
/// Per-interface throughput.
///
/// Default behaviour is to report the single busiest operational interface rather than
/// the sum of all of them. Summing double-counts on any machine with Hyper-V, WSL or a
/// VPN, where a virtual adapter mirrors the physical one's traffic. The user can pin a
/// specific adapter by description substring in the config.
/// </summary>
internal sealed unsafe class NetSampler : IDisposable
{
    private byte* _buffer;
    private uint _bufferBytes;

    private readonly Dictionary<uint, Counters> _prev = new();
    private readonly Dictionary<uint, string> _names = new();
    private bool _primed;

    /// <summary>Case-insensitive substring of the adapter description; null selects the busiest.</summary>
    public string? PinnedInterface { get; set; }

    private struct Counters { public uint Rx; public uint Tx; }

    public NetSampler()
    {
        _bufferBytes = 16 * 1024;
        _buffer = (byte*)NativeMemory.Alloc(_bufferBytes);
    }

    public void Sample(Snapshot s, double elapsedSeconds)
    {
        if (elapsedSeconds <= 0) return;

        uint size = _bufferBytes;
        int rc = IpHlpApi.GetIfTable(_buffer, &size, 0);
        if (rc == IpHlpApi.ERROR_INSUFFICIENT_BUFFER)
        {
            NativeMemory.Free(_buffer);
            _bufferBytes = size + 4096;
            _buffer = (byte*)NativeMemory.Alloc(_bufferBytes);
            size = _bufferBytes;
            rc = IpHlpApi.GetIfTable(_buffer, &size, 0);
        }
        if (rc != IpHlpApi.NO_ERROR) return;

        uint rows = *(uint*)_buffer;
        var table = (IpHlpApi.MIB_IFROW*)(_buffer + IpHlpApi.IFTABLE_ROWS_OFFSET);

        double bestRx = 0, bestTx = 0, bestTotal = -1;
        string bestName = "";

        for (uint i = 0; i < rows; i++)
        {
            ref var row = ref table[i];

            if (row.dwOperStatus != IpHlpApi.IF_OPER_STATUS_OPERATIONAL) continue;
            if (row.dwType is IpHlpApi.IF_TYPE_SOFTWARE_LOOPBACK or IpHlpApi.IF_TYPE_TUNNEL) continue;

            uint idx = row.dwIndex;
            var now = new Counters { Rx = row.dwInOctets, Tx = row.dwOutOctets };

            double rx = 0, tx = 0;
            if (_prev.TryGetValue(idx, out var old))
            {
                // Unsigned subtraction makes a 32-bit counter wrap come out correct.
                rx = unchecked(now.Rx - old.Rx) / elapsedSeconds;
                tx = unchecked(now.Tx - old.Tx) / elapsedSeconds;
            }
            _prev[idx] = now;

            if (!_primed) continue;

            string name = DescriptionOf(idx, ref row);

            if (PinnedInterface is { Length: > 0 } pin)
            {
                if (name.Contains(pin, StringComparison.OrdinalIgnoreCase))
                {
                    bestRx = rx; bestTx = tx; bestName = name;
                    bestTotal = double.MaxValue; // pinned wins outright
                }
                continue;
            }

            double total = rx + tx;
            if (total > bestTotal)
            {
                bestTotal = total; bestRx = rx; bestTx = tx; bestName = name;
            }
        }

        if (_primed && bestTotal >= 0)
        {
            s.NetRx = bestRx;
            s.NetTx = bestTx;
            s.NetInterface = bestName;
        }
        _primed = true;
    }

    /// <summary>
    /// bDescr is an ANSI byte string. Cached per interface index so the steady-state
    /// loop never allocates a string.
    /// </summary>
    private string DescriptionOf(uint index, ref IpHlpApi.MIB_IFROW row)
    {
        if (_names.TryGetValue(index, out var cached)) return cached;

        int len = (int)Math.Min(row.dwDescrLen, (uint)IpHlpApi.MAXLEN_IFDESCR);
        string name;
        fixed (byte* p = row.bDescr)
        {
            while (len > 0 && p[len - 1] == 0) len--;
            name = len > 0 ? Encoding.Latin1.GetString(p, len) : "Network";
        }
        _names[index] = name;
        return name;
    }

    public void Dispose()
    {
        if (_buffer != null) { NativeMemory.Free(_buffer); _buffer = null; }
    }
}

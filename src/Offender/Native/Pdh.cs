using System.Runtime.InteropServices;

namespace Offender.Native;

/// <summary>
/// Minimal PDH wrapper for the counters that have no cheap native equivalent:
/// physical disk throughput and GPU engine / adapter memory.
///
/// Everything uses PdhAddEnglishCounterW so counter paths are not locale-dependent.
/// A path that fails to resolve (old GPU driver, VM with no GPU counters) is not an
/// error -- the counter is simply marked unavailable and its row is hidden.
/// </summary>
internal static unsafe partial class Pdh
{
    public const int PDH_CSTATUS_VALID_DATA = 0;
    public const int PDH_CSTATUS_NEW_DATA = 1;
    public const int PDH_MORE_DATA = unchecked((int)0x800007D2);
    public const int PDH_INVALID_DATA = unchecked((int)0xC0000BC6);
    public const int PDH_CALC_NEGATIVE_DENOMINATOR = unchecked((int)0x800007D8);
    public const int PDH_NO_DATA = unchecked((int)0x800007D5);

    public const uint PDH_FMT_DOUBLE = 0x00000200;
    public const uint PDH_FMT_LARGE = 0x00000400;
    public const uint PDH_FMT_NOCAP100 = 0x00008000;

    /// <summary>x64: CStatus at 0, 4 bytes of padding, 8-byte union at 8. 16 bytes total.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PDH_FMT_COUNTERVALUE
    {
        public uint CStatus;
        private readonly uint _padding;
        public double doubleValue; // union; read largeValue via BitConverter when needed
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PDH_FMT_COUNTERVALUE_ITEM_W
    {
        public nint szName;                 // LPWSTR into the same buffer
        public PDH_FMT_COUNTERVALUE Value;  // 24 bytes total
    }

    [LibraryImport("pdh.dll", EntryPoint = "PdhOpenQueryW")]
    public static partial int PdhOpenQueryW(char* szDataSource, nuint dwUserData, nint* phQuery);

    [LibraryImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW")]
    public static partial int PdhAddEnglishCounterW(nint hQuery, char* szFullCounterPath, nuint dwUserData, nint* phCounter);

    [LibraryImport("pdh.dll")]
    public static partial int PdhCollectQueryData(nint hQuery);

    [LibraryImport("pdh.dll")]
    public static partial int PdhGetFormattedCounterValue(nint hCounter, uint dwFormat, uint* lpdwType, PDH_FMT_COUNTERVALUE* pValue);

    [LibraryImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")]
    public static partial int PdhGetFormattedCounterArrayW(nint hCounter, uint dwFormat, uint* lpdwBufferSize, uint* lpdwItemCount, void* ItemBuffer);

    [LibraryImport("pdh.dll")]
    public static partial int PdhCloseQuery(nint hQuery);
}

/// <summary>
/// One PDH query holding every counter this app needs. Opened once at startup,
/// collected once per fast tick. Owns its own scratch buffer for wildcard reads so
/// the steady-state loop allocates nothing.
/// </summary>
internal sealed unsafe class PdhQuery : IDisposable
{
    private nint _query;
    private byte[] _scratch = new byte[8 * 1024];
    private bool _disposed;

    /// <summary>True once the query is open. False means every counter reads as unavailable.</summary>
    public bool Ok => _query != 0;

    public PdhQuery()
    {
        nint q;
        if (Pdh.PdhOpenQueryW(null, 0, &q) == 0) _query = q;
    }

    /// <summary>Adds a counter. Returns 0 if the path does not exist on this machine.</summary>
    public nint Add(string path)
    {
        if (_query == 0) return 0;
        fixed (char* p = path)
        {
            nint c;
            return Pdh.PdhAddEnglishCounterW(_query, p, 0, &c) == 0 ? c : 0;
        }
    }

    /// <summary>
    /// Refreshes every counter in the query. Must be called twice before rate counters
    /// return anything meaningful -- the first collect only establishes a baseline.
    /// </summary>
    public bool Collect() => _query != 0 && Pdh.PdhCollectQueryData(_query) == 0;

    public double Read(nint counter, double fallback = 0)
    {
        if (counter == 0) return fallback;
        Pdh.PDH_FMT_COUNTERVALUE v;
        int rc = Pdh.PdhGetFormattedCounterValue(counter, Pdh.PDH_FMT_DOUBLE | Pdh.PDH_FMT_NOCAP100, null, &v);
        if (rc != 0 || v.CStatus is not (Pdh.PDH_CSTATUS_VALID_DATA or Pdh.PDH_CSTATUS_NEW_DATA))
            return fallback;
        return v.doubleValue;
    }

    /// <summary>
    /// Sums every instance of a wildcard counter (e.g. all GPU engines, all adapters).
    /// Grows the scratch buffer on demand; in practice it settles after the first call.
    /// </summary>
    public double ReadWildcardSum(nint counter, double fallback = 0)
    {
        if (counter == 0) return fallback;

        for (int attempt = 0; attempt < 3; attempt++)
        {
            fixed (byte* buf = _scratch)
            {
                uint size = (uint)_scratch.Length;
                uint count = 0;
                int rc = Pdh.PdhGetFormattedCounterArrayW(
                    counter, Pdh.PDH_FMT_DOUBLE | Pdh.PDH_FMT_NOCAP100, &size, &count, buf);

                if (rc == Pdh.PDH_MORE_DATA)
                {
                    _scratch = new byte[Math.Max(size, (uint)_scratch.Length * 2)];
                    continue;
                }
                if (rc != 0 || count == 0) return fallback;

                var items = (Pdh.PDH_FMT_COUNTERVALUE_ITEM_W*)buf;
                double sum = 0;
                for (uint i = 0; i < count; i++)
                {
                    ref readonly var it = ref items[i];
                    if (it.Value.CStatus is Pdh.PDH_CSTATUS_VALID_DATA or Pdh.PDH_CSTATUS_NEW_DATA)
                        sum += it.Value.doubleValue;
                }
                return sum;
            }
        }
        return fallback;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_query != 0) { Pdh.PdhCloseQuery(_query); _query = 0; }
    }
}

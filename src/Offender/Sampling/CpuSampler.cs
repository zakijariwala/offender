using Offender.Native;

namespace Offender.Sampling;

/// <summary>
/// Total and per-core CPU load from SystemProcessorPerformanceInformation.
///
/// Note that KernelTime already includes IdleTime, so busy time is
/// (Kernel + User) - Idle, and the denominator is Kernel + User.
/// </summary>
internal sealed unsafe class CpuSampler : IDisposable
{
    private readonly int _cores;
    private readonly byte* _buffer;
    private readonly uint _bufferBytes;

    private readonly long[] _prevIdle;
    private readonly long[] _prevBusy;
    private bool _primed;

    public CpuSampler(int cores)
    {
        _cores = cores;
        _bufferBytes = (uint)(sizeof(NtDll.SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION) * cores);
        _buffer = (byte*)System.Runtime.InteropServices.NativeMemory.Alloc(_bufferBytes);
        _prevIdle = new long[cores];
        _prevBusy = new long[cores];
    }

    /// <summary>Fills snapshot.CpuPercent and CpuPerCore. Silent no-op if the query fails.</summary>
    public void Sample(Snapshot s)
    {
        uint returned;
        if (NtDll.NtQuerySystemInformation(
                NtDll.SystemProcessorPerformanceInformation, _buffer, _bufferBytes, &returned) != NtDll.STATUS_SUCCESS)
            return;

        var info = (NtDll.SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION*)_buffer;
        int n = Math.Min(_cores, (int)(returned / sizeof(NtDll.SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION)));

        long totalIdleDelta = 0, totalBusyDelta = 0;

        for (int i = 0; i < n; i++)
        {
            long idle = info[i].IdleTime;
            long busy = info[i].KernelTime + info[i].UserTime; // Kernel includes Idle

            long dIdle = idle - _prevIdle[i];
            long dBusy = busy - _prevBusy[i];
            _prevIdle[i] = idle;
            _prevBusy[i] = busy;

            if (_primed && dBusy > 0)
            {
                double used = (dBusy - dIdle) * 100.0 / dBusy;
                s.CpuPerCore[i] = used < 0 ? 0 : used > 100 ? 100 : used;
                totalIdleDelta += dIdle;
                totalBusyDelta += dBusy;
            }
        }

        if (_primed && totalBusyDelta > 0)
        {
            double total = (totalBusyDelta - totalIdleDelta) * 100.0 / totalBusyDelta;
            s.CpuPercent = total < 0 ? 0 : total > 100 ? 100 : total;
        }

        _primed = true;
    }

    public void Dispose() => System.Runtime.InteropServices.NativeMemory.Free(_buffer);
}

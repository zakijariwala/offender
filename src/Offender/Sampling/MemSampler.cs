using Offender.Native;

namespace Offender.Sampling;

/// <summary>Physical memory in use plus commit charge (the number that actually predicts paging).</summary>
internal sealed unsafe class MemSampler
{
    public void Sample(Snapshot s)
    {
        MemApi.MEMORYSTATUSEX ms = default;
        ms.dwLength = (uint)sizeof(MemApi.MEMORYSTATUSEX);
        if (MemApi.GlobalMemoryStatusEx(&ms) != 0)
        {
            s.MemTotal = ms.ullTotalPhys;
            s.MemAvail = ms.ullAvailPhys;
            s.MemUsed = ms.ullTotalPhys - ms.ullAvailPhys;
        }

        MemApi.PERFORMANCE_INFORMATION pi = default;
        pi.cb = (uint)sizeof(MemApi.PERFORMANCE_INFORMATION);
        if (MemApi.GetPerformanceInfo(&pi, pi.cb) != 0)
        {
            ulong page = pi.PageSize;
            s.CommitUsed = (ulong)pi.CommitTotal * page;
            s.CommitLimit = (ulong)pi.CommitLimit * page;
        }
    }
}

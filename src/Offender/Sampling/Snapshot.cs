namespace Offender.Sampling;

/// <summary>One process family (all PIDs sharing an image name) in the offenders list.</summary>
internal struct ProcRow
{
    public string Name;
    public double Score;        // composite 0..1, EMA-smoothed
    public double CpuPercent;   // 0..100 of total machine capacity
    public ulong PrivateBytes;
    public double IoBytesPerSec;
    public int Pid;             // representative PID (the heaviest member)
    public int InstanceCount;
}

/// <summary>
/// A complete reading of the machine. Two instances exist and are swapped -- the sampler
/// fills the spare while the UI paints the published one, so no allocation happens per tick.
/// Fields are plain and mutable by design; only the sampler thread writes them, and only
/// before the instance is published.
/// </summary>
internal sealed class Snapshot
{
    public const int MaxProcRows = 5;

    public long Timestamp;             // Stopwatch ticks when this reading completed

    // CPU
    public double CpuPercent;
    public double[] CpuPerCore;

    // Memory
    public ulong MemTotal;
    public ulong MemAvail;
    public ulong MemUsed;
    public ulong CommitUsed;
    public ulong CommitLimit;

    // Network (bytes/sec on the selected or busiest interface)
    public double NetRx;
    public double NetTx;
    public string NetInterface = "";

    // Disk
    public bool DiskOk;
    public double DiskRead;
    public double DiskWrite;
    public double DiskActivePercent;

    // GPU
    public bool GpuOk;
    public double GpuPercent;
    public bool VramOk;
    public double VramUsed;      // bytes; dedicated adapter memory in use

    // Top offenders
    public readonly ProcRow[] Procs = new ProcRow[MaxProcRows];
    public int ProcCount;

    public Snapshot(int coreCount)
    {
        CpuPerCore = new double[coreCount];
    }
}

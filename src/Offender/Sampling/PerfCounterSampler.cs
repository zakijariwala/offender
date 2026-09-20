using Offender.Native;

namespace Offender.Sampling;

/// <summary>
/// Disk throughput and GPU utilisation / VRAM, all from one PDH query.
///
/// These are the only metrics without a cheap direct native API. Each counter path is
/// optional: on a VM or an old driver the GPU paths simply fail to resolve, the
/// corresponding Ok flag stays false, and the UI hides that row instead of showing zeros.
/// </summary>
internal sealed class PerfCounterSampler : IDisposable
{
    /// <summary>
    /// How often to tear down and rebuild the query.
    ///
    /// The GPU counters are wildcards with one instance per process-engine pair, and PDH
    /// retains state for instances it has seen. On a machine that churns through
    /// processes -- a build server, or anything doing repeated short-lived work -- that
    /// retention grows without bound: measured at roughly +100 kernel handles and +11 MB
    /// over one working session. Rebuilding the query periodically drops the accumulated
    /// instance state; the cost is one extra priming tick.
    /// </summary>
    private const int RebuildAfterTicks = 600;   // ~10 minutes at the default interval

    private PdhQuery _query = new();

    private nint _diskRead;
    private nint _diskWrite;
    private nint _diskTime;
    private nint _gpuUtil;
    private nint _vramDedicated;
    private nint _vramShared;

    private bool _primed;
    private int _ticks;
    private bool _gpuEnabled = true;

    /// <summary>
    /// Whether to sample the GPU at all. The wildcard counters are by far the most
    /// expensive thing this app does, so a user who has hidden every GPU readout should
    /// not be paying for them.
    /// </summary>
    public bool GpuEnabled
    {
        get => _gpuEnabled;
        set
        {
            if (_gpuEnabled == value) return;
            _gpuEnabled = value;
            Rebuild();
        }
    }

    public PerfCounterSampler() => Build();

    private void Build()
    {
        _diskRead = _query.Add(@"\PhysicalDisk(_Total)\Disk Read Bytes/sec");
        _diskWrite = _query.Add(@"\PhysicalDisk(_Total)\Disk Write Bytes/sec");
        _diskTime = _query.Add(@"\PhysicalDisk(_Total)\% Disk Time");

        if (_gpuEnabled)
        {
            // Wildcards: one instance per GPU engine (3D, Copy, VideoDecode, ...) per
            // process, and one per adapter.
            _gpuUtil = _query.Add(@"\GPU Engine(*)\Utilization Percentage");

            // Dedicated usage is zero on an integrated GPU, which has no VRAM of its own
            // -- there the number that means anything is what it has carved out of RAM.
            _vramDedicated = _query.Add(@"\GPU Adapter Memory(*)\Dedicated Usage");
            _vramShared = _query.Add(@"\GPU Adapter Memory(*)\Shared Usage");
        }
        else
        {
            _gpuUtil = _vramDedicated = _vramShared = 0;
        }

        // First collect establishes the baseline for the rate counters.
        _query.Collect();
        _primed = false;
        _ticks = 0;
    }

    private void Rebuild()
    {
        _query.Dispose();
        _query = new PdhQuery();
        Build();
    }

    public void Sample(Snapshot s)
    {
        if (_gpuEnabled && ++_ticks >= RebuildAfterTicks) Rebuild();

        if (!_query.Collect()) return;

        // A single collect is not enough for a rate counter to produce a value.
        if (!_primed) { _primed = true; return; }

        if (_diskRead != 0 || _diskWrite != 0)
        {
            s.DiskOk = true;
            s.DiskRead = _query.Read(_diskRead);
            s.DiskWrite = _query.Read(_diskWrite);
            // % Disk Time is really a queue-time counter and routinely exceeds 100 on
            // busy multi-queue NVMe. Clamp it so the bar stays meaningful.
            double busy = _query.Read(_diskTime);
            s.DiskActivePercent = busy < 0 ? 0 : busy > 100 ? 100 : busy;
        }

        if (!_gpuEnabled) { s.GpuOk = false; s.VramOk = false; return; }

        if (_gpuUtil != 0)
        {
            double util = _query.ReadWildcardSum(_gpuUtil, -1);
            if (util >= 0)
            {
                s.GpuOk = true;
                // Engines are summed, so a machine with several engines busy can exceed 100.
                s.GpuPercent = util > 100 ? 100 : util;
            }
        }

        double vram = _query.ReadWildcardSum(_vramDedicated, 0);
        if (vram <= 0) vram = _query.ReadWildcardSum(_vramShared, 0);

        // A zero here means neither counter resolved to anything real, so show nothing
        // rather than a confident "0.00 B".
        s.VramOk = vram > 0;
        s.VramUsed = vram;
    }

    public void Dispose() => _query.Dispose();
}

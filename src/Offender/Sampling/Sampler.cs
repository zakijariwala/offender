using System.Diagnostics;

namespace Offender.Sampling;

/// <summary>
/// The single background thread that drives every sampler.
///
/// Two tiers: the fast tier (CPU, memory, network, disk, GPU) runs every interval; the
/// process table -- by far the most expensive read -- runs at a multiple of it. When the
/// panel is hidden the whole loop slows down, since only the tray icon still needs data.
///
/// Results are published into one of two Snapshot instances, alternated, so the UI thread
/// always has a stable object to paint and the loop never allocates.
/// </summary>
internal sealed class Sampler : IDisposable
{
    private readonly int _cores = Environment.ProcessorCount;

    private readonly CpuSampler _cpu;
    private readonly MemSampler _mem = new();
    private readonly NetSampler _net = new();
    private readonly PerfCounterSampler _perf = new();
    private readonly ProcessSampler _proc;

    private readonly Snapshot _bufferA;
    private readonly Snapshot _bufferB;
    private Snapshot? _published;
    private bool _useA = true;

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _stop = new(false);
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    /// <summary>Raised on the sampler thread once a new snapshot is published.</summary>
    public Action? OnSnapshot;

    /// <summary>Fast-tier period in milliseconds.</summary>
    public volatile int IntervalMs = 1000;

    /// <summary>Process table is sampled every Nth fast tick.</summary>
    public volatile int ProcessEveryNTicks = 2;

    /// <summary>When true the loop halves its rate and stops reading the process table.</summary>
    public volatile bool Idle;

    /// <summary>
    /// Whether to sample the GPU. Applied by the sampler thread at the top of its loop
    /// rather than written straight through: enabling it rebuilds the PDH query, which
    /// must not happen underneath an in-flight collect.
    /// </summary>
    public volatile bool GpuWanted = true;

    public string? PinnedInterface { get => _net.PinnedInterface; set => _net.PinnedInterface = value; }

    public Sampler()
    {
        _cpu = new CpuSampler(_cores);
        _proc = new ProcessSampler(_cores);
        _bufferA = new Snapshot(_cores);
        _bufferB = new Snapshot(_cores);

        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "monitor-sampler",
            Priority = ThreadPriority.BelowNormal,
        };
    }

    public void Start() => _thread.Start();

    /// <summary>The most recent published reading, or null before the first one lands.</summary>
    public Snapshot? Current => Volatile.Read(ref _published);

    private void Run()
    {
        long lastFast = _clock.ElapsedTicks;
        long lastProc = lastFast;
        int tick = 0;

        // Prime every sampler once so the first displayed values are real deltas.
        Warmup();

        while (!_stop.IsSet)
        {
            int interval = Math.Max(250, IntervalMs) * (Idle ? 2 : 1);
            if (_stop.Wait(interval)) break;

            // Owned by this thread, so a rebuild can never land mid-collect.
            _perf.GpuEnabled = GpuWanted;

            long now = _clock.ElapsedTicks;
            double fastElapsed = (now - lastFast) / (double)Stopwatch.Frequency;
            lastFast = now;

            var s = _useA ? _bufferA : _bufferB;
            var prev = _published;
            _useA = !_useA;

            s.Timestamp = now;
            _cpu.Sample(s);
            _mem.Sample(s);
            _net.Sample(s, fastElapsed);
            _perf.Sample(s);

            tick++;
            bool doProcesses = !Idle && tick % Math.Max(1, ProcessEveryNTicks) == 0;
            if (doProcesses)
            {
                double procElapsed = (now - lastProc) / (double)Stopwatch.Frequency;
                lastProc = now;
                _proc.Sample(s, procElapsed, s.MemTotal);
            }
            else
            {
                CarryProcessRows(prev, s);
            }

            Volatile.Write(ref _published, s);
            OnSnapshot?.Invoke();
        }
    }

    private void Warmup()
    {
        var s = _bufferA;
        _cpu.Sample(s);
        _mem.Sample(s);
        _net.Sample(s, 1);
        _proc.Sample(s, 1, s.MemTotal);
    }

    /// <summary>
    /// The spare buffer still holds process rows from two ticks ago, so on a fast-only
    /// tick the current rows are copied forward rather than going blank.
    /// </summary>
    private static void CarryProcessRows(Snapshot? from, Snapshot to)
    {
        if (from is null) { to.ProcCount = 0; return; }
        to.ProcCount = from.ProcCount;
        for (int i = 0; i < from.ProcCount; i++) to.Procs[i] = from.Procs[i];
    }

    public void Dispose()
    {
        _stop.Set();
        if (_thread.IsAlive) _thread.Join(2000);
        _cpu.Dispose();
        _net.Dispose();
        _perf.Dispose();
        _proc.Dispose();
        _stop.Dispose();
    }
}

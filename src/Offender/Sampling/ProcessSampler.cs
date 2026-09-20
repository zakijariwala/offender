using System.Runtime.InteropServices;
using Offender.Native;

namespace Offender.Sampling;

/// <summary>
/// The "what is slowing this machine down" list.
///
/// One NtQuerySystemInformation call returns the entire process table already populated
/// with CPU times, private bytes and I/O transfer counts. Entries are aggregated by image
/// name so a browser's forty helper processes show up as one row, then ranked by a
/// composite score and smoothed so the list does not reshuffle every tick.
/// </summary>
internal sealed unsafe class ProcessSampler : IDisposable
{
    // Score weights. CPU dominates because it is what users actually feel; memory
    // pressure matters more slowly; I/O captures the "disk is thrashing" case.
    private const double WeightCpu = 0.50;
    private const double WeightMem = 0.30;
    private const double WeightIo = 0.20;

    // Smoothing factor for the displayed score. Lower = steadier list.
    private const double EmaAlpha = 0.45;

    // An aggregate not seen for this many ticks is forgotten.
    private const int ForgetAfterTicks = 8;

    private readonly int _cores;

    private byte* _buffer;
    private uint _bufferBytes;

    private struct PrevProc
    {
        public long CpuTicks;
        public long IoBytes;
        public long CreateTime;
        public string Name;
        public int SeenTick;
    }

    private struct Agg
    {
        public double CpuTicksDelta;
        public double IoBytesDelta;
        public ulong PrivateBytes;
        public int Instances;
        public int HeaviestPid;
        public double HeaviestCpu;
    }

    private readonly Dictionary<int, PrevProc> _prev = new(512);
    private readonly Dictionary<string, Agg> _agg = new(256);
    private readonly Dictionary<string, double> _ema = new(256);
    private readonly Dictionary<string, int> _lastSeen = new(256);
    private readonly List<int> _deadPids = new(64);
    private readonly List<string> _staleNames = new(64);

    private int _tick;
    private bool _primed;

    public ProcessSampler(int cores)
    {
        _cores = cores;
        _bufferBytes = 512 * 1024;
        _buffer = (byte*)NativeMemory.Alloc(_bufferBytes);
    }

    public void Sample(Snapshot s, double elapsedSeconds, ulong totalPhysical)
    {
        if (elapsedSeconds <= 0 || !Query()) return;

        _tick++;
        _agg.Clear();

        double maxIo = 0;
        var entry = (NtDll.SYSTEM_PROCESS_INFORMATION*)_buffer;

        while (true)
        {
            int pid = (int)entry->UniqueProcessId;

            // PID 0 is the idle process; its "CPU time" is the inverse of machine load.
            if (pid != 0)
            {
                long cpuTicks = entry->UserTime + entry->KernelTime;
                long ioBytes = entry->ReadTransferCount + entry->WriteTransferCount;
                long created = entry->CreateTime;

                bool known = _prev.TryGetValue(pid, out var old) && old.CreateTime == created;

                // A recycled PID looks like a huge counter jump, so only trust a delta
                // when the create time also matches.
                double dCpu = known ? Math.Max(0, cpuTicks - old.CpuTicks) : 0;
                double dIo = known ? Math.Max(0, ioBytes - old.IoBytes) : 0;

                string name = known ? old.Name : ReadImageName(pid, entry);

                _prev[pid] = new PrevProc
                {
                    CpuTicks = cpuTicks,
                    IoBytes = ioBytes,
                    CreateTime = created,
                    Name = name,
                    SeenTick = _tick,
                };

                if (_primed)
                {
                    _agg.TryGetValue(name, out var a);
                    a.CpuTicksDelta += dCpu;
                    a.IoBytesDelta += dIo;
                    a.PrivateBytes += (ulong)entry->PrivatePageCount;
                    a.Instances++;
                    if (a.HeaviestPid == 0 || dCpu > a.HeaviestCpu) { a.HeaviestCpu = dCpu; a.HeaviestPid = pid; }
                    _agg[name] = a;

                    if (a.IoBytesDelta > maxIo) maxIo = a.IoBytesDelta;
                    _lastSeen[name] = _tick;
                }
            }

            if (entry->NextEntryOffset == 0) break;
            entry = (NtDll.SYSTEM_PROCESS_INFORMATION*)((byte*)entry + entry->NextEntryOffset);
        }

        PruneExitedProcesses();

        if (!_primed) { _primed = true; return; }

        // 100 ns ticks available across the whole machine during this interval.
        double capacityTicks = elapsedSeconds * 1e7 * _cores;
        double physical = totalPhysical > 0 ? totalPhysical : 1;
        if (maxIo <= 0) maxIo = 1;

        s.ProcCount = 0;
        foreach (var kv in _agg)
        {
            var a = kv.Value;
            double cpuFrac = capacityTicks > 0 ? a.CpuTicksDelta / capacityTicks : 0;
            double memFrac = a.PrivateBytes / physical;
            double ioFrac = a.IoBytesDelta / maxIo;

            double raw = WeightCpu * cpuFrac + WeightMem * memFrac + WeightIo * ioFrac;

            _ema.TryGetValue(kv.Key, out double prev);
            double score = prev + EmaAlpha * (raw - prev);
            _ema[kv.Key] = score;

            InsertTop(s, new ProcRow
            {
                Name = kv.Key,
                Score = score,
                CpuPercent = cpuFrac * 100.0,
                PrivateBytes = a.PrivateBytes,
                IoBytesPerSec = a.IoBytesDelta / elapsedSeconds,
                Pid = a.HeaviestPid,
                InstanceCount = a.Instances,
            });
        }

        PruneStaleAggregates();
    }

    /// <summary>Keeps snapshot.Procs sorted descending by score, capped at MaxProcRows.</summary>
    private static void InsertTop(Snapshot s, in ProcRow row)
    {
        int count = s.ProcCount;
        int pos = count;
        while (pos > 0 && s.Procs[pos - 1].Score < row.Score) pos--;

        if (pos >= Snapshot.MaxProcRows) return;

        int last = Math.Min(count, Snapshot.MaxProcRows - 1);
        for (int i = last; i > pos; i--) s.Procs[i] = s.Procs[i - 1];

        s.Procs[pos] = row;
        s.ProcCount = Math.Min(count + 1, Snapshot.MaxProcRows);
    }

    private bool Query()
    {
        for (int attempt = 0; attempt < 6; attempt++)
        {
            uint returned = 0;
            int status = NtDll.NtQuerySystemInformation(
                NtDll.SystemProcessInformation, _buffer, _bufferBytes, &returned);

            if (status == NtDll.STATUS_SUCCESS) return true;
            if (status != NtDll.STATUS_INFO_LENGTH_MISMATCH) return false;

            // The table can grow between the size probe and the read, so add headroom.
            uint wanted = Math.Max(returned + 64 * 1024, _bufferBytes * 2);
            NativeMemory.Free(_buffer);
            _bufferBytes = wanted;
            _buffer = (byte*)NativeMemory.Alloc(_bufferBytes);
        }
        return false;
    }

    /// <summary>
    /// ImageName is a counted UNICODE_STRING pointing into the same buffer. This allocates,
    /// but only for a process seen for the first time -- steady state hits the cache in _prev.
    /// </summary>
    private static string ReadImageName(int pid, NtDll.SYSTEM_PROCESS_INFORMATION* entry)
    {
        var img = entry->ImageName;
        if (img.Buffer != 0 && img.Length > 0)
            return new string((char*)img.Buffer, 0, img.Length / 2);

        // Only the kernel-side pseudo-processes have no image name.
        return pid == 4 ? "System" : "(unknown)";
    }

    /// <summary>Drops processes that did not appear in this pass, so _prev cannot grow forever.</summary>
    private void PruneExitedProcesses()
    {
        _deadPids.Clear();
        foreach (var kv in _prev)
            if (kv.Value.SeenTick != _tick) _deadPids.Add(kv.Key);

        foreach (int pid in _deadPids) _prev.Remove(pid);
    }

    private void PruneStaleAggregates()
    {
        _staleNames.Clear();
        foreach (var kv in _lastSeen)
            if (_tick - kv.Value > ForgetAfterTicks) _staleNames.Add(kv.Key);

        foreach (var name in _staleNames)
        {
            _lastSeen.Remove(name);
            _ema.Remove(name);
        }
    }

    public void Dispose()
    {
        if (_buffer != null) { NativeMemory.Free(_buffer); _buffer = null; }
    }
}

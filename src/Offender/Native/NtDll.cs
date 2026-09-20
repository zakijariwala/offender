using System.Runtime.InteropServices;

namespace Offender.Native;

/// <summary>
/// The two NtQuerySystemInformation classes this tool lives on.
///
/// Both are undocumented-but-stable ABIs that every task manager on Windows uses.
/// The reason we go here instead of PDH or System.Diagnostics.Process: one syscall
/// returns the whole process table with CPU times, memory and I/O counters already
/// filled in. Enumerating Process objects would open a handle per process and cost
/// more than every other sampler in this app combined.
/// </summary>
internal static unsafe partial class NtDll
{
    public const uint SystemProcessorPerformanceInformation = 8;
    public const uint SystemProcessInformation = 5;

    public const int STATUS_SUCCESS = 0;
    public const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);

    [LibraryImport("ntdll.dll")]
    public static partial int NtQuerySystemInformation(
        uint systemInformationClass, void* systemInformation,
        uint systemInformationLength, uint* returnLength);

    /// <summary>One per logical processor. Times are in 100 ns units.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION
    {
        public long IdleTime;
        public long KernelTime;   // includes IdleTime
        public long UserTime;
        public long DpcTime;
        public long InterruptTime;
        public uint InterruptCount;
        private readonly uint _padding;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct UNICODE_STRING
    {
        public ushort Length;          // in bytes, not chars
        public ushort MaximumLength;
        public nint Buffer;
    }

    /// <summary>
    /// x64 layout of SYSTEM_PROCESS_INFORMATION, 256 bytes. Field order and the
    /// implied padding must match exactly -- one wrong field shifts everything after it.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_PROCESS_INFORMATION
    {
        public uint NextEntryOffset;              // 0
        public uint NumberOfThreads;              // 4
        public long WorkingSetPrivateSize;        // 8
        public uint HardFaultCount;               // 16
        public uint NumberOfThreadsHighWatermark; // 20
        public ulong CycleTime;                   // 24
        public long CreateTime;                   // 32
        public long UserTime;                     // 40
        public long KernelTime;                   // 48
        public UNICODE_STRING ImageName;          // 56
        public int BasePriority;                  // 72
        public nint UniqueProcessId;              // 80
        public nint InheritedFromUniqueProcessId; // 88
        public uint HandleCount;                  // 96
        public uint SessionId;                    // 100
        public nuint UniqueProcessKey;            // 104
        public nuint PeakVirtualSize;             // 112
        public nuint VirtualSize;                 // 120
        public uint PageFaultCount;               // 128
        public nuint PeakWorkingSetSize;          // 136
        public nuint WorkingSetSize;              // 144
        public nuint QuotaPeakPagedPoolUsage;     // 152
        public nuint QuotaPagedPoolUsage;         // 160
        public nuint QuotaPeakNonPagedPoolUsage;  // 168
        public nuint QuotaNonPagedPoolUsage;      // 176
        public nuint PagefileUsage;               // 184
        public nuint PeakPagefileUsage;           // 192
        public nuint PrivatePageCount;            // 200
        public long ReadOperationCount;           // 208
        public long WriteOperationCount;          // 216
        public long OtherOperationCount;          // 224
        public long ReadTransferCount;            // 232
        public long WriteTransferCount;           // 240
        public long OtherTransferCount;           // 248
    }
}

using System.Runtime.InteropServices;

namespace Offender.Native;

/// <summary>Physical and commit memory figures. Both calls are essentially free.</summary>
internal static unsafe partial class MemApi
{
    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    /// <summary>PERFORMANCE_INFORMATION. Counts are in pages, not bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PERFORMANCE_INFORMATION
    {
        public uint cb;
        public nuint CommitTotal;
        public nuint CommitLimit;
        public nuint CommitPeak;
        public nuint PhysicalTotal;
        public nuint PhysicalAvailable;
        public nuint SystemCache;
        public nuint KernelTotal;
        public nuint KernelPaged;
        public nuint KernelNonpaged;
        public nuint PageSize;
        public uint HandleCount;
        public uint ProcessCount;
        public uint ThreadCount;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GlobalMemoryStatusEx")]
    public static partial int GlobalMemoryStatusEx(MEMORYSTATUSEX* lpBuffer);

    [LibraryImport("psapi.dll", EntryPoint = "GetPerformanceInfo")]
    public static partial int GetPerformanceInfo(PERFORMANCE_INFORMATION* pPerformanceInformation, uint cb);
}

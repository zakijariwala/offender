using System.Runtime.InteropServices;

namespace Offender.Native;

/// <summary>
/// Per-interface byte counters via GetIfTable / MIB_IFROW.
///
/// GetIfTable2 offers 64-bit counters, but MIB_IF_ROW2 is a ~1350 byte struct with
/// several enum-alignment traps that are easy to get silently wrong. MIB_IFROW is all
/// DWORDs -- no padding ambiguity -- and its 32-bit octet counters are still exact here:
/// we sample at most once a second and take the delta in unsigned 32-bit arithmetic, so
/// a wrap is handled correctly as long as an interface moves under 4 GiB per second
/// (~34 Gbps). Speed display is capped at 4.29 Gbps by dwSpeed, which is cosmetic only.
/// </summary>
internal static unsafe partial class IpHlpApi
{
    public const int NO_ERROR = 0;
    public const int ERROR_INSUFFICIENT_BUFFER = 122;

    public const uint IF_TYPE_SOFTWARE_LOOPBACK = 24;
    public const uint IF_TYPE_TUNNEL = 131;
    public const uint IF_OPER_STATUS_OPERATIONAL = 5;

    public const int MAX_INTERFACE_NAME_LEN = 256;
    public const int MAXLEN_PHYSADDR = 8;
    public const int MAXLEN_IFDESCR = 256;

    /// <summary>860 bytes, entirely DWORD/byte fields.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MIB_IFROW
    {
        public fixed char wszName[MAX_INTERFACE_NAME_LEN]; //   0 .. 512
        public uint dwIndex;                               // 512
        public uint dwType;                                // 516
        public uint dwMtu;                                 // 520
        public uint dwSpeed;                               // 524
        public uint dwPhysAddrLen;                         // 528
        public fixed byte bPhysAddr[MAXLEN_PHYSADDR];      // 532
        public uint dwAdminStatus;                         // 540
        public uint dwOperStatus;                          // 544
        public uint dwLastChange;                          // 548
        public uint dwInOctets;                            // 552
        public uint dwInUcastPkts;                         // 556
        public uint dwInNUcastPkts;                        // 560
        public uint dwInDiscards;                          // 564
        public uint dwInErrors;                            // 568
        public uint dwInUnknownProtos;                     // 572
        public uint dwOutOctets;                           // 576
        public uint dwOutUcastPkts;                        // 580
        public uint dwOutNUcastPkts;                       // 584
        public uint dwOutDiscards;                         // 588
        public uint dwOutErrors;                           // 592
        public uint dwOutQLen;                             // 596
        public uint dwDescrLen;                            // 600
        public fixed byte bDescr[MAXLEN_IFDESCR];          // 604 .. 860
    }

    /// <summary>
    /// MIB_IFTABLE: DWORD count followed by the rows. All-DWORD alignment puts the
    /// first row at offset 4.
    /// </summary>
    public const int IFTABLE_ROWS_OFFSET = 4;

    [LibraryImport("iphlpapi.dll")]
    public static partial int GetIfTable(void* pIfTable, uint* pdwSize, int bOrder);
}

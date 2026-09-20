# Architecture

Why Offender is built the way it is, and where the non-obvious decisions are.

---

## Constraints that shaped everything

The target was to beat [NetSpeedTray](https://github.com/erez-c137/NetSpeedTray) —
Python + PyQt6, ~50 MB idle — on footprint while doing strictly more.

That budget rules out most of the obvious choices:

- **Python is out.** The CPython interpreter alone costs ~20 MB before a window exists.
- **WinForms and WPF are out.** Tens of megabytes of UI framework for a few hundred pixels
  of text and lines.
- **`System.Drawing` is out.** GDI+ brings its own initialisation cost for drawing we can
  do directly.

What is left is C# talking straight to Win32, compiled with NativeAOT. Concretely that
means no .NET runtime dependency in the shipping build, no UI framework, no JSON
serializer (settings are hand-rolled `key=value`), and no `System.Diagnostics.Process`.

A measured breakdown of where the working set actually goes, bucketed by module with
`QueryWorkingSet`:

| Bucket | Resident |
|---|---|
| Private — GC heap, JIT'd code, DIBs | 13.5 MB |
| .NET runtime (`System.Private.CoreLib`, `coreclr`, `clrjit`) | 7.3 MB |
| Shell / COM / UI (`windows.storage`, `combase`, `MSCTF`, …) | ~7 MB |
| OS core (`ntdll`, `KERNELBASE`, `gdi32full`, `RPCRT4`) | ~3 MB |

The DIB surfaces — the thing one would assume dominates a drawing app — are **57 KB** in
notch state. The runtime is the cost, which is why NativeAOT is the lever that matters.

---

## Layout

```
src/Offender/
  Program.cs              entry point, single-instance mutex, --make-shortcut
  Config.cs               INI load/save
  Startup.cs              HKCU Run entry, Start Menu shortcut

  Native/                 interop only; no logic
    Win32.cs              user32 / gdi32 / kernel32 / dwmapi / shell32
    NtDll.cs              NtQuerySystemInformation + struct layouts
    IpHlpApi.cs           GetIfTable / MIB_IFROW
    Pdh.cs                PDH interop + PdhQuery wrapper
    MemApi.cs             GlobalMemoryStatusEx, GetPerformanceInfo
    ShellLink.cs          IShellLinkW / IPersistFile (source-generated COM)

  Sampling/               all measurement; no UI types
    Sampler.cs            the background thread, tier scheduling, publication
    Snapshot.cs           one complete reading
    CpuSampler.cs  MemSampler.cs  NetSampler.cs
    PerfCounterSampler.cs disk + GPU via PDH
    ProcessSampler.cs     process table and the offender score

  Ui/                     all drawing and windowing
    PanelWindow.cs        main window, notch/panel state machine, input
    Renderer.cs           panel + notch painting, history ring buffers
    SettingsWindow.cs     custom-drawn settings, column packing
    TaskbarHost.cs        taskbar readout
    TrayIcon.cs           dynamically rendered tray icon
    GdiCanvas.cs          shared double-buffered drawing surface
    Theme.cs              palettes and layout metrics
    TextBuf.cs            allocation-free text formatting
    RingBuffer.cs         sparkline history
```

`Native` has no logic, `Sampling` has no UI types, and `Ui` does no measurement. The only
thing crossing between them is `Snapshot`.

---

## Threading

Two threads.

**The sampler thread** runs a loop on a cancellable wait. Two tiers: the fast tier (CPU,
memory, network, disk, GPU) every `interval_ms`; the process table — by far the most
expensive read — every *N*th fast tick, targeting ~2 s. When the panel is hidden the whole
loop halves its rate and stops reading the process table entirely, since only the tray and
taskbar still need data.

**The UI thread** runs an ordinary Win32 message loop. It never polls.

Results are published into one of **two** `Snapshot` instances, alternated, so the UI
always has a stable object to paint while the sampler fills the other. Publication is a
`Volatile.Write` followed by `PostMessage(WM_SNAPSHOT)`; the UI thread reads via
`Volatile.Read`. No locks.

Anything that must not race with an in-flight sample — rebuilding the PDH query when GPU
sampling is toggled — is expressed as a `volatile` flag that the sampler thread applies at
the top of its own loop, rather than being written through from the UI thread.

### Allocation

The steady-state loop allocates nothing. This is the difference between a lightweight
monitor and one that merely starts light:

- Two `Snapshot` buffers, reused.
- Process-table and interface-table buffers are grow-only native allocations.
- Process names are cached per PID (guarded by create time, so PID reuse is detected);
  only a first-seen process allocates a string.
- Text is formatted into pooled `char[]` via `TryFormat` — no interpolation in paint.
- GDI pens and brushes are pooled by colour, and purged when the palette changes.

---

## Where the numbers come from

| Metric | Source | Why |
|---|---|---|
| CPU total + per core | `NtQuerySystemInformation(SystemProcessorPerformanceInformation)` | Cheaper than PDH; exact tick deltas |
| Memory, commit | `GlobalMemoryStatusEx`, `GetPerformanceInfo` | Essentially free |
| Network | `GetIfTable` / `MIB_IFROW` | See below |
| Disk | PDH `\PhysicalDisk(_Total)\…` | No cheap direct equivalent |
| GPU, VRAM | PDH `\GPU Engine(*)`, `\GPU Adapter Memory(*)` | Vendor-neutral, no NVML |
| Processes | `NtQuerySystemInformation(SystemProcessInformation)` | One call for the whole table |

Everything works as a normal user. No admin, no WMI, no vendor SDK.

**Process table.** One syscall returns every process with CPU times, private bytes and I/O
counters already populated. Enumerating `System.Diagnostics.Process` would open a handle
per process and cost more than every other sampler combined. The price is a hand-written
256-byte `SYSTEM_PROCESS_INFORMATION` layout where one wrong field silently shifts
everything after it.

**Network.** Uses the older `GetIfTable`, not `GetIfTable2`. `MIB_IF_ROW2` is a ~1350-byte
struct with several enum-alignment traps that are easy to get silently wrong;
`MIB_IFROW` is all `DWORD`s with no padding ambiguity. Its 32-bit octet counters are still
exact here — deltas are taken in unsigned arithmetic, so a wrap is handled correctly as
long as an interface moves under 4 GiB per second. *This is a deliberate trade of
capability for confidence, and the main thing generated bindings would buy back.*

**Counters that do not exist** are not an error. A PDH path that fails to resolve (no GPU
counters on a VM, an old driver) marks the metric unavailable and the UI hides that row
rather than showing a confident zero.

**GPU counters are the expensive ones.** `\GPU Engine(*)` has one instance per
process-engine pair, and PDH retains state for instances it has seen. They are created
only while something on screen actually shows a GPU figure, and the whole query is rebuilt
every ~10 minutes to drop accumulated instance state.

---

## Compositing

This is the least obvious part of the codebase, and the reason is worth stating plainly:
**GDI does not write an alpha channel.** Any text drawn with `ExtTextOut` into a 32-bit
DIB leaves the alpha byte at zero. Every transparency effect here is a way of working
around that.

There are **two** paths, deliberately not unified:

**Opaque themes** (Glass, AMOLED, Light) draw a solid surface and let the window's
constant alpha handle translucency — ordinary `WM_PAINT` + `BitBlt`.

**Clear** draws its ink on black, then recovers a per-pixel alpha from the ink's own
brightness and hands the whole surface to `UpdateLayeredWindow`.

Why not use the second path everywhere? Because recovering alpha from brightness only
works for *light* ink. Light's near-black text (`#1C1B19`) would come out at roughly 11%
alpha and vanish. The per-pixel path is reserved for the one theme whose palette is built
for it — which is also why Clear's palette is all whites and pastels.

The same brightness-recovery trick is used by the tray icon and the taskbar readout, both
for the same reason: they must composite onto a surface whose colour we do not control.

A consequence worth knowing: on Clear the opacity slider fades the *surface* while text
keeps full alpha, so lowering it never hurts legibility.

### Corner radius

The two paths honour the radius differently, and the difference is visible:

- **Clear** composites its own alpha, so the radius is exact and antialiased.
- **Opaque themes** are a rectangular window whose corners DWM rounds — and DWM exposes
  three *states*, not a radius. An arbitrary value is therefore applied by clipping the
  window with a region: exact, but hard-edged, because regions are 1-bit masks with no
  antialiasing available.

At the default radius DWM rounding is left on, so the common case keeps the smooth native
corner and only a custom value trades that for exactness.

---

## The notch

The collapsed state is a fixed-height pill. Each element occupies a **fixed-width slot**,
so the notch changes width only when elements are toggled — never as the digits change. A
container that reflows at 1 Hz is the one thing an ambient readout must not do.

### Input

The window reports `HTCLIENT` from `WM_NCHITTEST`, not `HTCAPTION`. `HTCAPTION` would give
free dragging, but it routes every mouse message to the non-client area where
hover-to-expand never sees them — so dragging is implemented by hand with a 4 px
click/drag threshold.

Two non-obvious behaviours, both found by testing:

**`WM_MOUSELEAVE` is not trustworthy on its own.** Expanding resizes the window, and a
resize under the pointer makes Windows deliver `WM_MOUSELEAVE` even though the pointer
never moved out. Acting on it blindly produces a collapse/expand oscillation. The handler
re-checks the cursor against the window rect before collapsing.

**Clicking an unpinned panel pins it; it does not collapse it.** With hover-expansion on,
hovering has already expanded the panel by the time a click lands, so "click toggles
collapsed" would collapse it and hover would instantly re-expand — the pinned state would
be unreachable.

### Anchoring

Resizing holds whichever edge the window is nearest and grows inward. Always holding the
leading edge only works for a top-left-ish widget; a panel docked to the bottom would
expand past the screen, get clamped, and then collapse to wherever the clamp left it,
drifting out of its corner a little more on every hover.

### Z-order

The notch is **always** topmost — a screen-edge readout buried under a window is wasted
pixels. A *hover*-expansion also stays topmost, because it unfolds over whatever window
the pointer is already on and would otherwise vanish the instant it appeared. Only a
deliberately **pinned** panel honours the always-on-top setting.

---

## Settings window

Custom-drawn with the same primitives as the monitor, so it wears the active theme — an
AMOLED panel should not open a light-grey system dialog.

Controls are a flat list of records. **Layout, painting and hit-testing all walk the same
list**, so a control can never be drawn somewhere it cannot be clicked.

Sections (a header plus its controls) are packed into columns whole, and the column count
is chosen adaptively — the fewest that fit the monitor's work area, up to three. A single
column reads best, so it only widens when the content genuinely does not fit. A header
stranded at the foot of one column with its controls at the head of the next reads as two
broken groups, which is worse than an uneven column.

---

## Taskbar readout

**This is the fragile feature, and it is fragile for everyone who ships it.** There is no
supported way to put a control in the Windows taskbar.

The usual technique is to `SetParent` a child window into `Shell_TrayWnd`. Offender
deliberately does **not** do that. A child is destroyed whenever Explorer restarts, gets
clipped by the shell's own layout, and — verified here — `UpdateLayeredWindow` does not
composite reliably for a cross-process child; the window existed, was positioned
correctly, reported visible, and rendered nothing.

Instead it is an ordinary top-level, always-on-top, non-activating layered window parked
over the taskbar strip and repositioned every tick. Visually identical, and it owns its
own lifetime: an Explorer restart moves the taskbar and the next tick simply follows it.

It is opt-in, off by default, and horizontal taskbars only.

---

## Testing

There is no unit-test suite. What exists instead is `tools/capture.ps1`, which screenshots
a live window for visual verification, plus measured footprint runs. Two traps it encodes,
both of which produced misleading results before being understood:

1. **Run DPI-aware.** The app is PerMonitorV2, so a DPI-virtualised caller gets
   scaled-down rects from `GetWindowRect` and captures the wrong screen region.
2. **Find windows with `EnumWindows`, not `FindWindow`.** A window class registered by
   another process without `CS_GLOBALCLASS` cannot be resolved by name from outside that
   process — `FindWindow("OffenderPanel")` returns 0 even though the window is right there.

`PrintWindow` cannot read a layered window (the Clear theme, the taskbar readout); use
`-Screen` for those.

Accuracy is spot-checked against `psutil` under load — CPU and RAM have matched within a
percentage point.

---

## Things that would be worth doing next

- **Produce the NativeAOT build and measure it.** Every published figure is from the
  framework-dependent build. This is the single biggest open question.
- **Generated Win32 bindings.** 96 hand-written P/Invoke signatures and several
  hand-transcribed struct layouts are the highest-risk surface in the codebase, and the
  reason for the `GetIfTable` compromise above.
- **Replace PDH for GPU** with D3DKMT queries, removing the most expensive sampler and its
  instance-retention behaviour.
- **Temperatures**, the most-requested missing feature — needs a driver or admin.

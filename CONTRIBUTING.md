# Contributing

Thanks for looking. Offender is small and early, so almost everything is open.

## Getting set up

```powershell
winget install --id Microsoft.DotNet.SDK.10 -e
git clone https://github.com/zakijariwala/offender
cd offender
.\build.ps1
```

That gives a framework-dependent build that runs on the installed .NET runtime — fine for
all development.

The NativeAOT publish additionally needs the MSVC linker:

```powershell
winget install --id Microsoft.VisualStudio.2022.BuildTools -e `
  --override "--quiet --wait --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended"

.\build.ps1 -Release
```

Read [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) before changing anything structural. It
documents the decisions that look wrong until you know why.

## The one rule that matters

**The steady-state loop must not allocate.** A monitor that leaks a few kilobytes a second
stops being a lightweight monitor after an afternoon, and that is the entire point of this
project. Concretely:

- No string interpolation or `string.Format` in painting — format into a pooled `char[]`
  via `TryFormat` (see `Ui/TextBuf.cs`).
- Reuse buffers. Native buffers are grow-only; `Snapshot` is double-buffered.
- Pool GDI objects, and free them when the palette changes.
- Every `Create*` GDI call needs a matching `Delete*` on every path, including failure
  paths.

If you add a per-tick code path, measure it before and after — see below.

## House style

Match the surrounding code. Beyond that:

- **Comments explain *why*, not *what*.** The codebase is full of non-obvious Win32
  workarounds; a comment that restates the line is noise, one that records why the obvious
  approach failed is the most valuable thing in the file.
- Keep the layering: `Native/` is interop with no logic, `Sampling/` has no UI types,
  `Ui/` does no measurement.
- Interop signatures stay all-blittable (`nint`, `int`, `uint`, pointers) so
  `LibraryImport` generates no marshalling and NativeAOT stays happy. BOOL-returning calls
  come back as `int`.

## Testing a change

There is no unit-test suite; verification is empirical.

**Visual changes** — screenshot the live window:

```powershell
.\tools\capture.ps1 -Class OffenderPanel   -Out panel.png
.\tools\capture.ps1 -Class OffenderSettings -Out settings.png
.\tools\capture.ps1 -Class OffenderTaskbar -Out taskbar.png -Screen
```

Use `-Screen` for layered windows (the Clear theme, the taskbar readout) — `PrintWindow`
cannot read them.

**Footprint changes** — measure, don't assume:

```powershell
$p = Get-Process Offender
$p.WorkingSet64/1MB; $p.PrivateMemorySize64/1MB; $p.Handles
```

Two traps documented in the README that will otherwise waste your time: the reading right
after launch is *below* steady state (startup trims the working set), and every feature
costs a one-time allocation on first use. **Sample at intervals, not at two endpoints** —
one-time initialisation looks exactly like a leak if you only have two points.

## Good first issues

- Producing and measuring the NativeAOT build.
- Replacing hand-written P/Invoke with generated bindings (this also unblocks 64-bit
  network counters — see ARCHITECTURE).
- Win10 verification; ARM64 build.
- Multi-monitor and mixed-DPI verification.
- Temperatures / fan / power, the most-requested missing feature.

## Reporting bugs

Include your Windows build, display scaling, monitor layout, and the contents of
`%APPDATA%\Offender\config.ini`. For anything about footprint, include a series of
measurements over time rather than a single number.

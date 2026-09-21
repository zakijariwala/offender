# Known issues

Current as of v0.1.1.

Three categories, because they deserve different levels of trust:

- **Confirmed defects** — observed, with evidence.
- **Unverified** — code paths that exist but have never been exercised. Not known to be
  broken; not known to work either.
- **Limitations** — deliberate scope or platform boundaries, listed so nobody files them
  twice.

---

## Confirmed defects

### 1. Unexplained memory and handle step · Medium

After hours of heavy machine activity the process jumps by roughly **+100 kernel handles
and +11 MB working set**, then holds at the new level. Observed twice (35.6 MB / 428
handles, and 42.7 MB / 423 handles, against a normal 7–24 MB / ~260–330).

What is ruled out: it is not GDI or USER objects (34 and 17, both normal), not threads
(6, all from startup), and not a per-tick leak — controlled idle watches are flat for
4+ minutes, and settings open/close and expand/collapse both plateau completely.

What is not known: what those handles are. A `\GPU Engine(*)` PDH instance-retention
hypothesis was tested with a 35-second A/B and showed **no difference**, so it is
unconfirmed.

A mitigation is in place — GPU counters are only created while a GPU figure is on screen,
and the PDH query is rebuilt every ~10 minutes to drop retained instance state — but that
is a guess at the cause, not a validated fix.

*Reproducing this deliberately is the single most valuable open task.*

### 2. v0.1.0 shipped with a wrong version resource · Fixed in 0.1.1

The published v0.1.0 binary reports `FileVersion 1.0.0.0`, a `ProductVersion` carrying the
hash of the *initial* commit, a blank copyright, and `CompanyName` defaulted to the
assembly name. No `<Version>` was set in the project file.

This matters beyond cosmetics: MSI, winget and MSIX all key upgrade detection off the file
version, so every release would have reported 1.0.0.0 and upgrades would have silently
no-opped.

Fixed in 0.1.1. Release builds now take their version from the git tag, and the release
workflow fails if the binary's version resource does not match the tag it was built from.
**v0.1.0 itself is unchanged** — released artifacts are immutable — so anything reading the
version of a v0.1.0 download still sees 1.0.0.0.

### 3. The selected network interface is never shown · Low

`NetSampler` picks the busiest adapter (or the one matched by `pin_interface`) and records
its description in `Snapshot.NetInterface` — which **nothing ever reads**. On a machine
with several adapters, or with `pin_interface` set, there is no way to tell which adapter
the NET rows describe.

Either surface it in the panel or on the tray tooltip, or drop the field.

### 4. `SettingsWindow` object is retained after its window closes · Low

`PanelWindow._settings` is never set to `null` when the settings window is destroyed, so
the managed object survives until settings is reopened or the app exits.

The expensive part is already handled — the `GdiCanvas`, including its multi-megabyte DIB,
is disposed on `WM_DESTROY` — so this is a small managed object, not a GDI leak.

### 5. Notch throughput slot can clip · Low

`AppendCompactRate` emits up to five characters for sub-kilobyte rates (`1011B`), plus a
direction arrow, into a slot sized at 40 logical px. The worst case is marginal and can
clip the last glyph.

Either widen the slot or switch sub-kilobyte rates to three significant characters.

### 6. Corner radius is aliased on the opaque themes · Low

DWM exposes three corner *states*, not a radius, so a custom radius is applied by clipping
the window with a region — and regions are 1-bit masks with no antialiasing. At larger
radii the corners look visibly jagged next to the smooth DWM default.

Clear is unaffected: it composites its own alpha and gets an exact antialiased radius.

Fixing this properly means moving the opaque themes to per-pixel alpha with a
geometrically-derived mask, which would also need re-testing the Glass theme's acrylic.

### 7. Tray tooltip line breaks · Low

`TrayIcon.WriteTip` separates lines with `\n`. Multi-line tray tooltips generally expect
`\r\n`, and reliable multi-line behaviour needs `NOTIFYICON_VERSION_4`. How it actually
renders has not been visually checked.

---

## Unverified

None of these are known to be broken. They are code paths that have never run.

| # | Area | Why it is unverified |
|---|---|---|
| 8 | **Acrylic blur (Glass theme)** | `SetWindowCompositionAttribute` is an undocumented private API and may be failing silently — the theme would look like a plain solid panel and nobody would notice. `PrintWindow` captures cannot show blur, so the screenshots do not prove it works. |
| 9 | **Three-column settings layout** | The column count adapts to the work area; a 1128 px display fits everything in two. The three-column branch has never rendered. |
| 10 | **Multi-monitor / mixed DPI** | `WM_DPICHANGED` handling and per-monitor anchoring are implemented but were developed on a single display. |
| 11 | **Explorer restart recovery** | The `TaskbarCreated` handler rebuilds the tray icon and taskbar readout. Explorer was never actually restarted during testing. |
| 12 | **Start with Windows across a reboot** | The `HKCU\…\Run` entry is written and read back correctly, but the machine was never rebooted to confirm it launches and restores position. |
| 13 | **Tray icon appearance** | Confirmed to be created and updating every tick; never visually inspected against a real taskbar, light or dark. |
| 14 | **One-off: notch started expanded** | During one test run the panel came up expanded despite `collapsed=1`. Never reproduced across a dozen subsequent runs. Recorded in case it resurfaces. |

---

## Limitations

Deliberate, not defects:

- **x64 only.** No ARM64 build.
- **Windows 11 tested only.** Windows 10 should work — DWM corner rounding is simply
  ignored there — but is untested.
- **Horizontal taskbars only** for the taskbar readout. On a vertical taskbar there is
  nowhere sensible to put a wide readout, so the feature stays off.
- **No temperatures, fan speeds or power draw.** These need vendor drivers or admin
  rights. This is the most-requested feature in the category.
- **No per-application network usage.**
- **The binary is unsigned**, so SmartScreen warns on first run. See
  [RELEASING.md](RELEASING.md).

---

## Reporting something new

Include your Windows build, display scaling, monitor layout, and
`%APPDATA%\Offender\config.ini`.

For anything about memory or handles, include **a series of measurements over time**, not
a single number. Two readings taken minutes apart will show growth that is not growth —
the working set immediately after launch sits below steady state because startup ends with
a deliberate trim, and each feature costs a one-time allocation the first time it is used.
See the footprint notes in the [README](../README.md#footprint).

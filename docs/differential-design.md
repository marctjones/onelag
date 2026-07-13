# Differential Design

This document records why OneLag stopped being a OneDrive tool that occasionally shrugs, and became a lag
differential tool in which OneDrive is one hypothesis among ten.

## The Problem With The Previous Design

The original engine asked one question: *is OneDrive to blame?* It answered by combining static folder shape
(item count, high-churn directories, sync blockers) with whatever live OneDrive telemetry it could get, and
collapsing the result into four verdicts. Non-OneDrive causes had no evidence collectors at all, so they
could never win. `NonOneDrivePressureSuspected` existed as a verdict but had almost nothing behind it.

Three failure modes followed from that shape, and all three showed up in a real scan of a real laptop:

1. **Static risk was treated as an active cause.** A scan reached `OneDrivePossible` on 22 `desktop.ini`
   hits — a file Windows and OneDrive create themselves — with no live telemetry at all. Folder shape
   describes exposure. It cannot explain a freeze.

2. **A capture with no evidence still produced a confident-looking report.** The same scan ran while OneDrive
   was not even running. Every live signal was absent, and the report said so only in a footnote at the
   bottom, under a headline verdict that implicated OneDrive.

3. **The most likely real cause was invisible.** The machine in question lagged when docked with external
   displays and Bluetooth peripherals, and was fine undocked. That is a DPC/ISR latency signature — a kernel
   driver holding a CPU at high IRQL. OneLag collected no DPC counters, no display topology, no Bluetooth
   state, and no dock state. It could not have found this if it tried.

## What Changed

### OneDrive is now one hypothesis among ten

`HypothesisEngine` scores every candidate cause against the same evidence and ranks them:

| Hypothesis | Primary evidence |
| --- | --- |
| `OneDriveSync` | OneDrive process CPU, log churn, item count, high-churn directories |
| `DriverInterruptLatency` | DPC and interrupt time, per-core and total; DPCs queued |
| `DisplayOrDockPipeline` | Indirect/USB displays, external displays, mixed refresh rates, display-driver resets |
| `BluetoothOrInputRadio` | Bluetooth radio state, connected devices, coincident interrupt pressure |
| `StorageSaturation` | Disk queue length, disk active time |
| `CpuContention` | Total CPU, processor queue, top consumers |
| `MemoryPaging` | Available memory, commit charge, paging usage |
| `ShellExtensionBlocking` | Explorer message-pump latency, hung windows |
| `SecurityOrSearchScanner` | Defender, Search, and Update servicing CPU |
| `ThermalOrPowerThrottling` | Processor-power, thermal, and WHEA events |

Every hypothesis records the evidence **for** it, the evidence **against** it, and its own next step. A cause
that is not OneDrive now produces a specific action rather than a shrug toward WPR.

### A hypothesis cannot be promoted on evidence that was never collected

Each hypothesis refuses to score on evidence it could not gather. If DPC counters were unavailable,
`DriverInterruptLatency` returns `Unknown`, not a low score that reads as exoneration. If the machine has no
display context, `DisplayOrDockPipeline` returns `Unknown`. Absence of evidence is reported as absence of
evidence.

This is why verdicts are deliberately **not** clamped by overall evidence quality. Each hypothesis already
gates itself, so a thin capture cannot manufacture a verdict; clamping again would suppress a hypothesis
that *was* directly measured just because unrelated collectors were unavailable.

### OneDrive specifically requires live evidence

Static folder shape can raise OneDrive no higher than `Possible`. To reach `Likely` or `StronglySupported`,
the capture must contain at least one live signal: elevated OneDrive CPU, elevated log churn, OneDrive active
during disk saturation, or a hung shell while OneDrive is running. If OneDrive was not running at all, the
hypothesis is reported as *untested*, not disproven — and never as the headline.

Sync-restriction issues (invalid names, long paths, blocked names) still produce their findings and their
rename guidance, because they are real sync-configuration problems. They no longer move the lag diagnosis.

### Evidence quality is stated before any verdict

`EvidenceQualityAssessor` grades each capture `Complete`, `Partial`, or `Insufficient` and lists exactly what
was missing. An `Insufficient` capture says so at the top of the report, in place of the headline:

> This capture contains almost no live evidence. Nothing below should be treated as a diagnosis. Run
> `onelag watch start` and reproduce the lag, or re-run `onelag scan` while the machine is actually slow.

A report that looks authoritative while containing nothing is worse than no report.

### Interrupt latency is measured, per core

The counters that were missing are the ones that matter most for a frozen desktop:

- `\Processor(_Total)\% DPC Time`
- `\Processor(_Total)\% Interrupt Time`
- `\Processor(_Total)\DPCs Queued/sec`
- `\Processor(_Total)\Interrupts/sec`
- `\Processor(*)\% DPC Time` and `\Processor(*)\% Interrupt Time`, read across every instance

The per-core wildcard counters matter because a driver storm usually pins one core, and the `_Total` instance
averages that away. A core at 30% DPC time reads as 2% when divided across sixteen cores, which is the
difference between a stalled desktop and a clean bill of health.

This is still user-mode evidence: it proves a driver is responsible without naming which one. WPR/WPA with
the DPC/ISR profile remains the escalation, and OneLag still generates that runbook rather than starting
traces on its own.

### The machine's physical configuration is recorded

`HostContext` captures display topology (via `QueryDisplayConfig`), Bluetooth radio and connected-device
state, power source, wired-network state, and a derived dock state — on every watch sample.

The signal worth the most is `DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INDIRECT_WIRED`: displays driven by
DisplayLink-class USB graphics, which render frames on the CPU and push them over USB, and are a documented
source of long DPC routines.

### Explorer blocking is tested, not inferred

`ShellResponsiveness` sends `WM_NULL` to the shell tray window with `SMTO_ABORTIFHUNG` and times the reply,
and checks `IsHungAppWindow`. This is the same mechanism Windows uses to decide a window is hung, and it
measures the source guide's central failure mode — a stalled sync-status query blocking Explorer — directly,
instead of inferring it from folder shape.

### Watch mode correlates lag with configuration

`WatchContextCorrelation` groups episodes by the configuration they happened in and reports episodes per hour
for each. When the same machine is recorded docked and undocked, the comparison is direct:

> Every lag episode happened in `external-display + bluetooth-connected + docked-likely` (12.4 per hour), and
> none at all in `internal-display-only + bluetooth-off + undocked-likely`. The configuration itself is
> implicated: the difference between those two states is where to look, not OneDrive.

A snapshot scan can never produce that sentence. It is the single most diagnostic thing the tool can say.

## What Did Not Change

- No continuous background service. Watch mode is still opt-in, bounded, and explicitly started.
- No silent file moves, deletes, resets, or process kills.
- No parsing of undocumented OneDrive databases.
- No automatic WPR, ProcMon, or heavy tracing in the default path.
- Bluetooth device enumeration reads only the already-connected set. It never issues a radio inquiry, which
  would put a scan in the middle of a diagnostic sample.

### The driver gets named

`onelag trace dpc` runs a bounded kernel ETW session. It records every DPC and ISR with the address of the
routine that ran, builds a map of loaded kernel images from the ImageLoad rundown, resolves each routine
address into the image that contains it, and reports drivers by the time they spent at high IRQL.

`DriverClassifier` then maps each driver image to the subsystem that owns it — DisplayLink-class USB
graphics, GPU kernel drivers, Thunderbolt/USB4, the Bluetooth stack, Wi-Fi (which shares the 2.4 GHz band
with Bluetooth), storage, the cloud-files filter, the Defender filter — so attributed DPC time strengthens
the hypothesis it belongs to rather than sitting in the report as trivia. A named DisplayLink driver
promotes the display-and-dock hypothesis above OneDrive on its own.

This is elevation-gated and explicitly invoked. It is never part of the default scan path, which keeps the
project's promise not to start heavy tracing on its own.

### The experiment is a command

`onelag compare --session docked-day --session undocked-day` pools watch sessions recorded in different
hardware configurations, groups every episode by the configuration it happened in, and reports episodes per
hour for each. The user who is fine undocked in the office and slow docked at home has already run this
experiment by accident; this makes the same comparison from recorded evidence.

## Known Limits Of The New Probes

- **Dock state is derived, not read.** It is inferred from display topology, wired-network state, and power
  source. A dock with no display attached and no Ethernet will read as undocked.
- **Driver attribution needs administrator rights** and a system-wide kernel logger session. Without
  elevation it says so rather than returning an empty result.
- **Routine addresses in unloaded or dynamically relocated images resolve to `unresolved`** and are excluded
  from the ranking rather than guessed at.

## Still Open

- Real Windows 11 laptop validation of the DPC, driver-trace, display-topology, Bluetooth, and shell probes.
  All of the new Windows code degrades to an explicit `unavailable` evidence state rather than failing, but
  none of it has been observed against real hardware yet.

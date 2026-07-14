# OneLag

OneLag is a Windows diagnostic and remediation-planning utility for OneDrive-driven system lag. The project starts from the local source document, [OneDrive_Diagnostic_and_Remediation_Guide.pdf](OneDrive_Diagnostic_and_Remediation_Guide.pdf), which describes a failure mode where OneDrive sync load, very high item counts, shell extension blocking, and risky synced development folders can make Windows Explorer and the desktop feel frozen.

The working product is a low-impact, one-shot diagnostic CLI first. It identifies local OneDrive roots, measures current OneDrive and whole-system evidence where available, stream-counts high-risk directory clusters without loading huge file lists into memory, and produces a practical report. It does not install a resident background service.

The roadmap also includes an opt-in responsiveness watch mode for recurring keyboard, mouse, and UI freezes. The current preview has a bounded foreground recorder with explicit start/stop controls, lag markers, privacy redaction, ring-buffer retention, strict resource limits, and native tray/GUI surfaces. All-day and real Windows 11 laptop validation remain open.

## Install The Windows Preview

Download the latest `OneLag-*-win-x64-installer.zip` from [GitHub Releases](https://github.com/marctjones/onelag/releases/latest), extract it on Windows 11, then run:

```powershell
Set-ExecutionPolicy -Scope Process Bypass -Force
.\Install-OneLag.ps1
```

Open a new terminal and verify the install:

```powershell
onelag version
onelag scan
```

This preview is a self-contained Windows x64 CLI plus native Windows tray/GUI plus PowerShell installer bundle. It is not yet a signed MSI/EXE installer.

## Commands

Run a one-shot scan using discovered OneDrive roots. With no `--output`, it writes a timestamped report
(`onelag-report-20260714-175230.md`) so a second run never destroys the first — diagnosing lag almost always
means comparing two captures, and both have to survive:

```powershell
onelag scan
```

Scan a specific folder and emit JSON:

```powershell
onelag scan --root "$env:USERPROFILE\OneDrive" --format json
```

Pass an explicit `--output PATH` when you want a fixed name — a script piping the report elsewhere, for
example, or a name you plan to refer back to from a later command, as the rest of the walkthrough below does:

```powershell
onelag scan --output onelag-report.md
```

If that path already exists, OneLag refuses to overwrite it and exits non-zero rather than silently destroying
whatever it contains; pass `--overwrite` when that is what you actually want. The rest of the examples below
name their outputs explicitly, because later steps (comparing sessions, bundling reports) refer back to those
names — running one of them twice with the same name needs `--overwrite`.

## Catch The Freeze, Don't Describe The Calm

Run this **from an elevated terminal, and leave it running for the day**. It detects freezes by itself:

```powershell
onelag watch start --duration 8h
```

You do not need to tag anything. Asking you to mark a freeze is asking you to act at the one moment your
machine will not let you — so the watcher measures its own starvation instead. It asks to sleep for a second,
and when it wakes up four seconds later, it knows the machine stalled, writes a marker naming the signal that
tripped, and takes a deep capture on the spot. It also samples memory every thirty seconds, which turns an
all-day run into a leak hunt: the report ranks processes by **growth rate rather than size**, because the
leaker is whatever is climbing, not whatever is biggest.

If commit keeps climbing while every process stays flat, the leak is in the kernel, held by a driver, and no
process list will ever show it. The report says so.

`watch start` **refuses to run with degraded collectors**, because an eight-hour session that silently
collects nothing is worse than no session at all. If it stops you, re-run it as administrator.

Generate the report when the session ends:

```powershell
onelag watch report --report onelag-watch-report.md
```

If the machine is locking up *right now* and you want a capture this instant:

```powershell
onelag freeze --note "clicks queued up closing chrome windows"
```

That takes no inventory and walks no directory, so it returns while the symptom is still happening, and it
prints its headline to the console — you should not have to open a file mid-freeze.

Reclaim memory from a bloated shell process without rebooting (dry run first; nothing is killed until you
say so):

```powershell
onelag remediate reclaim-memory
onelag remediate reclaim-memory --execute --i-understand-this-restarts-the-shell
```

Name the driver holding the CPU at high IRQL. This needs an elevated terminal, and you should reproduce the
lag while it runs:

```powershell
onelag trace dpc --duration 30s --output onelag-driver-trace.md
```

Compare lag rates across hardware configurations, for example a docked day against an undocked day:

```powershell
onelag compare --session docked-day --session undocked-day --output onelag-comparison.md
```

Check that the probes actually measure anything on this machine before recording a session:

```powershell
onelag selftest
```

Collect the raw log files off this machine into one bundle, so analysis runs over the actual bytes instead
of guessing at what is relevant — every OneDrive `.odl`, the `.log` files under the Windows tree, crash
dumps, and the recent event logs:

```powershell
onelag collect --hours 48 --output onelag-logs
```

Run it from an elevated terminal for a complete bundle (some Windows logs and the Security event log need
administrator rights). Add `--all-channels` to export every event-log channel rather than the broad default
set. The result is a single `.zip` you can pull back for analysis. It is raw and unredacted by design — read
`PRIVACY.txt` inside it before sharing. For a redacted, curated summary suitable for wider sharing, use
`onelag support bundle` instead.

## Finding Lag That Only Happens When Docked

Run `onelag selftest` first. A watch session recorded with degraded collectors produces an
authoritative-looking report containing nothing, and it costs a working day to find that out.


If the machine is fine undocked and slow on the dock, the lag tracks the hardware, not the sync load. Record
both configurations and let the tool compare them.

Record a normal working day on the dock, with the monitors and Bluetooth peripherals you usually use. Press
the lag marker whenever it stutters:

```powershell
onelag watch start --duration 8h --output docked-day
onelag watch mark --output docked-day --note "cursor stuttered dragging a window"
```

Record another working day undocked, on the internal panel, with Bluetooth off:

```powershell
onelag watch start --duration 8h --output undocked-day
```

Compare them:

```powershell
onelag compare --session docked-day --session undocked-day --output onelag-comparison.md
```

If the lag concentrates in one configuration, run the driver trace from an elevated terminal *while in that
configuration* to name the driver:

```powershell
onelag trace dpc --duration 30s
```

Review the supported OneDrive reset plan without changing anything:

```powershell
onelag repair reset-onedrive
```

Execute the first supported reset command candidate only after reviewing the dry run:

```powershell
onelag repair reset-onedrive --execute --i-understand-reset-disconnects-sync
```

Generate WPR/WPA and ProcMon escalation runbooks for unresolved responsiveness pressure:

```powershell
onelag support trace-plan --output onelag-trace-plan
```

Package redacted reports for offline analysis in Codex or Claude Code on another machine:

```powershell
onelag support bundle --report onelag-report.md --report onelag-watch-report.md --output onelag-support-bundle --zip
```

Summarize a saved diagnostic or watch report locally:

```powershell
onelag view --report onelag-watch-report.md --timeline
```

Generate a dry-run move plan for a risky synced folder:

```powershell
onelag remediate move-plan --source "$env:USERPROFILE\OneDrive\project" --destination "C:\LocalDev\project" --output onelag-move-plan
```

Execute a reviewed move plan directly only after pausing OneDrive and confirming the destination:

```powershell
onelag remediate move --source "$env:USERPROFILE\OneDrive\project" --destination "C:\LocalDev\project" --execute --i-understand-moves-files
onelag remediate verify --source "$env:USERPROFILE\OneDrive\project" --destination "C:\LocalDev\project"
```

Open the native Windows tray/GUI:

```powershell
onelag-gui
```

The GUI has tabs for Diagnose (self test), Scan, Watch, Collect Logs, Compare, Reports, Support, and
Remediation, and shows a readiness banner on startup that tells you at a glance whether the probes are
measuring live data. The tray icon's menu covers the workflow you need without opening a terminal: Self Test,
Start/Stop Watch, **Mark Lag Now** (flag a freeze the instant you feel it), and Collect Logs.

## Local Validation

Run the macOS-friendly validation suite from the repository root:

```bash
scripts/test-local.sh
```

That command restores packages, verifies formatting, builds Release, runs tests, and cross-publishes Windows x64 self-contained executables to `tmp/local-validation/publish/win-x64/onelag.exe` and `tmp/local-validation/publish/win-x64-gui/onelag-gui.exe`.

Run the coverage gate:

```bash
scripts/test-coverage.sh
```

That command collects Cobertura coverage, merges duplicate source lines across test projects, and enforces the current ratchet gates. Override with `ONELAG_COVERAGE_MIN` and `ONELAG_CORE_COVERAGE_MIN` only when deliberately tightening the gate.

GitHub CI runs the same Release build and tests on macOS and Windows. macOS CI uploads coverage artifacts, and Windows CI runs the published CLI and GUI through version, GUI smoke, scan, report view, watch, reset dry-run, and remediation smoke coverage.

Sample redacted reports are checked in under [samples](samples/). Review [privacy and support bundle guidance](docs/privacy-and-support-bundles.md) before sharing reports, traces, or logs.

Windows validation details are documented in [Windows 11 validation](docs/windows-11-validation.md).

## What We Are Working On

OneLag is a lag differential tool. OneDrive is one hypothesis among ten, not the default. Every capture ranks
all candidate causes against the same evidence, records what argues for and against each, and states how much
of the evidence it could actually collect before it states a verdict. See
[differential design](docs/differential-design.md) for why.

The ranked causes are OneDrive sync, driver interrupt/DPC latency, the display and dock pipeline, the
Bluetooth and input radio, storage saturation, CPU contention, memory paging, Explorer shell blocking,
Defender/Search/Update scanners, and thermal or power throttling.

We are building a tool that answers these questions on a Windows machine:

- Which of the candidate causes does the evidence actually support, and which does it argue against?
- Is a kernel driver holding a CPU at high IRQL long enough to stall the desktop and the cursor?
- Does the lag track the dock, the external displays, or the Bluetooth radio rather than sync load?
- Is Explorer genuinely blocked, measured rather than inferred?
- Is OneDrive plausibly responsible for the current lag or Explorer unresponsiveness?
- Is the evidence strong enough to say `OneDrive likely`, or is broader non-OneDrive system pressure more likely?
- Are synced folders over the practical item-count limits documented by Microsoft?
- Are development or build-output directories inside OneDrive creating high-churn sync load?
- Are sync blockers such as hidden files, temp files, invalid names, long paths, or large mail/archive/media files involved?
- Is the OneDrive client producing suspicious log churn or high CPU while disk queues are elevated?
- Do recent Windows event logs support or weaken the OneDrive hypothesis?
- What specific folders should be moved out of OneDrive, made online-only, excluded from sync, or reviewed by an administrator?
- What safe, reversible commands should the user run next?
- If the issue happens later, can an opt-in recorder preserve enough evidence around the freeze to explain it?

The first implementation target was a .NET Windows console application because the source guide and Microsoft APIs line up around `System.Diagnostics.PerformanceCounter`, `Process.PriorityClass`, and streaming `System.IO` enumeration. The current preview keeps that tested core while adding guided console, tray, and native GUI surfaces on top of shared services.

## Non-Goals

- No continuous background watcher in the MVP. Later watch mode must be opt-in, bounded, and explicitly started.
- No silent file moves, deletes, OneDrive resets, or process kills.
- No automatic WPR, ProcMon, clean boot, service disablement, Windows Search disablement, or Defender disablement in the default scan path.
- No attempt to parse undocumented OneDrive database internals.
- No embedded local AI runtime; offline AI review should use the explicit support bundle so evidence collection stays deterministic and inspectable.
- No replacement for Microsoft 365 admin sync reports in managed tenants.
- No generic cleaner that treats all large folders as disposable.

## Documentation

- [Problem statement](docs/problem-statement.md)
- [Differential design](docs/differential-design.md)
- [Testing strategy](docs/testing-strategy.md)
- [Research validation](docs/research-validation.md)
- [Architecture](docs/architecture.md)
- [Implementation plan](docs/implementation-plan.md)
- [Development best practices](docs/development-best-practices.md)
- [Release hardening](docs/release-hardening.md)
- [Windows evidence matrix](docs/windows-evidence-matrix.md)
- [Roadmap](ROADMAP.md)
- [Agent instructions](AGENTS.md)

## Current Status

The repository contains the source PDF, design documentation, development guardrails, live milestones/issues [#1](https://github.com/marctjones/onelag/issues/1)-[#61](https://github.com/marctjones/onelag/issues/61), and an initial .NET implementation.

Implemented in the current preview:

- .NET solution split into core, Windows platform probe, CLI, and tests.
- Ranked differential across ten candidate causes, with supporting and opposing evidence and a specific next step for each, plus a live-evidence gate that stops static folder shape from implicating OneDrive on its own.
- `onelag trace dpc`, a bounded kernel ETW trace that attributes DPC and ISR time to specific driver images and maps each driver to the subsystem it belongs to, so the tool names the driver instead of handing out a WPR runbook.
- `onelag compare`, which compares watch sessions recorded in different hardware configurations and reports lag episodes per hour for each.
- Evidence-quality grading (`Complete`, `Partial`, `Insufficient`) stated above the verdict, so a capture with no live evidence says so instead of reading as authoritative.
- DPC and interrupt sampling including per-core maximums, so a driver storm pinning one core is not averaged away.
- Host-context sampling: display topology including DisplayLink-class indirect/USB displays, Bluetooth radio and connected devices, power source, and derived dock state.
- Direct Explorer message-pump probing, so shell blocking is measured rather than inferred.
- Watch-mode configuration correlation, reporting lag episodes per hour grouped by hardware configuration.
- `onelag scan` with streaming inventory, high-risk directory detection, static sync-blocker detection, redacted Markdown/JSON reports, and conservative differential diagnosis.
- Fuller OneDrive known-issue detection for invalid characters, leading/trailing spaces, blocked names, reserved device names, root `forms`, path-length limits, duplicate names, network/reparse sync roots, temporary files, PST/OST files, OneNote notebook files, preview-size limits, and large files.
- OneDrive client-cache health metadata checks that avoid undocumented database parsing, report log/settings/DAT metadata, and offer a Microsoft-supported reset dry run.
- Windows system-pressure sampling for OneDrive CPU, top-process CPU, memory availability/commit pressure, disk queue/active-time counters, paging-file usage, system-drive free space, and power source when available.
- Recent Windows Event Viewer summary correlation for critical, error, and warning events when Windows can provide it.
- Deeper lightweight Windows evidence for Windows Update, Defender, Driver Frameworks, and Files On-Demand attribute metadata when available.
- WPR/WPA and ProcMon escalation-plan generation for inconclusive responsiveness pressure, without automatically starting heavy tracing.
- Dry-run remediation move-plan generation with explicit execution flags, rollback script, verification script, and destination-space evidence.
- `onelag watch` bounded foreground recorder with start, stop, status, mark, and report commands.
- Watch report episode detection that groups timer-drift samples and manual lag markers into inferred categories.
- UI-neutral report-view service plus `onelag view` for saved diagnostic and watch report summaries.
- `onelag support bundle` for offline Codex/Claude Code analysis with copied reports, summaries, manifest, privacy checklist, user notes, environment snapshot, and a ready-to-use prompt.
- Native Windows Forms tray/GUI with a startup readiness banner, self test, scan, watch, log collection, session comparison, report view, support-bundle export, and remediation controls, and a tray menu covering self test, watch, mark-lag, and log collection.
- Direct remediation move, verify, and rollback commands behind explicit confirmation flags.
- Coverage collection, merged coverage summary, and CI artifact upload with initial ratchet gates.
- Redacted sample diagnostic/watch reports and privacy/support-bundle guidance.
- Cross-platform test framework with core unit tests, Windows-layer parser tests, CLI process tests, local macOS validation, Windows CI, and release-time Windows executable smoke tests.
- Windows x64 self-contained publish and PowerShell installer bundle.
- GitHub Actions release workflow for test, publish, package, and release artifacts.

Still roadmap work:

- Real Windows 11 validation of the DPC, driver-trace, display-topology, Bluetooth, and shell probes. All of the new Windows code degrades to an explicit `unavailable` evidence state rather than failing, but none of it has been observed against real hardware yet.
- Signed MSI/EXE installer.
- Deeper Windows-only validation for Files On-Demand states, WPR/WPA, and ProcMon escalation runbooks.
- Real Windows 11 laptop validation run on a self-hosted runner.
- Broader integration tests on real Windows 11 systems.

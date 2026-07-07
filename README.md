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
onelag scan --output onelag-report.md
```

This preview is a self-contained Windows x64 CLI plus native Windows tray/GUI plus PowerShell installer bundle. It is not yet a signed MSI/EXE installer.

## Commands

Run a one-shot scan using discovered OneDrive roots:

```powershell
onelag scan --output onelag-report.md
```

Scan a specific folder and emit JSON:

```powershell
onelag scan --root "$env:USERPROFILE\OneDrive" --format json --output onelag-report.json
```

Start a bounded foreground watch session:

```powershell
onelag watch start --duration 8h
```

Mark that lag is happening, then generate a watch report:

```powershell
onelag watch mark
onelag watch report --report onelag-watch-report.md
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

We are building a tool that answers these questions on a Windows machine:

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
- Native Windows Forms tray/GUI with scan, watch, report view, support-bundle export, and remediation controls.
- Direct remediation move, verify, and rollback commands behind explicit confirmation flags.
- Coverage collection, merged coverage summary, and CI artifact upload with initial ratchet gates.
- Redacted sample diagnostic/watch reports and privacy/support-bundle guidance.
- Cross-platform test framework with core unit tests, Windows-layer parser tests, CLI process tests, local macOS validation, Windows CI, and release-time Windows executable smoke tests.
- Windows x64 self-contained publish and PowerShell installer bundle.
- GitHub Actions release workflow for test, publish, package, and release artifacts.

Still roadmap work:

- Signed MSI/EXE installer.
- Deeper Windows-only validation for Files On-Demand states, WPR/WPA, and ProcMon escalation runbooks.
- Real Windows 11 laptop validation run on a self-hosted runner.
- Broader integration tests on real Windows 11 systems.

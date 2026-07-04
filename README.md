# OneLag

OneLag is a Windows diagnostic and remediation-planning utility for OneDrive-driven system lag. The project starts from the local source document, [OneDrive_Diagnostic_and_Remediation_Guide.pdf](OneDrive_Diagnostic_and_Remediation_Guide.pdf), which describes a failure mode where OneDrive sync load, very high item counts, shell extension blocking, and risky synced development folders can make Windows Explorer and the desktop feel frozen.

The working product is a low-impact, one-shot diagnostic CLI first. It identifies local OneDrive roots, measures current OneDrive and whole-system evidence where available, stream-counts high-risk directory clusters without loading huge file lists into memory, and produces a practical report. It does not install a resident background service.

The roadmap also includes an opt-in responsiveness watch mode for recurring keyboard, mouse, and UI freezes. The current preview has a bounded foreground recorder with explicit start/stop controls, lag markers, privacy redaction, ring-buffer retention, and strict resource limits. Tray and GUI surfaces remain roadmap work.

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

This preview is a self-contained Windows x64 CLI plus PowerShell installer bundle. It is not yet a signed MSI/EXE installer, tray app, or native GUI.

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

The first implementation target is a .NET Windows console application because the source guide and Microsoft APIs line up around `System.Diagnostics.PerformanceCounter`, `Process.PriorityClass`, and streaming `System.IO` enumeration. Guided console, tray, and native GUI surfaces can come later after the core scanner and watch services are proven.

## Non-Goals

- No continuous background watcher in the MVP. Later watch mode must be opt-in, bounded, and explicitly started.
- No silent file moves, deletes, OneDrive resets, or process kills.
- No automatic WPR, ProcMon, clean boot, service disablement, Windows Search disablement, or Defender disablement in the default scan path.
- No attempt to parse undocumented OneDrive database internals.
- No replacement for Microsoft 365 admin sync reports in managed tenants.
- No generic cleaner that treats all large folders as disposable.

## Documentation

- [Problem statement](docs/problem-statement.md)
- [Research validation](docs/research-validation.md)
- [Architecture](docs/architecture.md)
- [Implementation plan](docs/implementation-plan.md)
- [Development best practices](docs/development-best-practices.md)
- [Roadmap](ROADMAP.md)
- [Agent instructions](AGENTS.md)

## Current Status

The repository contains the source PDF, design documentation, development guardrails, live milestones/issues [#1](https://github.com/marctjones/onelag/issues/1)-[#60](https://github.com/marctjones/onelag/issues/60), and an initial .NET implementation.

Implemented in the current preview:

- .NET solution split into core, Windows platform probe, CLI, and tests.
- `onelag scan` with streaming inventory, high-risk directory detection, static sync-blocker detection, redacted Markdown/JSON reports, and conservative differential diagnosis.
- Fuller OneDrive known-issue detection for invalid characters, leading/trailing spaces, blocked names, reserved device names, root `forms`, path-length limits, duplicate names, network/reparse sync roots, temporary files, PST/OST files, OneNote notebook files, preview-size limits, and large files.
- `onelag watch` bounded foreground recorder with start, stop, status, mark, and report commands.
- Windows x64 self-contained publish and PowerShell installer bundle.
- GitHub Actions release workflow for test, publish, package, and release artifacts.

Still roadmap work:

- Native tray app and GUI.
- Signed MSI/EXE installer.
- Deeper Windows-only validation for performance counters, event logs, Files On-Demand states, WPR/WPA, and ProcMon escalation runbooks.
- Automated remediation plan execution with confirmation and rollback notes.
- Coverage reporting and broader integration tests on real Windows 11 systems.

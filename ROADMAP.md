# Roadmap

Live tracker: [GitHub milestones](https://github.com/marctjones/onelag/milestones) and implementation issues [#1](https://github.com/marctjones/onelag/issues/1) through [#60](https://github.com/marctjones/onelag/issues/60). Treat GitHub milestones and issues as the implementation source of truth; keep this file synchronized when tracker scope changes.

Current release status: the latest installable preview implements the core .NET solution, `scan`, fuller OneDrive known-issue detection, OneDrive client-cache health metadata, Windows system-pressure sampling with PDH/process/memory/power signals when available, recent Event Viewer summary correlation when available, WPR/WPA and ProcMon escalation-plan generation, supported reset dry-run/explicit execution, bounded foreground `watch`, local macOS validation, Windows CI smoke validation, release packaging, and a Windows x64 PowerShell installer bundle. It does not complete every roadmap issue; native tray/GUI, a signed MSI/EXE installer, deeper WPR/WPA and ProcMon trace execution/validation, broader remediation execution, and broad Windows 11 integration coverage remain open.

## v0.1 - Documentation And Scanner Foundation

Tracker: [v0.1 milestone](https://github.com/marctjones/onelag/milestone/1), issues [#1](https://github.com/marctjones/onelag/issues/1)-[#6](https://github.com/marctjones/onelag/issues/6).

- Publish the source PDF and design docs.
- Create .NET CLI skeleton.
- Implement `scan --root`.
- Emit Markdown and JSON reports.
- Add tests for process priority setup, cancellation, and report writing.

## v0.2 - Safe OneDrive Inventory

Tracker: [v0.2 milestone](https://github.com/marctjones/onelag/milestone/2), issues [#7](https://github.com/marctjones/onelag/issues/7)-[#11](https://github.com/marctjones/onelag/issues/11) and [#35](https://github.com/marctjones/onelag/issues/35).

- Discover personal and work/school OneDrive roots.
- Add streaming file and directory inventory.
- Detect high-risk development directories.
- Detect static sync blockers beyond item count, including invalid names, long paths, hidden files, temp files, large archive/media/mail files, and unsupported reparse points.
- Add bounded-memory large-tree tests.
- Add report redaction.

## v0.3 - Telemetry Correlation

Tracker: [v0.3 milestone](https://github.com/marctjones/onelag/milestone/3), issues [#12](https://github.com/marctjones/onelag/issues/12)-[#16](https://github.com/marctjones/onelag/issues/16), [#33](https://github.com/marctjones/onelag/issues/33), [#34](https://github.com/marctjones/onelag/issues/34), [#38](https://github.com/marctjones/onelag/issues/38), and [#39](https://github.com/marctjones/onelag/issues/39).

- Sample OneDrive CPU and memory.
- Capture a low-cost whole-system pressure snapshot.
- Sample disk queue counters where available.
- Correlate recent Event Viewer evidence.
- Estimate OneDrive log churn.
- Add unknown/degraded evidence handling.
- Correlate current pressure with static folder risk.
- Add WPR/WPA and ProcMon escalation runbooks for cases that require expert tracing.

## v0.4 - Recommendation Engine

Tracker: [v0.4 milestone](https://github.com/marctjones/onelag/milestone/4), issues [#17](https://github.com/marctjones/onelag/issues/17)-[#21](https://github.com/marctjones/onelag/issues/21), [#36](https://github.com/marctjones/onelag/issues/36), and [#37](https://github.com/marctjones/onelag/issues/37).

- Add configurable threshold policy.
- Add default `300,000` item guidance.
- Add documented public-preview `1,000,000` item profile checks.
- Add differential diagnosis for `OneDrive likely`, `OneDrive possible`, `OneDrive not proven`, and `non-OneDrive pressure suspected`.
- Rank findings by user impact and confidence.
- Emit official-remediation guidance for pause, Files On-Demand, selective sync, and reset.
- Recommend Microsoft Support and Recovery Assistant for work/school sync repair cases.

## v0.5 - Remediation Planning

Tracker: [v0.5 milestone](https://github.com/marctjones/onelag/milestone/5), issues [#22](https://github.com/marctjones/onelag/issues/22)-[#26](https://github.com/marctjones/onelag/issues/26).

- Generate dry-run PowerShell move plans.
- Add destination free-space checks.
- Add explicit confirmation flow for executing generated moves.
- Add rollback and verification instructions.
- Keep OneDrive reset and process kill as manual emergency actions.

## v0.6 - Release Hardening

Tracker: [v0.6 milestone](https://github.com/marctjones/onelag/milestone/6), issues [#27](https://github.com/marctjones/onelag/issues/27)-[#32](https://github.com/marctjones/onelag/issues/32).

- Add GitHub Actions build and test.
- Add packaged Windows artifacts.
- Add sample reports.
- Add privacy and support-bundle docs.
- Publish `v0.1.0` once the scanner is useful and safe.

## v0.7 - Responsiveness Watch Mode

Tracker: [v0.7 milestone](https://github.com/marctjones/onelag/milestone/7), issues [#40](https://github.com/marctjones/onelag/issues/40)-[#49](https://github.com/marctjones/onelag/issues/49).

- Design the opt-in watch-mode privacy model, data-retention limits, and resource budget.
- Add `onelag watch` lifecycle commands for start, stop, status, mark, and report.
- Persist bounded ring-buffer telemetry without unbounded disk growth.
- Sample system pressure, foreground-safe context, event-log summaries, and safe log-file metadata.
- Detect responsiveness stalls with timer-jitter evidence and manual lag markers.
- Generate episode timeline reports with direct evidence and inferred categories.
- Hand off inconclusive episodes to Event Viewer, WPR/WPA, ProcMon, driver/update review, or OneDrive remediation guidance.
- Validate all-day overhead and reliability on Windows 11.

## v0.8 - Interactive And Native Interfaces

Tracker: [v0.8 milestone](https://github.com/marctjones/onelag/milestone/8), issues [#50](https://github.com/marctjones/onelag/issues/50)-[#60](https://github.com/marctjones/onelag/issues/60).

- Define UI-neutral service contracts shared by CLI, guided console, tray, and GUI.
- Add a guided interactive console flow without committing to a full TUI.
- Record a TUI-versus-guided-console decision.
- Add local report and episode timeline viewing.
- Add an optional tray controller for bounded watch mode.
- Scaffold a native Windows GUI shell after a framework decision.
- Build scan/watch dashboard, lag-marker UX, privacy/export controls, and accessibility validation.
- Package CLI, GUI, and tray artifacts with explicit startup opt-in.

## Later

- Optional Microsoft 365 admin sync report integration.
- Optional automated WPR/ProcMon support-bundle workflow after the manual runbooks, watch-mode evidence, and privacy rules are proven.
- Optional cloud-side Microsoft Graph inventory.
- Optional scheduled scans, only after the one-shot scanner and watch mode have measured overhead and clear opt-in controls.

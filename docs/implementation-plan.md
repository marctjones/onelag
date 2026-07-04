# Implementation Plan

## Phase 0 - Repository And Design Baseline

Status: in progress.

Deliverables:

- Source PDF retained in the repository.
- Research-backed problem statement.
- Architecture document.
- Roadmap.
- Agent instructions and development best-practice documentation.
- GitHub issue and pull request templates.
- Initial public GitHub repository.
- Live GitHub milestones and issues for the implementation roadmap.

Exit criteria:

- Documentation explains what OneLag is, what it is not, and why the chosen design fits the OneDrive lag problem.

## Phase 1 - Scanner Skeleton

Goal: create a working .NET CLI that can run safely on Windows and emit a minimal report.

Tasks:

- Create solution and CLI project.
- Add command parsing for `scan`, `report`, and `version`.
- Set process priority to `Idle` at startup on Windows.
- Implement cancellation and timeout handling.
- Add JSON and Markdown report writers.
- Add test project and CI.

Acceptance:

- `onelag scan --root C:\SomeFolder --dry-run` emits a report without modifying files.
- Tests pass on local Windows and CI.

## Phase 2 - OneDrive Root Discovery

Goal: find likely OneDrive sync roots without requiring administrator rights.

Tasks:

- Read common OneDrive environment variables.
- Inspect common user-profile locations.
- Add optional user-specified roots.
- Add confidence scoring and clear reporting when root discovery is uncertain.
- Add redaction rules for report paths.

Acceptance:

- The scanner can identify personal and work/school roots when available.
- The user can override roots explicitly.
- Reports do not leak unrelated local paths by default.

## Phase 3 - Streaming Filesystem Inventory

Goal: identify item-count and high-churn folder risks with bounded memory.

Tasks:

- Implement streaming directory traversal.
- Track item counts, directory counts, max depth, write-time ranges, and inaccessible paths.
- Detect high-risk dev/build folders.
- Add early-stop caps for subtrees already above action thresholds.
- Add synthetic large-tree tests.

Acceptance:

- Large synthetic trees do not cause memory spikes from bulk arrays.
- High-risk directories are reported with confidence and recommended action.

## Phase 4 - Live Telemetry Correlation

Goal: correlate static folder risk with current OneDrive, whole-system, event-log, and disk pressure.

Tasks:

- Collect OneDrive process CPU and memory samples.
- Capture a low-cost whole-system pressure snapshot.
- Collect disk queue counters when available.
- Count OneDrive log-file churn by timestamp.
- Query recent Application, System, and OneDrive-relevant event logs when available.
- Degrade gracefully when counters or log paths are unavailable.
- Add consecutive-sample rules to reduce false positives.

Acceptance:

- Reports distinguish "risky tree found" from "risky tree plus current pressure".
- Reports classify evidence as `OneDrive likely`, `OneDrive possible`, `OneDrive not proven`, or `non-OneDrive pressure suspected`.
- Missing counters are recorded as unknown, not failure.
- Event-log access failures are recorded as unknown, not failure.

## Phase 5 - Risk Engine And Recommendations

Goal: turn raw observations into practical next actions.

Tasks:

- Implement threshold policy with default `300,000` item guidance.
- Add configurable public-preview `1,000,000` item profile checks.
- Score risks by item count, churn, path validity, staleness, hidden/temp/large-file blockers, CPU, disk queue, log churn, event evidence, and whole-system pressure.
- Generate action plans with confidence and rationale.
- Recommend Microsoft Support and Recovery Assistant for work/school account or sync-client repair cases.
- Recommend Event Viewer, ProcMon, or WPR/WPA escalation when OneDrive causality is not established.

Acceptance:

- The top report section names the most likely cause and the safest first action.
- Recommendations cite whether they came from direct evidence, heuristic inference, or official OneDrive guidance.
- Reports avoid folder-move recommendations when the evidence points to broader non-OneDrive pressure.

## Phase 6 - Remediation Script Generation

Goal: produce safe, reviewable scripts for the user.

Tasks:

- Generate dry-run PowerShell move plans.
- Verify destination free space before execution.
- Generate pause-sync and reset instructions, not automatic sync manipulation by default.
- Add explicit `--execute` mode behind confirmation prompts.
- Add rollback notes for every generated move.

Acceptance:

- Default scripts do not mutate files.
- Executable scripts require explicit confirmation and print a final review prompt.

## Phase 7 - Packaging And First Release

Goal: make OneLag easy to install and trust.

Tasks:

- Add signed or checksumed release artifacts.
- Add GitHub Actions for build, test, and release.
- Add sample reports.
- Add privacy and safety documentation.
- Tag `v0.1.0`.

Acceptance:

- A user can download a release, run a scan, and understand the output without building from source.

## Open Technical Questions

- Whether to use `System.Diagnostics.PerformanceCounter` directly, PowerShell `Get-Counter`, or PDH bindings as the primary counter backend.
- Whether root discovery should depend on registry reads or keep registry access optional.
- Whether high-risk development directories should be counted fully or summarized with capped estimates by default.
- Whether OneDrive log churn can be used reliably without relying on undocumented log formats.
- Which event logs and providers are useful across personal and work/school OneDrive installs without requiring administrator rights.
- Whether WPR support should remain documentation-only for the first release or include optional command generation behind explicit confirmation.
- Whether ProcMon support should be limited to filter templates and docs or include an optional collection workflow later.

## Tracker Source Of Truth

GitHub milestones and issues are the implementation tracker. Keep this document and `ROADMAP.md` aligned with the live tracker when scope changes.

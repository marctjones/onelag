# Agent Instructions

These instructions apply to all Codex, Claude, and other AI-agent work in this repository.

## Product Guardrails

OneLag is a Windows diagnostic and remediation tool for OneDrive-induced lag. Treat system responsiveness, data safety, privacy, and user trust as primary requirements.

The default product shape is a one-shot Windows CLI. Do not add a resident service, tray monitor, GUI, automatic file mover, OneDrive reset, or process-kill behavior unless a GitHub issue explicitly calls for it and the implementation includes safety review, tests, and documentation.

## Required Workflow

- Start each task by checking the relevant GitHub issue, local roadmap docs, and current worktree.
- Keep local docs and GitHub tracker state aligned when scope, milestones, or acceptance criteria change.
- Prefer small pull requests tied to one issue or one tightly related issue cluster.
- Do not commit generated logs, reports, binaries, traces, user-specific paths, secrets, or local OneDrive data.
- Do not make destructive file-system or OneDrive changes in code paths unless the command requires explicit confirmation and has tests covering dry-run behavior.
- All commands that mutate user data must default to dry-run.

## Architecture Rules

- Keep scanning, telemetry, risk scoring, remediation planning, reporting, and UI in separate layers.
- Keep Windows-specific API calls behind interfaces so core risk and report logic can be unit-tested on non-Windows CI.
- Use streaming filesystem enumeration. Never use recursive APIs that materialize entire large directory trees into arrays.
- Set process priority to `Idle` before expensive scan work on Windows.
- Preserve cancellation support throughout filesystem scans and telemetry sampling.
- Treat unavailable counters, inaccessible paths, missing OneDrive roots, and permission failures as reportable degraded evidence, not routine crashes.
- Do not parse or write undocumented OneDrive database internals.
- Redact user-specific paths in reports by default unless a user explicitly requests full paths.
- Do not claim OneDrive is the cause unless static inventory, live telemetry, event evidence, or user-supplied symptoms support that conclusion. Use `OneDrive possible`, `OneDrive not proven`, or `non-OneDrive pressure suspected` when evidence is mixed.
- Event-log reads, WPR guidance, ProcMon guidance, and support-bundle workflows must be bounded, privacy-aware, and read-only unless an issue explicitly requests confirmed capture or export behavior.
- Do not automate clean boot, service disablement, Windows Search disablement, Defender disablement, startup-item disablement, Event Viewer log clearing, WPR capture, or ProcMon capture in default scan paths.

## .NET Project Rules

- Target the current supported .NET LTS unless a GitHub issue records a reason not to. As of 2026-07-04, that is .NET 10.
- Use SDK-style projects and keep shared MSBuild settings in `Directory.Build.props`.
- Enable nullable reference types, implicit usings, deterministic builds, latest code-analysis mode, and warnings-as-errors for repository code.
- Use Central Package Management once third-party packages are added.
- Keep package dependencies minimal and justify each one in the issue or pull request.
- Add analyzers and formatting gates before feature code grows.

## Test Coverage Rules

Every implementation issue must include tests unless the issue is documentation-only. If tests are impossible or deferred, document the reason in the PR and create a follow-up issue.

Minimum expectations:

- Unit tests for threshold policy, risk scoring, report redaction, root discovery parsing, remediation plan generation, and command-line validation.
- Integration tests for synthetic large directory trees, inaccessible directories, long paths, cancellation, and counter-unavailable fallback.
- Windows validation for OneDrive process telemetry, performance counters, Files On-Demand states, and reset/pause guidance.
- Coverage reporting in CI with line and branch coverage tracked. Do not reduce meaningful coverage without an explicit issue note.
- High-risk safety code, including move planning and `--execute` confirmation, needs branch coverage for dry-run, confirmation denied, partial failure, and rollback notes.

## UX And UI Rules

- The CLI is the primary UX until the scanner is proven safe and useful.
- Console output must be calm, short, and action-oriented: diagnosis, confidence, evidence, next step.
- Reports must distinguish observed evidence from heuristic inference.
- Do not present destructive commands as the first recommendation unless the report classifies the case as emergency recovery.
- If a GUI is added later, use native Windows UI guidance, support keyboard and screen-reader use, support high contrast and text scaling, and avoid hiding safety-critical details behind decorative UI.

## GitHub Management Rules

- Each milestone must have issues with concrete acceptance criteria.
- Every non-trivial PR must link an issue with a closing keyword when it completes that work.
- Labels should communicate type, area, priority, quality gate, and whether Windows validation is required.
- Keep milestone scope focused. Split large work instead of creating omnibus PRs.
- CI must run build, format/analyzer checks, tests, and coverage on pull requests before branch protection is enabled.

## Documentation Rules

- Update `README.md` only for user-facing behavior and current status.
- Update `ROADMAP.md` when milestone order, scope, or release gates change.
- Update `docs/development-best-practices.md` when researched engineering rules change.
- Update `docs/research-validation.md` when OneDrive, Windows, or .NET platform assumptions change.

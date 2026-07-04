# Development Best Practices

Research date: 2026-07-04.

This document turns current Windows, .NET, and GitHub guidance into repository rules for implementing OneLag.

## Baseline Platform

Use .NET 10 LTS for new implementation work unless a GitHub issue records a compatibility reason to target something else. Microsoft's current .NET support policy lists .NET 10 as LTS, released 2025-11-11 and supported until 2028-11-14. .NET 8 is still supported until 2026-11-10, but it is already in maintenance support.

Use SDK-style projects. Microsoft documents SDK-style projects as the modern .NET project model, with SDK targets/tasks handling compile, pack, publish, implicit item includes, and simpler project files.

Use `global.json` once the project is scaffolded so local and CI builds use an intentional SDK feature band. Keep it updated with Dependabot or an explicit upgrade issue.

Baseline repo files once code starts:

- `global.json`
- `Directory.Build.props`
- `Directory.Packages.props`
- `.editorconfig`
- `src/OneLag.Cli`
- `src/OneLag.Core`
- `src/OneLag.Windows`
- `tests/OneLag.Core.Tests`
- `tests/OneLag.Cli.Tests`
- `tests/OneLag.Windows.Tests`

## Project Setup Rules

Recommended project split:

- `OneLag.Core`: pure policy, scoring, report, and remediation-plan logic. No direct Windows API calls.
- `OneLag.Windows`: OneDrive root discovery, process telemetry, counters, filesystem adapters, and Windows-specific probes.
- `OneLag.Cli`: command-line parsing, console rendering, process priority setup, cancellation, and report output.
- Future `OneLag.Gui`: native Windows App SDK/WinUI 3 wrapper only after CLI behavior is proven.

Required build settings:

- Nullable reference types enabled.
- Implicit usings enabled.
- Deterministic builds enabled.
- Treat warnings as errors for repo projects.
- Latest recommended .NET analyzers enabled.
- Repository-wide formatting via `.editorconfig`.
- CI runs `dotnet format --verify-no-changes`, `dotnet build`, `dotnet test`, and coverage.

Prefer framework-dependent development builds and self-contained release artifacts for users who do not have the right runtime installed. If self-contained artifacts are used, track runtime patching responsibility in release docs.

## System Design Rules

OneLag must stay safe on machines that are already slow.

Required design decisions:

- Run as a one-shot scan by default.
- Set the process priority class to `Idle` before scanning.
- Use streaming enumeration and bounded summaries for directory walks.
- Keep cancellation responsive.
- Treat unavailable telemetry as unknown evidence, not a crash.
- Treat mixed evidence as mixed evidence. The product must support `OneDrive likely`, `OneDrive possible`, `OneDrive not proven`, and `non-OneDrive pressure suspected` outcomes.
- Avoid background polling unless a future issue explicitly adds an opt-in scheduler.
- Keep all remediation actions separate from diagnosis.

Layer boundaries:

- Scanner produces observations.
- Risk engine produces findings.
- Planner produces actions.
- Report writer renders findings and actions.
- Executor is optional and must require explicit confirmation.

Never write to undocumented OneDrive state. Use official user-facing remediation guidance for pause, selective sync, Files On-Demand, and reset.

Do not automate disruptive Windows troubleshooting steps in ordinary scans. Clean boot, service disablement, Windows Search disablement, Defender disablement, startup-item disablement, WPR capture, and ProcMon capture are escalation material only.

Watch mode rules:

- Watch mode is opt-in, bounded, and off by default.
- Persist structured samples in a ring buffer, not unbounded text logs.
- Enforce duration, retention, disk size, and write-rate limits.
- Do not capture keystrokes, mouse coordinates, screenshots, clipboard contents, raw document text, raw browser URLs, or raw meeting titles.
- Foreground context, paths, event messages, and account data are redacted by default.
- Watch reports must separate observed stalls from inferred causes.

## UX And UI Rules

The MVP UX is a CLI plus Markdown/JSON reports.

CLI rules:

- Default command must be dry-run and non-mutating.
- Console output should show top finding, confidence, evidence, and safest next action.
- Avoid noisy progress unless the scan runs long; long progress must be cancelable.
- Use explicit severity words: `info`, `warning`, `high-risk`, `emergency`.
- Always distinguish direct observations from heuristic inference.
- Give exact next steps, but do not run destructive steps automatically.

Report rules:

- Markdown report for humans.
- JSON report for automation and tests.
- Redact local user paths by default.
- Include scan start/end time, version, root confidence, inaccessible paths summary, counter availability, event-log availability, system-pressure classification, differential diagnosis, and recommendation confidence.

Future GUI rules:

- Use native Windows App SDK/WinUI 3 if a GUI is added.
- Follow Fluent/Windows design guidance for layout, controls, typography, and interaction.
- Support keyboard navigation, screen readers, high contrast, text scaling, and visible focus.
- Avoid static layouts that clip at different window sizes; use responsive XAML layout patterns.
- Keep remediation confirmation dialogs plain and explicit.
- Tray and GUI startup must be explicit opt-in and reversible.
- Watch controls must always show current recorder state, privacy mode, storage use, and stop control.

## Test Coverage Rules

Coverage is a quality gate, not a vanity metric. Use code coverage to reveal untested decision paths, especially safety behavior.

Initial coverage targets after code exists:

- `OneLag.Core`: at least 90% line coverage and 85% branch coverage.
- `OneLag.Cli`: at least 80% line coverage for command validation and rendering.
- `OneLag.Windows`: no fixed percentage until Windows CI strategy is in place, but all adapters must have unit-testable seams and manual Windows validation notes.
- Whole solution: start at 80% line coverage and ratchet upward rather than downward.

Required tests:

- Threshold policy around 200,000, 300,000, and public-preview 1,000,000 item policy.
- Risk score combinations for item count, dev directories, sync blockers, disk queue, CPU, event evidence, system pressure, log churn, and unknown evidence.
- Differential diagnosis classification for `OneDrive likely`, `OneDrive possible`, `OneDrive not proven`, and `non-OneDrive pressure suspected`.
- Report redaction and full-path opt-in.
- Streaming scanner cancellation and inaccessible directory behavior.
- Hidden, temporary, large-file, invalid-name, and long-path sync-blocker detection.
- Synthetic large-tree scan memory guard.
- Dry-run move plan generation.
- `--execute` confirmation denied, confirmation accepted, partial failure, and rollback-note paths.
- Counter-unavailable fallback.
- Event-log unavailable, missing-provider, access-denied, and redacted-message fallback.
- Watch ring-buffer retention, marker creation, timer-jitter classification, episode classification, and resource-budget behavior.
- UI-neutral service contract tests for scan, watch, report loading, progress, cancellation, and errors.
- CLI exit codes and invalid argument messages.

Manual Windows validation before release:

- Personal OneDrive root.
- Work or school OneDrive root if available.
- OneDrive active, paused, and reset states.
- Files On-Demand online-only and locally available files.
- Event-log access as a standard user.
- A high-pressure non-OneDrive workload to verify the report avoids blaming OneDrive when evidence does not support it.
- At least one large synthetic tree outside OneDrive and one inside a temporary OneDrive test root, if safe.
- All-day watch-mode run with CPU, memory, disk-write, disk-footprint, battery, and cancellation observations.
- Guided console, tray, and GUI validation when those interfaces are implemented.

## Development Workflow

Use issue-driven development:

1. Confirm the issue acceptance criteria.
2. Add or update tests first for policy-heavy and safety-heavy behavior.
3. Keep implementation scoped to the issue.
4. Run format, build, tests, and coverage locally when possible.
5. Update docs if behavior, safety boundaries, or user-visible output changes.
6. Link the PR to the issue with a closing keyword only when acceptance is complete.

Definition of done:

- Code implemented.
- Tests added or explicitly justified.
- Coverage did not regress.
- Docs updated.
- CI green.
- Manual Windows validation attached when the label `needs:windows-validation` is present.
- GitHub issue acceptance criteria checked off or explained.

## GitHub Management

Use milestones for release slices and issues for implementation tasks. GitHub documents milestones as a way to track progress across grouped issues and pull requests; use that instead of separate, drifting planning docs.

Use labels consistently:

- `type:feature`, `type:bug`, `type:docs`, `type:test`, `type:ci`, `type:research`
- `area:cli`, `area:scanner`, `area:telemetry`, `area:risk`, `area:remediation`, `area:ui`, `area:release`, `area:docs`
- `priority:p0`, `priority:p1`, `priority:p2`
- `quality:coverage`, `quality:safety`, `needs:windows-validation`

Use issue templates so reports contain expected behavior, observed behavior, validation evidence, and safety impact.

Use branch protection or repository rulesets once CI exists:

- Require the .NET CI workflow.
- Require pull requests before merging to `main`.
- Require conversation resolution.
- Require linear history if it matches the user's workflow.
- Restrict bypasses to emergency maintainer action.

Use Dependabot for NuGet and GitHub Actions once packages/workflows exist.

## Research Sources

- [.NET and .NET Core support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)
- [Select the .NET version to use](https://learn.microsoft.com/en-us/dotnet/core/versions/selection)
- [.NET project SDKs](https://learn.microsoft.com/en-us/dotnet/core/project-sdk/overview)
- [Code analysis in .NET](https://learn.microsoft.com/en-us/dotnet/fundamentals/code-analysis/overview)
- [Unit testing best practices for .NET](https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-best-practices)
- [Use code coverage for unit testing](https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-code-coverage)
- [dotnet test](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-test)
- [Design Windows apps overview](https://learn.microsoft.com/en-us/windows/apps/design/)
- [Windows App SDK](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/)
- [Quick start: WinUI 3 project](https://learn.microsoft.com/en-us/windows/apps/get-started/start-here)
- [Accessibility overview for Windows apps](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/accessibility-overview)
- [Responsive layouts](https://learn.microsoft.com/en-us/windows/apps/develop/ui/layouts-with-xaml)
- [About issues](https://docs.github.com/en/issues/tracking-your-work-with-issues/learning-about-issues/about-issues)
- [About milestones](https://docs.github.com/en/issues/using-labels-and-milestones-to-track-work/about-milestones)
- [Best practices for Projects](https://docs.github.com/en/issues/planning-and-tracking-with-projects/learning-about-projects/best-practices-for-projects)
- [Building and testing .NET with GitHub Actions](https://docs.github.com/en/actions/tutorials/build-and-test-code/net)
- [Secure use reference for GitHub Actions](https://docs.github.com/en/actions/reference/security/secure-use)
- [Configuring Dependabot version updates](https://docs.github.com/en/code-security/how-tos/secure-your-supply-chain/secure-your-dependencies/configure-version-updates)

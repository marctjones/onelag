# OneLag

OneLag is a planned Windows diagnostic and remediation utility for OneDrive-driven system lag. The project starts from the local source document, [OneDrive_Diagnostic_and_Remediation_Guide.pdf](OneDrive_Diagnostic_and_Remediation_Guide.pdf), which describes a failure mode where OneDrive sync load, very high item counts, shell extension blocking, and risky synced development folders can make Windows Explorer and the desktop feel frozen.

The working product is a low-impact, one-shot diagnostic CLI first. It should identify local OneDrive roots, measure current OneDrive, whole-system, event-log, and disk evidence, stream-count high-risk directory clusters without loading huge file lists into memory, and produce a practical remediation plan. It should not start as a resident background service.

After the one-shot scanner is proven, the roadmap adds an opt-in responsiveness watch mode for recurring keyboard, mouse, and UI freezes. That mode is planned as a bounded recorder with explicit start/stop controls, lag markers, privacy redaction, ring-buffer retention, and strict resource limits.

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

The repository currently contains the source PDF, design documentation, development guardrails, GitHub planning templates, and live milestones/issues [#1](https://github.com/marctjones/onelag/issues/1)-[#60](https://github.com/marctjones/onelag/issues/60). Implementation has not started yet.

# Interface Strategy

OneLag should keep diagnostic logic in UI-neutral application services. CLI, guided console, tray, and GUI surfaces should call shared services rather than duplicating scanning, watch, report parsing, or recommendation rules.

## Current Decision

Use a guided console and local report viewer before building a full TUI.

Reasons:

- The tool is safety-sensitive and benefits from explicit prompts, generated files, and copyable command output.
- A full terminal UI would add rendering and keyboard-navigation complexity without improving diagnostic accuracy.
- Report summaries and timeline items are now represented by `IReportViewService`, which can be reused by CLI, tray, and native GUI code.
- Native tray and GUI work should wait until scan/watch contracts are stable enough to avoid reimplementing business logic in the UI.

## Implemented Surface

- `onelag interactive` provides a guided console entry point for scan, watch, marking lag, reset-plan review, trace-plan generation, and saved-report viewing.
- `onelag view --report PATH [--timeline]` summarizes saved diagnostic and watch reports.
- Diagnostic JSON reports are parsed structurally.
- Diagnostic Markdown and watch Markdown reports are summarized using conservative section parsing.

## Native UI Direction

The next native interface should be a small Windows tray controller for watch status and "mark lag now" before a full dashboard. A future GUI should expose:

- Scan and watch status.
- Episode timeline.
- Privacy/export controls.
- Reset and remediation plans as reviewable files.
- Accessibility validation for keyboard use, high contrast, text scaling, and screen readers.

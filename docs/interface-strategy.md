# Interface Strategy

OneLag should keep diagnostic logic in UI-neutral application services. CLI, guided console, tray, and GUI surfaces should call shared services rather than duplicating scanning, watch, report parsing, or recommendation rules.

## Current Decision

Use a guided console and local report viewer before building a full TUI. For the native Windows app, use Windows Forms for the first tray/GUI surface.

Reasons:

- The tool is safety-sensitive and benefits from explicit prompts, generated files, and copyable command output.
- A full terminal UI would add rendering and keyboard-navigation complexity without improving diagnostic accuracy.
- Report summaries and timeline items are now represented by `IReportViewService`, which can be reused by CLI, tray, and native GUI code.
- Windows Forms keeps the first native UI small, local, and packageable with the CLI while scan/watch/remediation behavior remains in shared services.

## Implemented Surface

- `onelag interactive` provides a guided console entry point for scan, watch, marking lag, reset-plan review, trace-plan generation, and saved-report viewing.
- `onelag view --report PATH [--timeline]` summarizes saved diagnostic and watch reports.
- `onelag-gui.exe` provides a native Windows tray icon and dashboard tabs for scan, watch, report viewing, and guarded remediation.
- Diagnostic JSON reports are parsed structurally.
- Diagnostic Markdown and watch Markdown reports are summarized using conservative section parsing.

## Native UI Direction

The first native interface is a Windows tray controller plus dashboard. Future GUI work should deepen:

- Accessibility validation for keyboard use, high contrast, text scaling, and screen readers.
- More detailed episode timeline filtering.
- Files On-Demand and OneDrive account-state validation.
- Signed installer integration and optional startup registration with explicit opt-in.

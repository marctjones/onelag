# Windows Evidence Matrix

OneLag should collect enough local evidence for a human or offline Codex/Claude review to distinguish OneDrive pressure from broader Windows 11 responsiveness issues. It should not silently run invasive tracing or parse private OneDrive databases.

## Implemented Lightweight Evidence

- OneDrive root discovery and bounded inventory.
- Known OneDrive sync issue detection.
- OneDrive process CPU, memory, version, and log-churn metadata.
- OneDrive settings/log store metadata without reading contents.
- Files On-Demand attribute sampling for existing OneDrive roots.
- Whole-system CPU, disk, memory, paging, power, and top-process pressure snapshots where Windows exposes counters.
- Recent Event Viewer summaries from System, Application, Windows Update, Defender, and Driver Frameworks channels when available.
- Foreground process name during watch samples without capturing keystrokes, screenshots, window titles, document text, or clipboard data.
- WPR/WPA and ProcMon runbooks for expert escalation without starting traces automatically.
- Offline support bundles for Codex/Claude Code analysis.

## Evidence Not Collected By Default

- Raw OneDrive logs.
- OneDrive DAT, cache, or database contents.
- Raw Event Viewer exports.
- WPR ETL traces or ProcMon PML captures.
- Screenshots, document contents, browser history, clipboard data, mouse coordinates, or keystrokes.
- Tenant, SharePoint, or Microsoft 365 admin sync reports.

## Deeper Validation Still Needed

- Confirm Files On-Demand attribute interpretation on real Windows 11 personal and work/school OneDrive roots.
- Validate the event-channel list on consumer and managed Windows 11 laptops.
- Run WPR/WPA traces during real freezes and confirm the generated runbooks produce useful CPU, disk, file I/O, hard-fault, ready-thread, and driver/DPC evidence.
- Run short ProcMon captures only after WPR or OneLag evidence points toward file-system or registry churn.
- Compare OneLag watch episodes with Resource Monitor, Event Viewer, WPR/WPA, and user-observed lag markers from the same time window.

## Analysis Rules

- Treat OneDrive as likely only when static folder risk aligns with live process, disk, log, event, or watch evidence.
- Treat Files On-Demand counts as context, not proof of causality.
- Escalate to WPR/WPA when user-mode evidence suggests scheduling delay, hard faults, driver/DPC behavior, or unexplained foreground stalls.
- Prefer reversible, documented remediation before destructive or global system changes.

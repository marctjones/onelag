# Architecture

OneLag should be built as a safe local diagnostic first, then extended into remediation and GUI workflows only after the scanner proves reliable.

## Target Platform

- Windows 10 and Windows 11.
- .NET console application.
- No administrator requirement for ordinary scan mode.
- Optional elevated mode only for explicitly confirmed actions that require it.

## Execution Model

The default command is a one-shot scan:

```powershell
onelag scan --output .\onelag-report.json --format markdown
```

On startup, the process should:

1. Set its own process priority to `Idle`.
2. Register cancellation handlers for Ctrl+C.
3. Detect OneDrive roots and accounts.
4. Capture a low-cost system-pressure snapshot.
5. Sample OneDrive and disk telemetry before walking file trees.
6. Stream filesystem metadata with bounded memory.
7. Correlate recent event-log evidence when available.
8. Stop early when risk thresholds are already exceeded.
9. Emit a report and recommended next actions.

Later versions may add an opt-in watch command for recurring responsiveness problems:

```powershell
onelag watch start --duration 8h --buffer 30m
onelag watch mark
onelag watch report
```

Watch mode must be explicitly started, bounded by duration and storage limits, and safe to stop at any time. It is a recorder for evidence around transient freezes, not a resident optimizer.

## Components

### Root Discovery

Inputs:

- `OneDrive`, `OneDriveCommercial`, and `OneDriveConsumer` environment variables when present.
- Known user-profile OneDrive folder names.
- Registry-backed OneDrive account data when available.
- User-specified roots via `--root`.

Output:

- A normalized list of candidate sync roots.
- Account label when known.
- Confidence level for each root.

Root discovery should not assume every folder named `OneDrive` is safe to mutate.

### Telemetry Collector

Telemetry to collect:

- OneDrive process presence, PID, CPU sample, memory, and path.
- Physical or logical disk queue sample where counters are available.
- OneDrive log directory file creation rate, counted by file timestamps without parsing undocumented log contents.
- Operating system version and storage type when available.
- OneDrive client version when available.

The collector should treat unavailable counters as degraded evidence, not a fatal error.

### System Pressure Snapshot

The scanner needs enough non-OneDrive context to avoid blaming OneDrive whenever the machine is slow.

Signals:

- Total CPU pressure.
- Available memory and paging pressure when available.
- Disk free space for the OneDrive volume and planned destination volume.
- Disk queue or disk active-time counters when available.
- Storage media class where available, especially HDD versus SSD for public-preview item-count eligibility.
- Top process names by CPU or I/O only when the platform API supports a low-cost read.

The snapshot should classify pressure as:

- `onedrive-dominated`: OneDrive process pressure aligns with disk or event evidence.
- `mixed`: OneDrive is active but other system pressure is also significant.
- `not-onedrive-dominated`: the machine is under pressure but OneDrive is not the leading signal.
- `unknown`: counters or permissions were insufficient.

The report must make this classification visible before any remediation recommendation.

### Event Log Correlator

The correlator should perform a bounded, read-only query over recent events.

Initial logs:

- `Application`.
- `System`.
- OneDrive-relevant Applications and Services logs when present.

Behavior:

- Query a recent time window, defaulting to the scan window plus a small lookback.
- Use reverse chronological reads and a maximum event count.
- Summarize provider, level, event ID, timestamp, and count.
- Redact or omit message text by default because event messages may contain user paths or account details.
- Treat access-denied, missing logs, and query errors as degraded evidence.

Event-log evidence should increase confidence only when it aligns with static inventory or live telemetry. It should not be treated as proof of a specific root cause by itself.

### Responsiveness Watcher

The watcher is an optional post-MVP component for daily keyboard, mouse, and UI stalls that may not be reconstructable from logs after the fact.

Signals:

- Periodic system-pressure samples.
- Responsiveness canary samples such as timer jitter and scheduling delay.
- Foreground process identity with privacy-safe redaction.
- Manual "lag happened now" markers from CLI, tray, or GUI.
- Event-log summaries near marked or detected episodes.
- Safe log-file metadata such as file count, modified time, and churn rate.

The watcher must not capture keystrokes, mouse coordinates, screenshots, clipboard contents, document text, raw browser URLs, raw meeting titles, or raw log contents by default.

Storage model:

- Structured ring buffer.
- Maximum age, disk size, and write-rate limits.
- Redaction before persistence where possible.
- Reports saved outside OneDrive by default when possible.

Episode categories:

- `storage pressure`.
- `cpu starvation`.
- `memory paging`.
- `driver-or-dpc-suspected`.
- `foreground-app-blocked`.
- `onedrive-possible`.
- `unknown`.

Driver/DPC suspicion from user-mode samples is only a hypothesis. WPR/WPA is required to prove DPC/ISR behavior.

### Interface Surfaces

The diagnostic core should stay UI-neutral.

Supported interface progression:

- CLI for automation and testability.
- Guided console for safer interactive use.
- Local report/timeline viewer for scan and watch outputs.
- Optional tray controller for watch status and "mark lag now".
- Native Windows GUI only after scan/watch services are stable.

All interfaces must call shared application services. No diagnostic rules should live only in GUI or tray code.

### Streaming Filesystem Scanner

The scanner must never call APIs that materialize entire recursive file arrays for large roots.

Required behavior:

- Use streaming enumeration.
- Use `EnumerationOptions.IgnoreInaccessible = true`.
- Keep bounded memory summaries per directory.
- Track item counts, max depth, recent-write density, oldest and newest `LastWriteTime`, and extension/type patterns.
- Detect high-risk directory names such as `.git`, `node_modules`, `.venv`, `venv`, `bin`, `obj`, `target`, `.gradle`, `.next`, `dist`, and `build`.
- Detect likely sync blockers such as hidden-file clusters, `.tmp` and temp-file clusters, large archive/media/mail data files, invalid OneDrive names, long paths, and unsupported reparse points.
- Early-stop subtree counting after configurable caps where the exact count would not change the recommendation.
- Preserve errors as report findings instead of aborting the scan.

### Risk Engine

The risk engine combines independent signals:

- Item-count pressure against the supported OneDrive limit policy.
- High-churn development directories inside OneDrive.
- Hidden, temporary, very large, invalid-name, and long-path sync blockers.
- Current OneDrive CPU pressure.
- Disk queue pressure.
- OneDrive log churn.
- Recent event-log evidence.
- Whole-system pressure classification.
- Long paths and invalid OneDrive names.
- Stale large clusters that are safe candidates for archive or relocation.

The report should separate:

- Evidence observed directly.
- Heuristics inferred from evidence.
- Differential diagnosis: `OneDrive likely`, `OneDrive possible`, `OneDrive not proven`, or `non-OneDrive pressure suspected`.
- Recommended action.
- Confidence level.

### Remediation Planner

The planner generates actions, not silent mutations.

Action types:

- `observe`: no action yet; include reason.
- `pause-sync`: user should pause OneDrive before moving a cluster.
- `move-out-of-onedrive`: generate a dry-run move plan to an unsynced base such as `C:\LocalDev`.
- `free-up-space`: recommend Files On-Demand for storage pressure, not for metadata count pressure.
- `selective-sync`: recommend choosing fewer synced folders.
- `reset-onedrive`: emergency or post-cleanup repair, with warning that sync connections may need reconfiguration.
- `kill-onedrive`: emergency-only command, never automatic.
- `support-and-recovery-assistant`: recommend Microsoft's tool for work/school account or sync-client repair cases.
- `escalate-to-event-viewer`: show a bounded manual event-log review path when the scanner cannot query logs.
- `escalate-to-procmon-or-wpr`: when counters show pressure but folder evidence is inconclusive.

Generated scripts should default to dry-run.

### Report Writer

Formats:

- Human-readable Markdown.
- Structured JSON.
- Optional compact console summary.

The report should be suitable for issue attachments without exposing unnecessary personal data. Paths should be redacted by default outside the scanned roots unless the user passes `--full-paths`.

## Threshold Policy

Initial policy:

- Total synced items below `200,000`: informational unless other signals are high.
- `200,000` to `300,000`: warning zone.
- Above `300,000`: high risk unless the user explicitly enables a documented `1,000,000` item public-preview profile.
- Public-preview profile requires documented platform and hardware checks; otherwise stay with the default limit.
- High-churn development folders are high risk even below `300,000`.
- Disk queue, CPU, and log churn thresholds from the source PDF are treated as heuristics requiring consecutive samples.

The policy should be data-driven in a config file so Microsoft limit changes can be updated without code changes.

## Safety Rules

- No destructive action without explicit confirmation.
- No default administrator elevation.
- No service installation in MVP.
- No undocumented OneDrive database writes.
- No log-content upload.
- No automatic WPR, ProcMon, clean boot, service disablement, Windows Search disablement, Defender disablement, startup-item disablement, or Event Viewer log clearing.
- No automatic watch mode, tray startup, or GUI background launch without explicit opt-in.
- No keylogging, screenshot capture, clipboard capture, raw document capture, or raw input-event capture.
- No assumption that cloud deletion is reversible from the local Recycle Bin.
- All move plans must verify destination free space before execution.

## Testing Strategy

Unit tests:

- Root discovery parsing.
- Threshold policy.
- Risk scoring.
- Differential diagnosis classification.
- Event-log query result mapping.
- Report redaction.
- Remediation script generation.

Integration tests:

- Synthetic directory trees with hundreds of thousands of entries.
- Inaccessible directory handling.
- Long path handling.
- Hidden and temporary sync-blocker detection.
- High-risk development folder detection.
- Counter-unavailable fallback.
- Event-log unavailable and access-denied fallback.

Performance tests:

- Memory remains bounded during large tree scans.
- Idle priority is set before scanning.
- Scanner can cancel promptly.
- Subtree early-stop prevents unnecessary work once thresholds are exceeded.
- Event-log correlation respects time and event-count bounds.

Manual Windows validation:

- Personal OneDrive account.
- Work or school OneDrive account.
- Files On-Demand enabled and disabled.
- OneDrive paused, active, and reset states.
- HDD and SSD machines if available.

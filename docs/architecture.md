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
4. Sample low-cost telemetry before walking file trees.
5. Stream filesystem metadata with bounded memory.
6. Stop early when risk thresholds are already exceeded.
7. Emit a report and recommended next actions.

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

### Streaming Filesystem Scanner

The scanner must never call APIs that materialize entire recursive file arrays for large roots.

Required behavior:

- Use streaming enumeration.
- Use `EnumerationOptions.IgnoreInaccessible = true`.
- Keep bounded memory summaries per directory.
- Track item counts, max depth, recent-write density, oldest and newest `LastWriteTime`, and extension/type patterns.
- Detect high-risk directory names such as `.git`, `node_modules`, `.venv`, `venv`, `bin`, `obj`, `target`, `.gradle`, `.next`, `dist`, and `build`.
- Early-stop subtree counting after configurable caps where the exact count would not change the recommendation.
- Preserve errors as report findings instead of aborting the scan.

### Risk Engine

The risk engine combines independent signals:

- Item-count pressure against the supported OneDrive limit policy.
- High-churn development directories inside OneDrive.
- Current OneDrive CPU pressure.
- Disk queue pressure.
- OneDrive log churn.
- Long paths and invalid OneDrive names.
- Stale large clusters that are safe candidates for archive or relocation.

The report should separate:

- Evidence observed directly.
- Heuristics inferred from evidence.
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
- No assumption that cloud deletion is reversible from the local Recycle Bin.
- All move plans must verify destination free space before execution.

## Testing Strategy

Unit tests:

- Root discovery parsing.
- Threshold policy.
- Risk scoring.
- Report redaction.
- Remediation script generation.

Integration tests:

- Synthetic directory trees with hundreds of thousands of entries.
- Inaccessible directory handling.
- Long path handling.
- High-risk development folder detection.
- Counter-unavailable fallback.

Performance tests:

- Memory remains bounded during large tree scans.
- Idle priority is set before scanning.
- Scanner can cancel promptly.
- Subtree early-stop prevents unnecessary work once thresholds are exceeded.

Manual Windows validation:

- Personal OneDrive account.
- Work or school OneDrive account.
- Files On-Demand enabled and disabled.
- OneDrive paused, active, and reset states.
- HDD and SSD machines if available.

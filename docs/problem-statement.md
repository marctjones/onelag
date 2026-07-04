# Problem Statement

OneLag targets a specific Windows reliability problem: OneDrive sync activity can make the machine feel frozen when the sync engine, local file metadata, Explorer shell extensions, and storage queues interact badly.

The source PDF describes two root causes:

- Storage saturation: OneDrive can drive the disk subsystem into sustained high active time or queue depth while hashing, indexing, and syncing large or complex file trees.
- Explorer coupling: OneDrive integrates with File Explorer through shell extensions and status overlays, so a stalled sync status query can make Explorer, the desktop, and taskbar appear blocked.

The problem is not just "too many gigabytes." It is often a metadata and file-count problem: many small files, high-change directories, source control metadata, package folders, virtual environments, and build outputs create sync pressure that is disproportionate to their byte size.

## Evidence From The Source PDF

The PDF names these diagnostic vectors:

- Physical disk queue length greater than `2.5`.
- Normalized OneDrive CPU greater than `80%`.
- More than `5` OneDrive log files generated per minute under `%LocalAppData%\Microsoft\OneDrive\logs`.
- More than `300,000` tracked metadata nodes.

It also gives implementation directives:

- Do not use bulk recursive APIs such as `Directory.GetFiles()` for large tree counts.
- Do not place volatile development directories such as `node_modules`, `.git`, virtual environments, `bin`, or `obj` inside OneDrive.
- Do not create a continuous background monitor as the first solution.
- Do run the diagnostic process at `Idle` priority.
- Do stream metadata using enumeration APIs.
- Do prefer `LastWriteTime` over `LastAccessTime` for archival and staleness rules.
- Do move heavy directory clusters into unsynced local paths such as `C:\LocalDev`.

## User Problem

The user does not need another generic cleaner. They need a tool that can run when the system is already stressed and produce a short, high-confidence explanation:

1. Whether OneDrive is implicated.
2. Which local folders are most likely causing the issue.
3. Which action is safest and most likely to help.
4. Which built-in Microsoft remediation step should be used, if any.

The tool must also be able to say when OneDrive is not proven. On a Windows 11 laptop, similar lag can come from broader system pressure such as Windows Search, Defender, Windows Update, browser caches, build tools, storage pressure, or another process generating disk I/O. OneLag should strengthen or weaken the OneDrive hypothesis with live telemetry, event evidence, and folder inventory rather than treating any slow machine with a OneDrive folder as a OneDrive failure.

## Success Criteria

OneLag succeeds when it can:

- Complete a scan of large OneDrive trees without large memory spikes.
- Avoid making UI lag worse while it is diagnosing.
- Rank risky synced folders by item count, churn indicators, and known high-write development patterns.
- Detect common sync blockers beyond item count, including hidden files, temporary files, invalid names, long paths, and large archive/media/mail files.
- Distinguish `OneDrive likely`, `OneDrive possible`, `OneDrive not proven`, and `non-OneDrive pressure suspected`.
- Distinguish "observe", "warn", "move out of OneDrive", "pause sync", and "reset OneDrive" recommendations.
- Generate a human-readable report plus optional dry-run PowerShell commands.
- Require explicit confirmation before any process kill, OneDrive reset, or file move.

# OneLag Diagnostic Report

- Started: `2026-07-04T12:00:00.0000000+00:00`
- Finished: `2026-07-04T12:00:21.0000000+00:00`
- Diagnosis: `OneDrivePossible`
- Telemetry: `available`
- System pressure: `mixed; disk queue elevated; memory normal`
- CPU: `normal`
- Memory: `normal`
- Disk: `elevated`
- Power: `ac`
- OneDrive client health: `available; reset worth considering after inventory cleanup`

## Roots
- `<root:1>` (environment, high, work/school)

## Inventory
### `<root:1>`

- Files: `312,450`
- Directories: `41,280`
- Total bytes: `95,420,100,000`
- Max depth: `18`
- Capped: `False`
- Inaccessible paths: `2`
- High-risk directories: `3`
- Sync blockers: `19`

- Top-level item: `<root:1>\Documents` files `284,120`, directories `38,900`, bytes `71,000,000,000`
- Top-level item: `<root:1>\Pictures` files `28,330`, directories `2,380`, bytes `24,420,100,000`
- Known issue `temporary-file`: `8` item(s)
- Known issue `desktop-ini-metadata`: `7` item(s)
- Known issue `long-local-sync-path`: `4` item(s)
- High-risk directory: `<root:1>\Documents\project\node_modules` - High-churn development dependency cache inside OneDrive.
- High-risk directory: `<root:1>\Documents\project\.git` - Source-control metadata changes frequently and contains many small files.
- High-risk directory: `<root:1>\Documents\project\bin` - Build output inside OneDrive can create avoidable sync churn.
- Warning blocker `temporary-file`: `<root:1>\Documents\project\scratch.tmp` - Temporary TMP files are not synced to OneDrive and can leave sync pending evidence.
- HighRisk blocker `long-local-sync-path`: `<root:1>\Documents\project\deep\...\file.txt` - OneDrive root plus relative path over 520 characters is not supported for synced PC paths.

## OneDrive Client Health
- Internal sync database parsed: `False`
- Reset command candidates: `1`
- **Warning** `dat-cache-newer-than-settings`: OneDrive DAT cache changed frequently during the scan window. Safety: Do not edit DAT files directly; use supported reset only after reviewing visible sync state.
- Reset candidate `LocalAppData`: `<redacted>\OneDrive.exe /reset`

## System Performance
- `cpu-total`: `24.0` `%` (available)
- `memory-available`: `10,240.0` `MB` (available)
- `physical-disk-current-queue`: `5.3` `count` (available)
- `onedrive-log-churn`: `7.0` `files/min` (available)

Top sampled processes:
- `OneDrive` PID `4242` CPU `18.4%` working set `450,000,000` bytes
- `SearchIndexer` PID `2112` CPU `9.1%` working set `210,000,000` bytes

## Recent Windows Events
- `System` `Disk` event `153` `Warning` count `2` newest `2026-07-04T11:58:10.0000000+00:00`
- `Application` `Application Hang` event `1002` `Error` count `1` newest `2026-07-04T11:56:44.0000000+00:00`

## Findings
- **HighRisk**: Synced item count is above Microsoft's optimum-performance guidance. Total items are over 300,000. Confidence: `high`.
- **HighRisk**: Development folders are inside OneDrive. Dependency caches, source metadata, and build outputs can create heavy sync churn. Confidence: `high`.
- **Warning**: Current pressure is mixed. OneDrive is active, but disk and indexing evidence mean OneDrive is not proven as the only cause. Confidence: `medium`.

## Recommendations
- **Pause OneDrive before moving or reorganizing risky folders** (`PauseSync`): Use the official OneDrive tray menu before large local moves. Safety: OneLag does not pause sync automatically.
- **Move development and build-output folders out of OneDrive** (`MoveOutOfOneDrive`): Generate and review a dry-run move plan before moving high-churn folders. Safety: Generated move scripts require explicit execution flags.
- **Use Event Viewer, WPR/WPA, or ProcMon if freezes continue** (`EscalateToProcmonOrWpr`): The scan suggests mixed system pressure and may need trace evidence. Safety: Trace files can contain private paths and process names.

## OneDrive Processes
- `OneDrive` PID `4242` working set `450,000,000` bytes CPU time `00:18:33.1200000` sampled CPU `18.4%`

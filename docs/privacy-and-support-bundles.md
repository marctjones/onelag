# Privacy And Support Bundles

OneLag reports are designed to be shareable only after review. The default scanner redacts root paths in Markdown reports, but support artifacts can still reveal sensitive details such as process names, timestamps, OneDrive account type, folder names inside the scanned root, event provider names, or trace file metadata.

## Safe To Include By Default

- Redacted Markdown diagnostic reports.
- Redacted watch reports.
- `onelag support bundle` output after local review.
- Generated trace-plan README and scripts.
- Coverage summaries from CI.
- OneLag version and release tag.

## Review Before Sharing

- JSON reports, because they may preserve exact paths depending on command options.
- Reports generated with `--full-paths`.
- ProcMon or WPR trace outputs.
- Event Viewer exports.
- Screenshots of OneDrive, Explorer, taskbar, or Office apps.
- Work/school tenant, account, library, or SharePoint site names.

## Do Not Include Without Explicit Need

- Raw OneDrive log contents.
- OneDrive DAT/cache/database files.
- Browser history, clipboard data, document text, screenshots, or keystroke data.
- Files copied from synced business folders.
- Dumps, ETL traces, or ProcMon PML files that have not been reviewed.

## Support Bundle Shape

Use the automated bundle command when you want to move evidence to another machine for Codex, Claude Code, a GitHub issue, Microsoft support, or internal IT review:

```powershell
onelag support bundle --report onelag-report.md --report onelag-watch-report.md --output onelag-support-bundle --zip
```

The generated bundle includes:

1. Copied diagnostic and watch reports that you explicitly pass with `--report`.
2. A generated watch report when you pass `--watch-output`.
3. Report summaries derived from the copied reports.
4. `manifest.json` with bundle format, report list, file sizes, and SHA-256 hashes.
5. `ANALYZE_WITH_CODEX_OR_CLAUDE.md`, a ready-to-use offline analysis prompt.
6. `privacy-checklist.md`, `user-notes.md`, and `environment.md`.
7. Generated trace-plan runbooks when you pass `--include-trace-plan`.

The command does not include raw OneDrive logs, OneDrive DAT/cache/database files, raw Event Viewer exports, WPR ETL traces, ProcMon PML captures, screenshots, document contents, keystrokes, clipboard data, or browser history.

If you need to create a manual bundle, include:

1. The redacted diagnostic report.
2. The redacted watch report for the affected day.
3. The generated trace-plan folder, if deeper investigation was needed.
4. A short note with the exact local time of the lag episode and what app was in the foreground.
5. A statement of whether OneDrive was paused, syncing, processing changes, or showing an error in the tray UI.

Keep the bundle local unless you intentionally attach it to a GitHub issue, Microsoft support case, or internal IT ticket.

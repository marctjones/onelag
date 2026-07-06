# Changelog

## Unreleased

- Added GUI support-bundle export for offline Codex or Claude Code analysis.
- Added Files On-Demand attribute metadata sampling for OneDrive roots without opening file contents.
- Expanded read-only Windows event summary coverage to selected operational channels for Windows Update, Defender, and Driver Frameworks.
- Added release-hardening and Windows evidence-matrix documentation.
- Added support-bundle smoke coverage to CI, release, and Windows validation workflows.
- Configured `main` branch protection requiring macOS and Windows CI plus review/conversation gates.

## 0.1.0-preview.10

- Added `onelag support bundle` for offline Codex or Claude Code analysis without embedding a local AI runtime.
- Added native Windows tray/GUI packaging alongside the CLI.
- Added direct remediation move, verify, and rollback commands guarded by explicit confirmation.
- Added bounded watch-mode recording, marker capture, episode timeline reports, and local report viewing.
- Added fuller OneDrive known-issue detection, client-cache metadata checks, Windows system-pressure snapshots, Event Viewer summaries, and WPR/WPA plus ProcMon trace-plan generation.
- Added Windows installer ZIP release assets with SHA-256 checksums.

## Release Readiness

The current public release remains a preview. The next non-preview `v0.1.0` should wait for the release-hardening gates in [release hardening](docs/release-hardening.md), especially real Windows 11 laptop validation.

# Roadmap

## v0.1 - Documentation And Scanner Foundation

- Publish the source PDF and design docs.
- Create .NET CLI skeleton.
- Implement `scan --root`.
- Emit Markdown and JSON reports.
- Add tests for process priority setup, cancellation, and report writing.

## v0.2 - Safe OneDrive Inventory

- Discover personal and work/school OneDrive roots.
- Add streaming file and directory inventory.
- Detect high-risk development directories.
- Add bounded-memory large-tree tests.
- Add report redaction.

## v0.3 - Telemetry Correlation

- Sample OneDrive CPU and memory.
- Sample disk queue counters where available.
- Estimate OneDrive log churn.
- Add unknown/degraded evidence handling.
- Correlate current pressure with static folder risk.

## v0.4 - Recommendation Engine

- Add configurable threshold policy.
- Add default `300,000` item guidance.
- Add documented public-preview `1,000,000` item profile checks.
- Rank findings by user impact and confidence.
- Emit official-remediation guidance for pause, Files On-Demand, selective sync, and reset.

## v0.5 - Remediation Planning

- Generate dry-run PowerShell move plans.
- Add destination free-space checks.
- Add explicit confirmation flow for executing generated moves.
- Add rollback and verification instructions.
- Keep OneDrive reset and process kill as manual emergency actions.

## v0.6 - Release Hardening

- Add GitHub Actions build and test.
- Add packaged Windows artifacts.
- Add sample reports.
- Add privacy and support-bundle docs.
- Publish `v0.1.0` once the scanner is useful and safe.

## Later

- Optional GUI wrapper.
- Optional Microsoft 365 admin sync report integration.
- Optional WPR/ProcMon escalation bundle workflow.
- Optional cloud-side Microsoft Graph inventory.
- Optional scheduled scans, only after the one-shot scanner has measured overhead and clear opt-in controls.

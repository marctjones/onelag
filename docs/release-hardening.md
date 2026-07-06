# Release Hardening

OneLag is installable and useful as a preview, but the non-preview `v0.1.0` gate should stay stricter than "builds and publishes".

## Current Decision

Keep shipping preview releases until the real Windows 11 laptop validation path passes. Hosted Windows CI is valuable, but the hosted runner can be Windows Server and cannot prove tray behavior, Defender/SmartScreen behavior, OneDrive client behavior, or daily laptop responsiveness.

## Non-Preview Gate

Before tagging non-preview `v0.1.0`:

1. Run the Windows 11 validation workflow on a self-hosted Windows 11 laptop runner.
2. Install the latest release ZIP on a separate Windows 11 laptop and validate `onelag.exe` plus `onelag-gui.exe`.
3. Confirm the GUI support-bundle flow creates a ZIP containing the analysis prompt, manifest, reports, summaries, user notes, privacy checklist, and optional trace-plan runbooks.
4. Run an all-day watch-mode session and record CPU, memory, disk writes, output size, and battery observations.
5. Validate GUI scaling, keyboard navigation, high contrast, and common display scaling settings.
6. Confirm branch protection or repository rules require CI before merging to `main`.
7. Confirm release notes, checksum assets, installer README, and changelog match the shipped artifact.

## Repository Rules Plan

Status on 2026-07-06: `main` branch protection is configured to require `test-macos-latest` and `test-windows-latest`, require branches to be up to date, require one approving review, require conversation resolution, and block force-pushes and deletion.

The recommended `main` protection is:

- Require pull requests before merging.
- Require status checks before merge.
- Require both CI matrix jobs: `test-macos-latest` and `test-windows-latest`.
- Require branches to be up to date before merging.
- Require conversation resolution.
- Do not require signed commits until installer signing and contributor workflow are settled.

If repository rules are unavailable for the current account or plan, keep this as a documented release gate and enforce it manually before non-preview releases.

## Preview Release Gate

Preview tags may ship when:

- Local `dotnet build`, `dotnet test`, `dotnet format`, coverage, and `scripts/test-local.sh` pass.
- GitHub CI passes on macOS and Windows.
- The release workflow builds, smokes, uploads, and publishes the Windows installer ZIP and checksum.
- New diagnostic or remediation behavior is either covered by tests or explicitly marked as Windows-validation pending.

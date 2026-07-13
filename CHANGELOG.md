# Changelog

## Unreleased

### Differential redesign

OneDrive is no longer the only hypothesis the tool can hold. See [differential design](docs/differential-design.md).

- Added `HypothesisEngine`, which ranks ten candidate causes of desktop lag against the same evidence and records what argues for and against each one, with a specific next step per cause.
- Added a live-evidence gate: static folder shape (item count, high-churn directories, sync blockers) can no longer promote OneDrive past `possible`. Reaching `likely` requires a live signal. This fixes reports that reached `OneDrivePossible` on `desktop.ini` hits alone, with no live telemetry.
- Added `EvidenceQualityAssessor`, which grades every capture `Complete`, `Partial`, or `Insufficient`, lists exactly what was missing, and states it above the verdict instead of in a footnote.
- Added DPC and interrupt counters, including per-core maximums read across every processor instance, so a driver storm pinning a single core is not averaged away by the `_Total` instance.
- Added `HostContext`: display topology via `QueryDisplayConfig` (including DisplayLink-class indirect/USB displays), Bluetooth radio and connected-device state, power source, wired-network state, and a derived dock state.
- Added `ShellResponsiveness`, which measures Explorer message-pump latency directly instead of inferring shell blocking from folder shape.
- Added `WatchContextCorrelation`, which reports lag episodes per hour grouped by hardware configuration, so lag that tracks the dock, the external displays, or the Bluetooth radio is visible as such.
- Added `DisplayOrDockSuspected`, `InputOrBluetoothSuspected`, and `ShellBlocked` watch-episode categories, and reordered categorization so a kernel-level stall is not attributed to the foreground app it happens to be starving.
- Sync-restriction findings and rename guidance now surface as sync hygiene regardless of the lag diagnosis, rather than being gated behind an OneDrive verdict they should never have driven.

### Driver attribution and the docked/undocked experiment

- Added `onelag trace dpc`: a bounded kernel ETW session that records every DPC and ISR, maps the routine address into the loaded kernel image that contains it, and reports drivers by the time they spent at high IRQL. The counters could prove *a* driver was stalling the machine; this names it. Elevation-gated and explicitly invoked, so it is never part of the default scan.
- Added `DriverClassifier`, which maps a driver image to the subsystem that owns it (DisplayLink-class USB graphics, GPU, Thunderbolt/USB4, Bluetooth stack, Wi-Fi 2.4 GHz coexistence, storage, cloud-files filter, Defender filter), so attributed DPC time strengthens the hypothesis it belongs to instead of sitting in a table as trivia.
- Added `scan --trace-drivers <duration>` to fold a driver trace into a full diagnostic report.
- Added `onelag compare --session A --session B`, which pools watch sessions recorded in different hardware configurations and reports lag episodes per hour for each. This is the docked-versus-undocked experiment, made from recorded evidence rather than memory.
- Bluetooth devices are now enumerated through the PnP device tree, which sees Bluetooth LE peripherals. The Bluetooth API's own enumeration cannot, and most modern mice and keyboards are LE.
- The GUI reports evidence quality and ranked causes after a scan, and has a driver-trace button.
- The support-bundle analysis prompt now instructs offline review to lead with evidence quality, to check for live OneDrive evidence before accepting folder shape as a cause, to check the per-core DPC signal, to check the configuration correlation, and not to blame OneDrive merely because the bundle came from a tool named OneLag.

### Fixed

- Watch-mode timer drift was measured against a running schedule, so the samplers' own cost (PDH and process sampling each hold a window open) was folded into drift and accumulated. Every sample after the first read as a lag episode with an ever-growing stall, which would have made an all-day watch session produce nothing but false positives. Drift now measures the overshoot of each individual sleep.
- PDH counter values were accepted only when `CStatus` equalled zero. `CStatus` is severity-coded, and rate counters — which is every counter OneLag samples — are documented to return `PDH_CSTATUS_NEW_DATA` (1) when the raw value advanced between collections. The equality test discarded exactly the samples that carried data. Success is now tested by severity.
- Laptop panels that report `DISPLAYPORT_EMBEDDED` or `LVDS` rather than `INTERNAL` were counted as external displays, which would report an undocked laptop as docked.
- Connected Bluetooth devices are now reported as unknown rather than zero when no classic devices are found, because `BluetoothFindFirstDevice` cannot enumerate Bluetooth LE devices and most modern mice and keyboards are LE. A confident zero would have wrongly weakened the Bluetooth hypothesis.

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

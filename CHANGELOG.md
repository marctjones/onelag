# Changelog

## Unreleased

### Stop silently overwriting previous captures

Diagnosing lag is a comparison exercise: a docked day against an undocked day, a capture before a fix against
one after it, commit growth on Monday against commit growth on Friday. Every one of those needs both captures
to still exist. The default output names used to be fixed — `onelag-report.md`, `onelag-watch-report.md`,
`onelag-comparison.md`, `onelag-driver-trace.md`, `onelag-trace-plan`, `onelag-support-bundle`,
`onelag-move-plan` — so a second run silently destroyed the evidence the first run existed to produce, and the
user found out only when they went looking for it.

- Added `OutputPaths.Timestamped`, and every command that writes a report, trace, or plan now defaults to a
  timestamped name (`onelag-report-20260714-175230.md`) instead of a fixed one, so repeated runs accumulate
  rather than collide. `scan`'s default extension still follows `--format` (`.json` stays `.json`), and
  `freeze`'s pre-existing timestamped default now goes through the same implementation instead of its own.
- Added `OutputPaths.EnsureWritable` and wired it into every command above plus `trace dpc` and `compare`: an
  explicit `--output`/`--report` that already exists is now refused with an actionable message and a non-zero
  exit rather than silently overwritten. Pass the new `--overwrite` flag (same name and semantics as the
  existing `collect`/`support bundle` flag) when overwriting is actually what you want.
- Left `watch start --output DIR` and `compare --session DIR` alone on purpose: those name a session directory
  that `watch mark`, `watch stop`, and `watch report` refer to by name across a whole run, and timestamping or
  overwrite-guarding them would break that workflow outright.

### Catching the freeze instead of describing the calm

Every capture this project had ever taken was recorded while the machine was fine. That is why nothing was
ever diagnosed: `scan` walks a folder tree and measures exposure, `watch` had to be armed before an episode
and marked during one, and marking a freeze requires the machine to respond at the exact moment it will not.

- Added `onelag freeze`, a one-shot composite capture for the moment the machine locks up. It takes no
  inventory and walks no directory, so it returns while the symptom is still happening: commit accounting,
  the filter-driver stack, the shell message pump, the file namespace a dialog would land in, and an optional
  kernel trace. It prints a TL;DR to the console, because a user mid-freeze will not open a file.
- **`onelag watch` now detects freezes on its own, and auto-capture is on by default.** Asking a user to tag
  a freeze is asking them to act at the one moment they cannot. `FreezeDetector` watches the sampler's own
  starvation — the loop asks to sleep one second, and if it wakes four seconds later, the machine stalled —
  plus shell-pump latency, commit collapse, and hard-fault rate. On detection it writes a marker naming the
  signal that tripped and takes a deep capture, debounced and capped so one thirty-second freeze produces one
  episode rather than thirty, and so an all-day run on a badly broken machine cannot fill the disk. Captures
  dropped to the cap are stated in the report rather than silently omitted.
- Added `MemoryTrendAnalyzer`, which turns an all-day watch into a leak hunt. It ranks processes **by growth
  rate, not by size** — the leaker is the thing that grows, so a large flat process is innocent and a small
  climbing one is guilty. It also tracks *unaccounted* commit over time: when every process stays flat and
  commit keeps climbing, the leak is in the kernel, held by a driver, and no process list will ever show it.

### Collectors the tool was missing

Three hypotheses could never win, because nothing collected the evidence they needed.

- Added `CaptureFilterDriverStack` (`fltmc`). `SecurityOrSearchScanner` previously looked only at Defender and
  Windows Search **CPU**, which made it structurally incapable of seeing the expensive case: a machine
  carrying a large third-party endpoint-security stack. Those filters cost almost nothing in CPU — they cost
  *latency*, synchronously, on every file open. An open-file dialog performs one open per file just to draw an
  icon, so the bill lands on Explorer and file dialogs while the rest of the desktop looks fine. A real machine
  running eleven third-party kernel drivers, six of them file-system minifilters, with Defender's own filter
  passive, scored zero.
- Added `CaptureMemoryPressure`: commit headroom in bytes, kernel paged/non-paged pool, system uptime, top
  processes by **private bytes** (not working set — a leaked page trimmed out to the page file still consumes
  commit but vanishes from working set, which is precisely what the old sampler measured), the leak candidates
  **Windows itself names** via RADAR and the Resource-Exhaustion resolver, and **commit that no user-mode
  process accounts for** — the number that decides whether a leak lives in an application or in a driver, and
  the one Task Manager cannot show you at all.
- Added `CaptureShellExtensions` (icon overlay handlers run synchronously on the Explorer UI thread, and
  Windows honours only the first ~15) and `CaptureFileSystemContext` (Known Folder Move — every native
  open-file dialog defaults to Documents, so a Documents folder redirected into OneDrive makes every dialog
  enumerate a cloud-backed tree through the Cloud Files filter and the whole security stack — plus dead mapped
  drives, checked under a hard timeout because a dead UNC path blocking for thirty seconds *is* the bug).
- Added `memory-page-reads-per-second`, the hard-fault rate. This is the direct measurement of "the UI thread
  is blocked waiting on the page file", which is what a user is describing when their clicks do nothing and
  then all replay at once.

### Fixes

- **`MemoryPaging` scored zero at 96% commit.** Its gates were absolute — available memory had to fall below
  1024 MB and commit had to reach 90% — so 1.5 GB of headroom on a 16 GB laptop scored nothing. Scoring is now
  based on headroom in bytes, headroom as a fraction of the machine, the hard-fault rate, and whether Windows
  has already named a leaking process.
- **"OneDrive was not running" could be false, and it gates the entire OneDrive hypothesis.** A real capture
  reported it while OneDrive wrote a `.odl` file two seconds later: the process-name match had failed and
  nothing cross-checked it. Log churn now vetoes a failed process match in the hypothesis engine, the evidence
  assessor, and the client-health signal — if OneDrive's log store is being written to, the sync engine is
  running, whatever the process enumeration concluded.

### Refusing to waste a working day

- `onelag selftest` now exercises the four new probes and reports, for each degraded one, **which hypothesis
  becomes untestable** and how to fix it. Elevation is reported as a first-class line, because it is the single
  thing that most determines whether a capture is worth anything.
- **`onelag watch start` now refuses to begin an all-day run with degraded collectors** unless you pass
  `--i-understand-collectors-are-degraded`. `fltmc` and the kernel trace need an elevated terminal; an
  eight-hour session that silently collects nothing is worse than no session at all. `onelag freeze` warns and
  continues instead — a freeze capture is opportunistic, and partial evidence captured now beats perfect
  evidence never captured.

### Remediation

- Added `onelag remediate reclaim-memory`. `StartMenuExperienceHost` was observed holding **2.7 GB** on a real
  machine, against a normal footprint of about 100 MB; Windows relaunches it instantly and clean when killed,
  with no data loss and no elevation. Dry-run by default, `--execute` plus `--i-understand-this-restarts-the-shell`
  to act, and a hard-coded allowlist that cannot be extended from the command line — a diagnostic tool must not
  become an arbitrary process killer.

### GUI and tray parity

- The GUI now exposes the full feature set: a **Diagnose** tab that runs the self test, a **Collect Logs** tab for the raw log bundle, and a **Compare** tab for docked-versus-undocked session comparison, alongside the existing scan, watch, report, support, and remediation tabs.
- Added a startup readiness banner: the GUI runs the self test on launch and shows, at a glance, whether the probes are measuring live data — so a non-CLI user finds out immediately if a watch session would be empty, rather than after recording one.
- The tray menu now covers the whole workflow without a terminal: Self Test, Start/Stop Watch, Mark Lag Now, and Collect Logs.

### Raw log collection

- Added `onelag collect`, which pulls the actual log files off the machine into one bundle so analysis runs over real bytes instead of guessing at what is relevant: every OneDrive `.odl`, the `.log` and `.etl` files under the Windows tree (CBS, DISM, Panther, setupapi, storage, driver setup), crash dumps and live kernel reports, driver and system inventory, and the recent event logs. Events are exported per channel as rendered XML (with message text) over a configurable window via `wevtutil`; `--all-channels` exports every channel rather than the broad default set.
- The bundle is raw and unredacted by design, and ships a `PRIVACY.txt`, a complete `manifest.json` with a SHA-256 per file, a `README.md`, and an analysis prompt, so its contents can be reviewed before it goes anywhere. For a redacted, curated summary, `onelag support bundle` remains the right tool.
- Collection is bounded by per-file, total-size, and file-count caps that record what they dropped rather than failing or ballooning; the Windows tree is full of multi-gigabyte and locked logs, and busy files are copied through a shared read handle so the ones held open by their writer are not lost.

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

### Testing the Windows layer from a macOS dev machine

See [testing strategy](docs/testing-strategy.md). The measurement layer had zero test coverage: a green run on
the dev machine meant nothing had executed, not that anything worked.

- Added `InteropLayoutTests`, which pins every interop struct against its native contract using `Marshal.SizeOf` and `Marshal.OffsetOf` — managed metadata that resolves identically off Windows. Offsets are asserted, not just sizes: swapping two `uint` fields leaves the struct the same size while making OneLag read a rotation value as a display output technology.
- Added `WindowsEventFixtures`, a faithful model of how Windows renders events, including the variation that breaks parsers: classic providers carrying only a `Guid` or an `EventSourceName`, `EventID` with a `Qualifiers` attribute, missing `Level`, missing `TimeCreated`, malformed XML. `WindowsEventEvidenceTests` carries those events into the differential, so a display-driver reset is proven to move the display-and-dock hypothesis rather than merely to parse.
- Fixed a real gap this surfaced: a classic provider rendered with only a GUID was reported as `unknown`, making it invisible to every hypothesis that matches on provider name.
- Extracted `OneDriveLogStore` with an injectable log root and clock. OneDrive's `.odl` files are an undocumented binary format that this project deliberately never parses; the churn signal it does use is pure file metadata, and is now tested against a synthetic log store. A missing log store is reported as *unmeasured* rather than as *quiet*, and files stamped in the future after a clock change no longer count as churn.
- Extracted `KernelModuleMap` from the ETW session, so driver attribution — which loaded kernel image contains this routine address — is testable without running a kernel trace, and now uses a binary search rather than a linear scan.
- Added `FakePlatformProbe` and `ScanPipelineTests`, giving `ScanRunner` its first coverage: a thrashing OneDrive is blamed on OneDrive, a pinned CPU core with a DisplayLink monitor is blamed on the dock, and a capture where every collector degraded produces an Insufficient report rather than a verdict.
- Added `WindowsProbeIntegrationTests`, gated by `[WindowsFact]` so they skip on macOS rather than silently passing. They assert probes returned *live* data, not merely that they did not throw.
- Added `onelag selftest`, which runs every probe once and reports which ones measured anything. A watch session recorded with degraded collectors produces an authoritative-looking report containing nothing, and costs a working day to discover.

### Fixed

- Driver names arrive from Windows as full paths, and `Path.GetFileName` splits on the *host* separator, so off Windows it treated the backslashes as ordinary characters and returned the entire path as the driver name. `DriverClassifier` would then have matched nothing and reported every driver as unclassified. Caught by the new cross-platform tests.

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

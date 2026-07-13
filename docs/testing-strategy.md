# Testing Strategy

OneLag diagnoses Windows, and it is developed on macOS. That is a real hazard, not a footnote: a green test
run on the dev machine can mean the Windows measurement layer works, or it can mean none of it ran. The suite
is organised so those two outcomes can never be confused.

## The Three Tiers

### Tier 1 — Interpretation, tested with fakes (runs anywhere)

Everything OneLag learns about a machine arrives through `IPlatformProbe`. `FakePlatformProbe` scripts a
whole simulated Windows machine — telemetry, pressure counters, host context, shell state, driver trace — and
`ScanPipelineTests` drives it through discovery, inventory, ranking, and report rendering. This proves that
*when* the probes read Windows correctly, the right conclusion follows: a thrashing OneDrive is blamed on
OneDrive, a pinned CPU core with a DisplayLink monitor is blamed on the dock, and a capture where every
collector degraded produces an "Insufficient evidence" report rather than a verdict.

Two Windows subsystems are modelled directly rather than mocked away:

**The event log.** Windows renders events as XML against a published, stable schema, so it can be reproduced
faithfully. `WindowsEventFixtures` models the providers this diagnosis actually turns on — display driver
resets (Display/4101), disk I/O retries, Bluetooth transport errors, WHEA, Kernel-Power, Defender — and,
more importantly, the variation that quietly breaks parsers: manifest providers that carry a `Name`, classic
providers that carry only a `Guid` or an `EventSourceName`, an `EventID` with a `Qualifiers` attribute, a
missing `Level`, a missing `TimeCreated`, malformed XML. `WindowsEventEvidenceTests` then carries those
events all the way into the differential, so what is under test is not "the parser reads events" but "a
display driver reset moves the display-and-dock hypothesis".

**The OneDrive log store.** This is the honest answer to "can you model Windows' undocumented logs?" —
mostly no, and the tool is designed so it does not have to. OneDrive writes `.odl` / `.odlgz` / `.odlsent`
files whose contents are a binary, obfuscated, undocumented format, and parsing them is an explicit
non-goal. What the source guide actually relies on is *churn*: more than five log files written per minute
means the sync engine is thrashing. Churn is pure file metadata, so the log store is modelled exactly — the
right directory shape, the rotation extensions, controlled write times — and `OneDriveLogStoreTests` covers
the cases that matter: churn inside the window, rotated logs that must not count, a missing log store
reported as *unmeasured* rather than as *quiet*, files stamped in the future after a clock change, and a
pathological store that is capped and says so.

### Tier 2 — Memory layout, pinned by inspection (runs anywhere)

The interop structs are the highest-risk code in the project. A single wrong field order does not throw; it
reads garbage out of the operating system and produces a confident, wrong diagnosis. This code cannot be
*executed* off Windows — but its layout is managed metadata, and `Marshal.SizeOf` and `Marshal.OffsetOf`
resolve identically on macOS. `InteropLayoutTests` pins every struct against its native contract:
`DISPLAYCONFIG_PATH_INFO` at 72 bytes, `DISPLAYCONFIG_MODE_INFO` at 64 with its 48-byte union,
`DISPLAYCONFIG_TARGET_DEVICE_NAME` at 420, `BLUETOOTH_DEVICE_INFO` at 560, `PDH_FMT_COUNTERVALUE_ITEM` at 24.

Offsets are asserted, not only sizes, and that distinction is load-bearing. Swapping `OutputTechnology` and
`Rotation` in `DISPLAYCONFIG_PATH_TARGET_INFO` leaves the struct exactly the same size — a size-only check
sails past it — while making OneLag read a rotation value as an output technology and misclassify every
display on a dock. The offset assertion catches it on the dev machine.

`KernelModuleMap` exists for the same reason: driver attribution reduces to "which loaded kernel image
contains this routine address?", which is arithmetic over an address table. Extracted from the ETW session,
it is tested against the address ranges Windows actually reports — including that user-mode images are
rejected, that the rundown reporting a module twice does not duplicate it, and that an address in a gap
resolves to `unresolved` rather than being attributed to an innocent neighbour.

The same split applies to log collection. `LogCollectionService` — staging, SHA-256, the per-file, total, and
count caps, path sanitisation, the manifest, and the zip — is platform-neutral and covered by
`LogCollectionServiceTests` with synthetic items on any OS. The Windows-specific part, `WindowsLogCollector`,
only *enumerates* sources (OneDrive logs, the Windows tree, `wevtutil` exports), and its real behaviour is a
Tier-3 check.

### Tier 3 — Real Windows (runs only on Windows)

Nothing above makes a single call into Windows. `WindowsProbeIntegrationTests` does, gated by
`[WindowsFact]` so it *skips* on macOS rather than silently passing. These run on the Windows CI runner and
on any real laptop, and they assert that a probe returned **live** data, not merely that it did not throw —
a probe that quietly degrades to `unavailable` would otherwise pass every check while measuring nothing,
which is precisely the failure this whole redesign exists to prevent.

They check that the PDH counters return values (a `CStatus` check that rejected `PDH_CSTATUS_NEW_DATA` would
null out every rate counter while every other test still passed), that the per-core DPC maximum is never
below the all-core average (a bad array stride routinely violates that), that `QueryDisplayConfig` returns a
self-consistent display set, that Bluetooth enumeration completes without issuing a radio inquiry, and that
the Explorer shell is genuinely probed.

CI also runs a real 10-second kernel ETW trace on the elevated Windows runner, which is the only place that
code path can execute. It fails the build if `KernelTraceControl.dll` was lost by single-file publish, or if
the session did not start.

## `onelag selftest`

Even a fully green suite cannot tell you that the probes work on *your* machine. `onelag selftest` runs every
probe once and prints which ones measured something:

```
[OK  ] performance-counters   10 of 12 counters returned a value
[OK  ] dpc-interrupt-counters DPC 1.2% all-core, 3.4% worst core
[OK  ] host-context           docked-likely; 2 display(s), 0 external, 1 indirect/USB; bluetooth on, 2 device(s) connected
[FAIL] explorer-shell         the shell could not be probed
```

Run it before recording a watch session. A session whose collectors were all degraded produces an
authoritative-looking report containing nothing, which is worse than no report — and it costs a working day
to discover.

## What This Still Cannot Prove

The dev machine can prove that struct layouts are right, that Windows event shapes are parsed, that log churn
is counted, and that the evidence leads to the right verdict. It cannot prove that `QueryDisplayConfig`
returns what the documentation says on a specific dock, that a specific Bluetooth stack enumerates the way
the PnP tree suggests, or that a specific laptop's DisplayLink driver shows up where expected. Only the
laptop can prove that, which is what `selftest` and the docked/undocked session are for.

# OneLag Watch Report

- Samples: `14,400`
- Markers: `2`
- First sample: `2026-07-04T08:00:00.0000000+00:00`
- Last sample: `2026-07-04T16:00:00.0000000+00:00`
- Max timer drift: `1,842.0 ms`

## Markers
- `2026-07-04T10:14:32.0000000+00:00` from `cli`: typing stalled in editor
- `2026-07-04T14:48:09.0000000+00:00` from `cli`: mouse clicks delayed

## Episodes
- `2026-07-04T10:14:30.0000000+00:00` to `2026-07-04T10:15:04.0000000+00:00` `OneDrivePossible` confidence `medium`: timer drift 1,842.0 ms, OneDrive CPU 22.0%, OneDrive log churn 9/min, foreground `devenv`
- `2026-07-04T14:48:08.0000000+00:00` to `2026-07-04T14:48:40.0000000+00:00` `StoragePressure` confidence `medium`: timer drift 1,104.0 ms, disk queue elevated, foreground `winword`
- `2026-07-04T15:31:12.0000000+00:00` to `2026-07-04T15:31:19.0000000+00:00` `Unknown` confidence `low`: timer drift 725.0 ms without matching user-mode pressure

## Largest Timer Delays
- `2026-07-04T10:14:33.0000000+00:00` drift `1,842.0 ms`, foreground `devenv`, telemetry `available`
- `2026-07-04T14:48:10.0000000+00:00` drift `1,104.0 ms`, foreground `winword`, telemetry `available`
- `2026-07-04T15:31:15.0000000+00:00` drift `725.0 ms`, foreground `explorer`, telemetry `available`

## Interpretation
Timer drift is a user-mode responsiveness canary. Episode categories are inferred from nearby user-mode samples. WPR/WPA is required to prove driver DPC/ISR root cause.

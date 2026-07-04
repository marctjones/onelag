# Research Validation

Research date: 2026-07-04.

This file records the external validation behind the design. Preference was given to Microsoft Support, Microsoft Learn, and Sysinternals documentation.

## Key Findings

Microsoft's current OneDrive restrictions page still recommends no more than `300,000` total synced items for optimum performance and warns that performance issues can occur above that count, even if not all items are being actively synced. The same page now says support for up to `1,000,000` items per sync instance is in public preview on Windows, with requirements including the latest OneDrive Insider client, Windows 11 or Windows Server 2022 public preview, SSD storage, at least 16 GB RAM, and a supported CPU. Devices outside those requirements remain on the existing `300,000` supported limit.

Source: [Restrictions and limitations in OneDrive and SharePoint](https://support.microsoft.com/en-US/onedrive/restrictions-and-limitations-in-onedrive-and-sharepoint)

Microsoft's own OneDrive performance advice includes using Files On-Demand, choosing only needed folders to sync, moving large files out of OneDrive, and pausing sync when needed. This supports OneLag's approach of recommending official controls instead of inventing a hidden repair path.

Sources:

- [Tips to improve OneDrive sync performance](https://support.microsoft.com/en-US/onedrive/tips-to-improve-onedrive-sync-performance)
- [Save disk space with OneDrive Files On-Demand for Windows](https://support.microsoft.com/en-US/onedrive/save-disk-space-with-onedrive-files-on-demand-for-windows)

Microsoft documents OneDrive reset as a sync repair step and states that reset causes OneDrive to rebuild its local DAT file. That makes reset a valid emergency recommendation, but not something OneLag should run silently.

Source: [Reset OneDrive](https://support.microsoft.com/en-US/onedrive/reset-onedrive)

Microsoft documents shell extension handlers as in-process COM DLLs and includes icon overlay handlers among shell extension types. This supports the PDF's model that sync status integrations can affect Explorer behavior, although OneLag should avoid claiming it can prove every Explorer hang from simple counters alone.

Source: [Creating Shell Extension Handlers](https://learn.microsoft.com/en-us/windows/win32/shell/handlers)

.NET documentation explicitly contrasts `Directory.EnumerateFiles()` with `Directory.GetFiles()`: enumeration can begin before the full collection is returned, while `GetFiles()` waits for the whole array. This validates the streaming scan requirement.

Source: [Directory.EnumerateFiles Method](https://learn.microsoft.com/en-us/dotnet/api/system.io.directory.enumeratefiles)

.NET exposes `ProcessPriorityClass.Idle`, where process threads run only when the system is idle and are preempted by higher-priority work. This validates the low-impact execution model.

Source: [ProcessPriorityClass Enum](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.processpriorityclass)

.NET `PerformanceCounter` can read existing Windows performance counters, but code must handle platform availability, permissions, disposal, and counters that require two samples. This validates using counters while keeping an alternate collector path for systems where counters are unavailable.

Source: [PerformanceCounter Class](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.performancecounter)

Windows documents NTFS `disablelastaccess` behavior and notes that disabling Last Access Time updates improves file and directory access speed and that updates can be deferred. This supports using `LastWriteTime` rather than `LastAccessTime` for staleness rules.

Source: [fsutil behavior](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/fsutil-behavior)

Microsoft 365 has a OneDrive sync health dashboard for administrators, but it requires Microsoft 365 roles, supported client versions, tenant setup, and device reporting. It is useful for managed fleets but does not replace a local personal-machine scanner.

Source: [OneDrive sync reports in the Apps Admin Center](https://learn.microsoft.com/en-us/sharepoint/sync-health)

Sysinternals Process Monitor and Windows Performance Recorder/Analyzer are stronger deep-diagnostic tools. They are better for expert root cause analysis, but they are not the safest first action on a machine already suffering sync-induced lag because they are capture-heavy, expert-oriented, and do not directly produce a folder migration plan.

Sources:

- [Process Monitor](https://learn.microsoft.com/en-us/sysinternals/downloads/procmon)
- [Windows Performance Recorder](https://learn.microsoft.com/en-us/windows-hardware/test/wpt/windows-performance-recorder)
- [Windows Performance Analyzer](https://learn.microsoft.com/en-us/windows-hardware/test/wpt/windows-performance-analyzer)

## Design Conclusions

The PDF's design is directionally sound, but the hard `300,000` rule needs nuance:

- Default supported performance target: warn as the total synced item count approaches `300,000`; high-risk above `300,000`.
- Public preview path: allow an explicit configuration profile for `1,000,000` item preview eligibility, but only after checking documented platform, hardware, and client requirements.
- Regardless of item-count preview, high-churn development folders inside OneDrive should still be treated as risky because churn and file density remain separate from the headline item-count limit.

The scanner should be one-shot by default:

- It aligns with the source PDF's warning against background monitoring.
- It avoids worsening a stressed system.
- It is easier to reason about, test, and trust before adding a GUI or scheduler.

The remediation model should be advisory-first:

- Built-in OneDrive fixes such as pause, Files On-Demand, selective sync, and reset are official and should be recommended.
- Moving folders out of OneDrive should be planned with dry-run scripts, confirmation, and clear rollback notes.
- Killing `onedrive.exe` and resetting OneDrive are emergency actions, not routine scan behavior.

## Alternatives Considered

### Microsoft 365 OneDrive Sync Health Dashboard

Better for tenant administrators managing fleets. Worse for personal machines, local development folder discovery, and immediate local remediation. It requires admin roles and reporting setup.

Decision: complementary future integration, not the MVP core.

### Process Monitor

Better for proving a specific file, registry key, or stack is causing sync churn. Worse as a first-line user tool because it captures real-time low-level activity and can produce large logs.

Decision: use as an escalation recommendation, not embedded in the MVP.

### Windows Performance Recorder And Analyzer

Better for expert ETW performance analysis. Worse for a quick user-facing answer and remediation checklist.

Decision: optional support-bundle workflow later.

### PowerShell-Only Script

Better for quick prototyping and administrative transparency. Worse for testability, packaging, structured reports, streaming scanner abstractions, and cross-version Windows API handling.

Decision: use PowerShell for generated remediation scripts, but implement the scanner in .NET.

### Continuous Tray Monitor

Better for ongoing fleet visibility. Worse for the source problem because it adds a resident process to an already stressed environment and contradicts the source guide.

Decision: defer until after the one-shot scanner is efficient and trusted.

### Direct Microsoft Graph Cloud Inventory

Better for cloud-side item counts and tenant-scale analysis. Worse for local-only symptoms: it cannot directly see local disk queues, OneDrive process CPU, local build-output churn, or unsynced local paths without additional agents.

Decision: possible later integration for work/school accounts, not MVP.

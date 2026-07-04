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

Microsoft's Event Viewer guidance says Windows logs and Applications and Services logs are a good place to start troubleshooting components. This supports adding a read-only event-log correlation pass before escalating to heavier trace capture.

Sources:

- [Event Viewer](https://learn.microsoft.com/en-us/shows/inside/event-viewer)
- [EventLogReader Class](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.eventing.reader.eventlogreader)
- [EventLogQuery Class](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.eventing.reader.eventlogquery)

PowerShell `Get-Counter` reads Windows performance counters, documents sampling intervals and maximum sample counts, notes that some counter sets are ACL-protected, and documents localized counter names. It also shows PhysicalDisk paths including `Current Disk Queue Length` and `Avg. Disk Queue Length`. This validates disk-counter sampling, but also confirms that OneLag must treat missing, localized, or access-denied counters as degraded evidence.

Source: [Get-Counter](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.diagnostics/get-counter)

Microsoft's OneDrive "Processing changes" troubleshooting names several non-count causes: an open OneDrive file, many newly added files, a very large file, sign-in state, recent sign-in or computer update, hidden files, temporary files, and large files such as ZIP, video, PST, or OST files. This broadens the scanner beyond total item count and development-folder heuristics.

Source: [Troubleshoot OneDrive sync issues: stuck on "Processing changes"](https://support.microsoft.com/en-US/onedrive/troubleshoot-onedrive-sync-issues-stuck-on-processing-changes)

Microsoft's OneDrive "Sync pending" troubleshooting says hidden files can be the cause when no visible file explains the pending state, and warns that temporary files may still belong to another program. This supports detecting hidden and temporary-file clusters while keeping remediation advisory unless the user explicitly confirms an action.

Source: [OneDrive is stuck on "Sync pending"](https://support.microsoft.com/en-US/onedrive/onedrive-is-stuck-on-sync-pending)

The implemented known-issue scanner maps Microsoft's OneDrive restrictions into specific local detector kinds: invalid characters, leading/trailing spaces, blocked names, reserved Windows device names, root-level `forms`, path and segment length limits, duplicate names, network or junction sync locations, temporary TMP files, PST/OST caveats, moved OneNote notebook files, preview-size limits, and over-limit individual files. These are advisory diagnostics only; OneLag does not rename, move, unlink, or reset anything automatically.

Source: [Restrictions and limitations in OneDrive and SharePoint](https://support.microsoft.com/en-US/onedrive/restrictions-and-limitations-in-onedrive-and-sharepoint)

Microsoft documents OneDrive reset as a supported sync repair path. The reset can resolve sync issues, disconnects sync connections, causes a full sync after reset, deletes the DAT file, and rebuilds the DAT file after OneDrive restarts. Microsoft also states that resetting OneDrive does not lose files or data on the computer. This supports a dry-run reset plan and explicit opt-in execution, but not direct editing or parsing of undocumented sync databases.

Source: [Reset OneDrive](https://support.microsoft.com/en-US/onedrive/reset-onedrive)

Microsoft says the Support and Recovery Assistant can identify and fix several OneDrive for work or school sync issues. That is a better escalation path for account, tenant, or sync-client repair cases than custom automation in OneLag.

Source: [Restrictions and limitations in OneDrive and SharePoint](https://support.microsoft.com/en-US/onedrive/restrictions-and-limitations-in-onedrive-and-sharepoint)

Windows Performance Recorder's General profile records basic system and performance data, including CPU, context switches, disk I/O, hard faults, memory info, process counters, process/thread, ready threads, sampled profile, and thread priority. WPR also has resource-analysis profiles for CPU, disk I/O, file I/O, registry I/O, network I/O, heap, memory pool, virtual allocation, and power. This is the Microsoft-aligned deeper investigation path when simple local evidence is inconclusive.

Sources:

- [Recording for Basic System Diagnosis](https://learn.microsoft.com/en-us/windows-hardware/test/wpt/recording-for-basic-system-diagnosis)
- [Recording for Resource-based Analysis](https://learn.microsoft.com/en-us/windows-hardware/test/wpt/recording-for-resource-based-analysis)

## Deep Dive Cause Model

The most likely OneDrive-linked cause is metadata pressure rather than raw storage size: too many tracked items, many newly added items, high-churn folders, hidden or temporary-file blockers, invalid names, long paths, large archive/media/mail data files, and account or update states that leave OneDrive processing changes for a long time. Development folders such as dependency caches, build outputs, virtual environments, and source-control metadata are especially risky because they create many small files and frequent writes.

The likely Windows-visible failure mode is sustained resource pressure: OneDrive, Explorer shell extensions, Windows Search, Defender, build tools, browser caches, or Windows Update can all drive CPU or disk I/O. OneLag should not assume causality from a folder inventory alone. It should report a stronger conclusion only when static OneDrive risk aligns with live process, disk, log, or event evidence.

The Explorer symptom is plausible but harder to prove from a low-impact scan. Microsoft documents shell extension handlers, including icon overlay handlers, and those handlers are queried before some shell actions. OneLag can detect conditions that make OneDrive shell integration suspect, but it should recommend Event Viewer, ProcMon, or WPR/WPA when Explorer hangs are present and the simple evidence does not isolate a cause.

## Added Design Gaps

The design now needs these additions:

- A lightweight system-pressure snapshot that records total CPU, memory, disk free space, disk queue, OS version, OneDrive version, and whether current pressure appears OneDrive-dominated or broader system pressure.
- A read-only Event Viewer correlation pass over recent Application, System, and OneDrive-relevant Applications and Services logs, with event messages redacted or summarized by default.
- A differential-diagnosis result category for "OneDrive likely", "OneDrive possible", "OneDrive not proven", and "non-OneDrive pressure suspected".
- Detection of OneDrive sync blockers beyond item count: long paths, invalid names, temporary files, hidden pending files, large archive/media/mail data files, and high-risk shortcut or synced-library patterns where observable locally.
- Earlier expert escalation guidance for WPR/WPA and ProcMon, including privacy warnings and minimum useful capture profiles, without running heavy tracing by default.
- A work/school account escalation recommendation to Microsoft Support and Recovery Assistant when the evidence points to account, tenant, or sync-client repair instead of local folder pressure.

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

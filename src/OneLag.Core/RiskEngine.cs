namespace OneLag.Core;

public sealed class RiskEngine
{
    public (DifferentialDiagnosis Diagnosis, IReadOnlyList<Finding> Findings, IReadOnlyList<Recommendation> Recommendations) Analyze(
        IReadOnlyList<InventorySummary> inventories,
        TelemetrySnapshot telemetry,
        SystemPressureSnapshot pressure,
        OneDriveClientHealthSnapshot clientHealth)
    {
        return Analyze(inventories, telemetry, pressure, clientHealth, Array.Empty<EventLogSummary>());
    }

    public (DifferentialDiagnosis Diagnosis, IReadOnlyList<Finding> Findings, IReadOnlyList<Recommendation> Recommendations) Analyze(
        IReadOnlyList<InventorySummary> inventories,
        TelemetrySnapshot telemetry,
        SystemPressureSnapshot pressure,
        OneDriveClientHealthSnapshot clientHealth,
        IReadOnlyList<EventLogSummary> eventLogs)
    {
        var findings = new List<Finding>();
        var recommendations = new List<Recommendation>();

        var totalItems = inventories.Sum(inventory => inventory.TotalItems);
        var highRiskDirectories = inventories.Sum(inventory => inventory.HighRiskDirectories.Count);
        var significantBlockers = inventories
            .SelectMany(inventory => inventory.SyncBlockers)
            .Where(blocker => blocker.Severity is Severity.Emergency or Severity.HighRisk or Severity.Warning)
            .ToArray();
        var highRiskBlockers = significantBlockers.Length;
        var emergencyBlockers = significantBlockers.Count(blocker => blocker.Severity == Severity.Emergency);
        var capped = inventories.Any(inventory => inventory.WasCapped);
        var onedriveRunning = telemetry.OneDriveProcesses.Count > 0;
        var onedriveCpuPercent = telemetry.OneDriveProcesses
            .Where(process => process.CpuPercent.HasValue)
            .Sum(process => process.CpuPercent.GetValueOrDefault());
        var logChurn = telemetry.OneDriveLogFilesChangedLastMinute;
        var resetWorthConsidering = clientHealth.ResetCommands.Count > 0
            && clientHealth.Signals.Any(signal => signal.Severity is Severity.Warning or Severity.HighRisk or Severity.Emergency);
        var recentWindowsEvents = eventLogs
            .Where(summary => summary.Level is "Critical" or "Error" or "Warning")
            .ToArray();
        var pressureClassification = PressureClassifier.Classify(pressure);

        if (totalItems >= 1_000_000)
        {
            findings.Add(new Finding(
                Severity.HighRisk,
                "OneDrive item count requires public-preview scale assumptions",
                $"Inventory counted {totalItems:N0} files and directories; Microsoft documents 1,000,000-item sync support as a Windows public preview with hardware and configuration requirements.",
                "medium"));
        }
        else if (totalItems >= 300_000 || capped)
        {
            findings.Add(new Finding(
                Severity.HighRisk,
                "OneDrive item count is in the high-risk range",
                capped
                    ? "Inventory hit the configured cap before completion."
                    : $"Inventory counted {totalItems:N0} files and directories.",
                "high"));
        }
        else if (totalItems >= 200_000)
        {
            findings.Add(new Finding(
                Severity.Warning,
                "OneDrive item count is near Microsoft's performance guidance",
                $"Inventory counted {totalItems:N0} files and directories.",
                "medium"));
        }

        if (highRiskDirectories > 0)
        {
            findings.Add(new Finding(
                Severity.HighRisk,
                "High-churn development folders are inside a synced tree",
                $"{highRiskDirectories:N0} high-risk development/build directories were detected.",
                "high"));
        }

        if (highRiskBlockers > 0)
        {
            var topKinds = string.Join(", ", significantBlockers
                .GroupBy(blocker => blocker.Kind)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .Select(group => $"{group.Key}={group.Count():N0}"));

            findings.Add(new Finding(
                emergencyBlockers > 0 ? Severity.Emergency : Severity.Warning,
                "Known OneDrive restriction issues were found",
                $"{highRiskBlockers:N0} known restriction issue(s) were detected. Top kinds: {topKinds}.",
                "medium"));
        }

        if (logChurn >= 5)
        {
            findings.Add(new Finding(
                Severity.Warning,
                "OneDrive log churn is elevated",
                $"{logChurn:N0} OneDrive log files changed in the last minute.",
                "medium"));
        }

        if (onedriveRunning)
        {
            findings.Add(new Finding(
                Severity.Info,
                "OneDrive process is running",
                onedriveCpuPercent > 0
                    ? $"{telemetry.OneDriveProcesses.Count:N0} OneDrive process instance(s) were observed using about {onedriveCpuPercent:N1}% CPU across sampled processes."
                    : $"{telemetry.OneDriveProcesses.Count:N0} OneDrive process instance(s) were observed.",
                "medium"));
        }

        if (onedriveCpuPercent >= 15)
        {
            findings.Add(new Finding(
                Severity.Warning,
                "OneDrive CPU usage was elevated during the sample",
                $"OneDrive used about {onedriveCpuPercent:N1}% CPU across sampled processes.",
                "medium"));
        }

        if (resetWorthConsidering)
        {
            var topSignals = string.Join(", ", clientHealth.Signals
                .Where(signal => signal.Severity is Severity.Warning or Severity.HighRisk or Severity.Emergency)
                .Take(5)
                .Select(signal => signal.Kind));

            findings.Add(new Finding(
                Severity.Warning,
                "OneDrive client cache reset is worth considering",
                $"OneDrive client health metadata found reset-worthy signal(s): {topSignals}. OneLag did not parse or modify the internal sync database.",
                "medium"));
        }

        if (pressureClassification.HasAnyPressure)
        {
            findings.Add(new Finding(
                Severity.Warning,
                "Whole-system performance pressure was observed",
                string.Join("; ", pressureClassification.Evidence),
                "medium"));
        }

        if (recentWindowsEvents.Length > 0)
        {
            var topEvents = string.Join(", ", recentWindowsEvents
                .OrderByDescending(summary => summary.Count)
                .ThenBy(summary => summary.Provider, StringComparer.OrdinalIgnoreCase)
                .Take(5)
                .Select(summary => $"{summary.LogName}/{summary.Provider}/{summary.EventId}/{summary.Level}={summary.Count:N0}"));

            findings.Add(new Finding(
                Severity.Warning,
                "Recent Windows reliability events were observed",
                $"Event Viewer had recent critical, error, or warning summaries. Top events: {topEvents}.",
                "medium"));
        }

        var hasStaticOneDriveRisk = totalItems >= 200_000 || highRiskDirectories > 0 || highRiskBlockers > 0 || capped;
        var hasLiveOneDriveRisk = (onedriveRunning && logChurn >= 5) || onedriveCpuPercent >= 15 || resetWorthConsidering;
        var hasRecentWindowsEventRisk = recentWindowsEvents.Length > 0;

        var diagnosis = (hasStaticOneDriveRisk, hasLiveOneDriveRisk) switch
        {
            (true, true) => DifferentialDiagnosis.OneDriveLikely,
            (true, false) => DifferentialDiagnosis.OneDrivePossible,
            (false, true) => DifferentialDiagnosis.OneDrivePossible,
            _ when hasRecentWindowsEventRisk || pressureClassification.HasAnyPressure => DifferentialDiagnosis.NonOneDrivePressureSuspected,
            _ when pressure.EvidenceState.Contains("pressure", StringComparison.OrdinalIgnoreCase) => DifferentialDiagnosis.NonOneDrivePressureSuspected,
            _ => DifferentialDiagnosis.OneDriveNotProven
        };

        recommendations.Add(new Recommendation(
            RecommendationKind.Observe,
            "Review the evidence before changing files",
            "The report separates direct observations from inferred causes.",
            "No data is modified by the scan."));

        if (diagnosis is DifferentialDiagnosis.OneDriveLikely or DifferentialDiagnosis.OneDrivePossible)
        {
            recommendations.Add(new Recommendation(
                RecommendationKind.PauseSync,
                "Pause OneDrive before moving or reorganizing risky folders",
                "Pausing sync reduces contention while you inspect or move high-churn folders.",
                "Use the official OneDrive tray menu; OneLag does not pause sync automatically."));

            if (highRiskDirectories > 0)
            {
                recommendations.Add(new Recommendation(
                    RecommendationKind.MoveOutOfOneDrive,
                    "Move development and build-output folders out of OneDrive",
                    "Dependency caches, build outputs, virtual environments, and source-control metadata create many small changes.",
                    "Generate and inspect a dry-run move plan before executing anything."));
            }

            if (significantBlockers.Any(blocker => blocker.Kind is "invalid-character" or "reserved-name" or "blocked-name" or "leading-or-trailing-space" or "root-forms-name" or "long-segment" or "long-onedrive-relative-path" or "long-local-sync-path" or "windows-explorer-path-limit" or "duplicate-filename" or "office-folder-semicolon" or "invalid-leading-folder-character"))
            {
                recommendations.Add(new Recommendation(
                    RecommendationKind.SelectiveSync,
                    "Rename or shorten items that violate OneDrive restrictions",
                    "Unsupported characters, blocked names, trailing spaces, reserved device names, and over-limit paths can keep sync stuck or processing changes.",
                    "Rename manually or with a reviewed dry-run script; do not bulk rename without a backup."));
            }

            if (significantBlockers.Any(blocker => blocker.Kind is "mail-data-file" or "onenote-notebook-file"))
            {
                recommendations.Add(new Recommendation(
                    RecommendationKind.MoveOutOfOneDrive,
                    "Move Outlook and OneNote data through supported app workflows",
                    "PST/OST and existing OneNote notebook files have special OneDrive behavior and can create misleading sync or lag symptoms.",
                    "Use Outlook or OneNote supported move/export steps instead of dragging live data files."));
            }

            if (significantBlockers.Any(blocker => blocker.Kind == "file-too-large-for-sync"))
            {
                recommendations.Add(new Recommendation(
                    RecommendationKind.MoveOutOfOneDrive,
                    "Move files that exceed OneDrive sync limits",
                    "Individual files over OneDrive's supported file-size limit cannot sync reliably.",
                    "Move or split over-limit files only after confirming backup and storage state."));
            }

            if (significantBlockers.Any(blocker => blocker.Kind is "network-sync-location" or "root-reparse-point" or "reparse-point"))
            {
                recommendations.Add(new Recommendation(
                    RecommendationKind.ReviewUnsupportedConfiguration,
                    "Review unsupported sync-root or junction configuration",
                    "OneDrive does not support syncing through network locations, symbolic links, or junction points.",
                    "Unlink/relink or move data only after confirming the real local path and backup state."));
            }
        }

        if (resetWorthConsidering)
        {
            recommendations.Add(new Recommendation(
                RecommendationKind.ResetOneDrive,
                "Consider a supported OneDrive reset",
                "Microsoft documents OneDrive reset as a supported sync repair path that rebuilds the DAT cache after restart.",
                "Run `onelag repair reset-onedrive` first to review the dry-run plan. Execute only after confirming sync status and work policy."));
        }

        if (pressureClassification.HasAnyPressure)
        {
            recommendations.Add(new Recommendation(
                RecommendationKind.EscalateToProcmonOrWpr,
                "Correlate system pressure before changing OneDrive data",
                "CPU, memory, or disk pressure can make the laptop unresponsive even when OneDrive inventory risk is low.",
                "Use the pressure evidence to decide whether WPR/WPA, Resource Monitor, or vendor driver/storage review is the next step."));
        }

        if (hasRecentWindowsEventRisk)
        {
            recommendations.Add(new Recommendation(
                RecommendationKind.EscalateToEventViewer,
                "Review recent Event Viewer reliability evidence",
                "Recent critical, error, or warning events can explain lag that the OneDrive inventory alone cannot prove.",
                "Use Event Viewer details as local evidence; avoid sharing full logs without reviewing sensitive data."));
        }

        if (diagnosis is DifferentialDiagnosis.OneDriveNotProven or DifferentialDiagnosis.NonOneDrivePressureSuspected)
        {
            recommendations.Add(new Recommendation(
                RecommendationKind.EscalateToProcmonOrWpr,
                "Use Event Viewer, WPR/WPA, or ProcMon if freezes continue",
                "The scan did not prove OneDrive as the cause. A transient stall may require trace evidence.",
                "Do not upload traces without reviewing privacy impact."));
        }

        return (diagnosis, findings, recommendations);
    }
}

namespace OneLag.Core;

public sealed class RiskEngine
{
    public (DifferentialDiagnosis Diagnosis, IReadOnlyList<Finding> Findings, IReadOnlyList<Recommendation> Recommendations) Analyze(
        IReadOnlyList<InventorySummary> inventories,
        TelemetrySnapshot telemetry,
        SystemPressureSnapshot pressure)
    {
        var findings = new List<Finding>();
        var recommendations = new List<Recommendation>();

        var totalItems = inventories.Sum(inventory => inventory.TotalItems);
        var highRiskDirectories = inventories.Sum(inventory => inventory.HighRiskDirectories.Count);
        var highRiskBlockers = inventories.Sum(inventory => inventory.SyncBlockers.Count(blocker => blocker.Severity is Severity.HighRisk or Severity.Warning));
        var capped = inventories.Any(inventory => inventory.WasCapped);
        var onedriveRunning = telemetry.OneDriveProcesses.Count > 0;
        var logChurn = telemetry.OneDriveLogFilesChangedLastMinute;

        if (totalItems >= 300_000 || capped)
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
            findings.Add(new Finding(
                Severity.Warning,
                "Potential OneDrive sync blockers were found",
                $"{highRiskBlockers:N0} hidden, temporary, large, invalid-name, long-path, or reparse-point risks were detected.",
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
                $"{telemetry.OneDriveProcesses.Count:N0} OneDrive process instance(s) were observed.",
                "medium"));
        }

        var hasStaticOneDriveRisk = totalItems >= 200_000 || highRiskDirectories > 0 || highRiskBlockers > 0 || capped;
        var hasLiveOneDriveRisk = onedriveRunning && logChurn >= 5;

        var diagnosis = (hasStaticOneDriveRisk, hasLiveOneDriveRisk) switch
        {
            (true, true) => DifferentialDiagnosis.OneDriveLikely,
            (true, false) => DifferentialDiagnosis.OneDrivePossible,
            (false, true) => DifferentialDiagnosis.OneDrivePossible,
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

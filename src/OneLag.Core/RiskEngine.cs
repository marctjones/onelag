namespace OneLag.Core;

public sealed record RiskAnalysis(
    DifferentialDiagnosis Diagnosis,
    IReadOnlyList<Finding> Findings,
    IReadOnlyList<Recommendation> Recommendations,
    IReadOnlyList<Hypothesis> Hypotheses,
    EvidenceQuality EvidenceQuality);

public sealed class RiskEngine
{
    private readonly HypothesisEngine hypothesisEngine = new();

    public RiskAnalysis Analyze(
        IReadOnlyList<InventorySummary> inventories,
        TelemetrySnapshot telemetry,
        SystemPressureSnapshot pressure,
        OneDriveClientHealthSnapshot clientHealth)
    {
        return Analyze(inventories, telemetry, pressure, clientHealth, Array.Empty<EventLogSummary>());
    }

    public RiskAnalysis Analyze(
        IReadOnlyList<InventorySummary> inventories,
        TelemetrySnapshot telemetry,
        SystemPressureSnapshot pressure,
        OneDriveClientHealthSnapshot clientHealth,
        IReadOnlyList<EventLogSummary> eventLogs,
        HostContext? hostContext = null,
        ShellResponsiveness? shell = null)
    {
        var findings = new List<Finding>();
        var recommendations = new List<Recommendation>();

        var totalItems = inventories.Sum(inventory => inventory.TotalItems);
        var highRiskDirectories = inventories.Sum(inventory => inventory.HighRiskDirectories.Count);
        var significantBlockers = inventories
            .SelectMany(inventory => inventory.SyncBlockers)
            .Where(blocker => blocker.Severity is Severity.Emergency or Severity.HighRisk or Severity.Warning)
            .ToArray();
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

        var evidenceQuality = EvidenceQualityAssessor.Assess(telemetry, pressure, eventLogs, hostContext, shell);
        var hypotheses = hypothesisEngine.Rank(new HypothesisInputs(
            inventories,
            telemetry,
            pressure,
            clientHealth,
            eventLogs,
            hostContext,
            shell,
            evidenceQuality));

        if (evidenceQuality.Grade == EvidenceGrade.Insufficient)
        {
            findings.Add(new Finding(
                Severity.Warning,
                "This capture contains too little live evidence to diagnose anything",
                $"Evidence quality scored {evidenceQuality.Score}/100 with {evidenceQuality.Gaps.Count:N0} gap(s). See the Evidence Quality section for what was missing.",
                "high"));
        }

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

        if (significantBlockers.Length > 0)
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
                $"{significantBlockers.Length:N0} known restriction issue(s) were detected. Top kinds: {topKinds}. Restriction issues describe sync hygiene, not system lag, and on their own do not implicate OneDrive in a freeze.",
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
        else
        {
            findings.Add(new Finding(
                Severity.Warning,
                "OneDrive was not running, so the OneDrive hypothesis could not be tested",
                "No OneDrive process was present at capture time. Any OneDrive verdict from this capture rests on folder shape alone, which describes exposure rather than an active cause.",
                "high"));
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

        if (pressureClassification.HasInterruptPressure)
        {
            findings.Add(new Finding(
                Severity.HighRisk,
                "Kernel interrupt or DPC latency is elevated",
                "A driver is holding CPU time at high IRQL. This starves the whole desktop, including the mouse cursor, and cannot be caused by OneDrive's user-mode sync engine.",
                "high"));
        }

        if (pressureClassification.HasAnyPressure)
        {
            findings.Add(new Finding(
                Severity.Warning,
                "Whole-system performance pressure was observed",
                string.Join("; ", pressureClassification.Evidence),
                "medium"));
        }

        if (shell?.ShellWindowHung == true)
        {
            findings.Add(new Finding(
                Severity.HighRisk,
                "The Explorer shell was not pumping messages",
                $"Windows reported the shell window as hung{(shell.ShellPumpLatencyMilliseconds.HasValue ? $", and it took {shell.ShellPumpLatencyMilliseconds:N0} ms to answer a null message" : string.Empty)}. This measures the shell-blocking failure mode directly rather than inferring it from folder shape.",
                "high"));
        }

        if (hostContext is not null && hostContext.IndirectDisplayCount > 0)
        {
            findings.Add(new Finding(
                Severity.Warning,
                "An indirect/USB display driver is rendering a display",
                $"{hostContext.IndirectDisplayCount:N0} display(s) run through an indirect display driver (DisplayLink-class USB graphics). These render frames on the CPU and push them over USB, and are a common source of long DPC routines and desktop stutter.",
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

        var diagnosis = DeriveDiagnosis(hypotheses, pressureClassification, recentWindowsEvents.Length > 0);

        recommendations.Add(new Recommendation(
            RecommendationKind.Observe,
            "Review the evidence before changing files",
            "The report separates direct observations from inferred causes, and ranks every candidate cause with the evidence for and against it.",
            "No data is modified by the scan."));

        if (evidenceQuality.Grade == EvidenceGrade.Insufficient)
        {
            recommendations.Add(new Recommendation(
                RecommendationKind.Observe,
                "Capture evidence while the machine is actually lagging",
                "A snapshot taken during a calm moment cannot explain an episodic freeze. The lag has to be observed while it is happening to be diagnosed.",
                "Run `onelag watch start --duration 8h` and press the lag marker when a freeze happens. No files are modified."));
        }

        AddOneDriveRecommendations(diagnosis, hypotheses, highRiskDirectories, significantBlockers, recommendations);

        if (resetWorthConsidering)
        {
            recommendations.Add(new Recommendation(
                RecommendationKind.ResetOneDrive,
                "Consider a supported OneDrive reset",
                "Microsoft documents OneDrive reset as a supported sync repair path that rebuilds the DAT cache after restart.",
                "Run `onelag repair reset-onedrive` first to review the dry-run plan. Execute only after confirming sync status and work policy."));
        }

        // Every non-OneDrive hypothesis that reaches Possible or better contributes its own next step, so a
        // non-OneDrive cause produces an actionable recommendation rather than a shrug toward WPR.
        foreach (var hypothesis in hypotheses.Where(candidate => candidate.Verdict is HypothesisVerdict.Possible or HypothesisVerdict.Likely or HypothesisVerdict.StronglySupported))
        {
            if (hypothesis.Kind == HypothesisKind.OneDriveSync)
            {
                continue;
            }

            recommendations.Add(new Recommendation(
                MapRecommendationKind(hypothesis.Kind),
                $"{Describe(hypothesis.Kind)} is {Describe(hypothesis.Verdict)}",
                hypothesis.Supporting.Count > 0 ? string.Join(" ", hypothesis.Supporting) : hypothesis.Summary,
                hypothesis.NextStep));
        }

        if (pressureClassification.HasAnyPressure)
        {
            recommendations.Add(new Recommendation(
                RecommendationKind.EscalateToProcmonOrWpr,
                "Correlate system pressure before changing OneDrive data",
                "CPU, memory, disk, or interrupt pressure can make the laptop unresponsive even when OneDrive inventory risk is low.",
                "Use the pressure evidence to decide whether WPR/WPA, Resource Monitor, or vendor driver/storage review is the next step."));
        }

        if (recentWindowsEvents.Length > 0)
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

        return new RiskAnalysis(diagnosis, findings, recommendations, hypotheses, evidenceQuality);
    }

    /// <summary>
    /// The OneDrive-shaped verdict, now derived from the ranked hypotheses rather than computed on its own.
    /// It survives so existing reports, views, and support bundles keep working, but the hypothesis table is
    /// the real output. Critically, OneDrive can no longer be promoted on static folder shape alone.
    /// </summary>
    private static DifferentialDiagnosis DeriveDiagnosis(
        IReadOnlyList<Hypothesis> hypotheses,
        PressureClassification pressure,
        bool hasRecentWindowsEvents)
    {
        var oneDrive = hypotheses.First(hypothesis => hypothesis.Kind == HypothesisKind.OneDriveSync);
        var topOther = hypotheses
            .Where(hypothesis => hypothesis.Kind != HypothesisKind.OneDriveSync)
            .MaxBy(hypothesis => hypothesis.Score);

        if (oneDrive.Verdict == HypothesisVerdict.StronglySupported)
        {
            return DifferentialDiagnosis.OneDriveLikely;
        }

        // Another cause outscoring OneDrive is the case the old engine had no way to express.
        if (topOther is not null && topOther.Score > oneDrive.Score && topOther.Score >= 20)
        {
            return DifferentialDiagnosis.NonOneDrivePressureSuspected;
        }

        if (oneDrive.Verdict == HypothesisVerdict.Likely)
        {
            return DifferentialDiagnosis.OneDriveLikely;
        }

        if (oneDrive.Verdict == HypothesisVerdict.Possible)
        {
            return DifferentialDiagnosis.OneDrivePossible;
        }

        if (pressure.HasAnyPressure || hasRecentWindowsEvents)
        {
            return DifferentialDiagnosis.NonOneDrivePressureSuspected;
        }

        return DifferentialDiagnosis.OneDriveNotProven;
    }

    private static void AddOneDriveRecommendations(
        DifferentialDiagnosis diagnosis,
        IReadOnlyList<Hypothesis> hypotheses,
        int highRiskDirectories,
        IReadOnlyList<SyncBlocker> significantBlockers,
        List<Recommendation> recommendations)
    {
        var oneDrive = hypotheses.First(hypothesis => hypothesis.Kind == HypothesisKind.OneDriveSync);
        var oneDriveInPlay = diagnosis is DifferentialDiagnosis.OneDriveLikely or DifferentialDiagnosis.OneDrivePossible
            || oneDrive.Verdict is HypothesisVerdict.Possible or HypothesisVerdict.Likely or HypothesisVerdict.StronglySupported;

        // Pausing sync is a lag mitigation, so it only makes sense when OneDrive is actually implicated in
        // the lag. Everything below it is documented sync-configuration hygiene that stays worth doing even
        // when the lag turns out to be a driver problem, so it is not gated on the diagnosis.
        if (oneDriveInPlay)
        {
            recommendations.Add(new Recommendation(
                RecommendationKind.PauseSync,
                "Pause OneDrive before moving or reorganizing risky folders",
                "Pausing sync reduces contention while you inspect or move high-churn folders.",
                "Use the official OneDrive tray menu; OneLag does not pause sync automatically."));
        }

        if (highRiskDirectories > 0)
        {
            recommendations.Add(new Recommendation(
                RecommendationKind.MoveOutOfOneDrive,
                "Move development and build-output folders out of OneDrive",
                "Dependency caches, build outputs, virtual environments, and source-control metadata create many small changes. This is a documented anti-pattern regardless of what is causing the current lag.",
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

    private static RecommendationKind MapRecommendationKind(HypothesisKind kind)
    {
        return kind switch
        {
            HypothesisKind.DisplayOrDockPipeline => RecommendationKind.ReviewUnsupportedConfiguration,
            HypothesisKind.BluetoothOrInputRadio => RecommendationKind.ReviewUnsupportedConfiguration,
            HypothesisKind.ShellExtensionBlocking => RecommendationKind.ReviewUnsupportedConfiguration,
            HypothesisKind.SecurityOrSearchScanner => RecommendationKind.EscalateToEventViewer,
            HypothesisKind.ThermalOrPowerThrottling => RecommendationKind.EscalateToEventViewer,
            HypothesisKind.MemoryPaging => RecommendationKind.FreeUpSpace,
            _ => RecommendationKind.EscalateToProcmonOrWpr
        };
    }

    private static string Describe(HypothesisKind kind)
    {
        return kind switch
        {
            HypothesisKind.OneDriveSync => "OneDrive sync",
            HypothesisKind.StorageSaturation => "Storage saturation",
            HypothesisKind.CpuContention => "CPU contention",
            HypothesisKind.MemoryPaging => "Memory paging",
            HypothesisKind.DriverInterruptLatency => "Driver interrupt/DPC latency",
            HypothesisKind.DisplayOrDockPipeline => "Display or dock pipeline",
            HypothesisKind.BluetoothOrInputRadio => "Bluetooth or input radio",
            HypothesisKind.ShellExtensionBlocking => "Explorer shell blocking",
            HypothesisKind.SecurityOrSearchScanner => "Defender, Search, or Update scanner",
            HypothesisKind.ThermalOrPowerThrottling => "Thermal or power throttling",
            _ => kind.ToString()
        };
    }

    private static string Describe(HypothesisVerdict verdict)
    {
        return verdict switch
        {
            HypothesisVerdict.StronglySupported => "strongly supported",
            HypothesisVerdict.Likely => "likely",
            HypothesisVerdict.Possible => "possible",
            HypothesisVerdict.NotSupported => "not supported",
            HypothesisVerdict.RuledOut => "ruled out",
            _ => "untested"
        };
    }
}

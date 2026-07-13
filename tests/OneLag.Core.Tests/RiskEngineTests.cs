using OneLag.Core;

namespace OneLag.Core.Tests;

public sealed class RiskEngineTests
{
    [Fact]
    public void AnalyzeDoesNotBlameOneDriveOnItemCountAloneWhenOneDriveIsNotRunning()
    {
        var inventory = HighItemCountInventory();

        var result = new RiskEngine().Analyze(
            new[] { inventory },
            EmptyTelemetry(),
            EmptyPressure(),
            EmptyHealth());

        // A tree that is too big is exposure, not an active cause. With OneDrive not even running, there is
        // no live evidence to test the hypothesis against, so it must not be promoted.
        Assert.Equal(DifferentialDiagnosis.OneDriveNotProven, result.Diagnosis);
        Assert.Contains(result.Findings, finding => finding.Severity == Severity.HighRisk);
        Assert.Contains(result.Findings, finding => finding.Title == "OneDrive was not running, so the OneDrive hypothesis could not be tested");
    }

    [Fact]
    public void AnalyzeAllowsOneDrivePossibleWhenRunningButIdleWithHighItemCount()
    {
        var inventory = HighItemCountInventory();

        var telemetry = new TelemetrySnapshot(
            DateTimeOffset.UtcNow,
            new[] { new ProcessSample("OneDrive", 123, 100, TimeSpan.FromSeconds(1), null, 0.5) },
            0,
            null,
            "test");

        var result = new RiskEngine().Analyze(new[] { inventory }, telemetry, EmptyPressure(), EmptyHealth());

        Assert.Equal(DifferentialDiagnosis.OneDrivePossible, result.Diagnosis);
    }

    [Fact]
    public void AnalyzeDoesNotBlameOneDriveOnSyncBlockersAlone()
    {
        // The regression this guards: a real scan reached "OneDrive possible" purely on 22 desktop.ini
        // hits, with no live telemetry at all. Restriction issues are sync hygiene, not lag evidence.
        var inventory = new InventorySummary(
            "C:\\Users\\test\\OneDrive",
            900,
            200,
            0,
            3,
            false,
            Array.Empty<string>(),
            Array.Empty<TopLevelInventory>(),
            Array.Empty<DirectoryRisk>(),
            new[]
            {
                new SyncBlocker("C:\\Users\\test\\OneDrive\\a\\desktop.ini", "blocked-name", "test", Severity.HighRisk),
                new SyncBlocker("C:\\Users\\test\\OneDrive\\b\\desktop.ini", "blocked-name", "test", Severity.HighRisk)
            });

        var result = new RiskEngine().Analyze(new[] { inventory }, EmptyTelemetry(), EmptyPressure(), EmptyHealth());

        Assert.Equal(DifferentialDiagnosis.OneDriveNotProven, result.Diagnosis);

        var oneDrive = result.Hypotheses.Single(hypothesis => hypothesis.Kind == HypothesisKind.OneDriveSync);
        Assert.Equal(HypothesisVerdict.NotSupported, oneDrive.Verdict);
        Assert.Contains(oneDrive.Opposing, evidence => evidence.Contains("far below the 300,000-item threshold", StringComparison.Ordinal));
    }

    [Fact]
    public void AnalyzeRanksInterruptLatencyAboveOneDriveWhenDpcIsPinned()
    {
        var inventory = new InventorySummary(
            "C:\\Users\\test\\OneDrive",
            900,
            200,
            0,
            3,
            false,
            Array.Empty<string>(),
            Array.Empty<TopLevelInventory>(),
            Array.Empty<DirectoryRisk>(),
            Array.Empty<SyncBlocker>());

        var pressure = new SystemPressureSnapshot(
            DateTimeOffset.UtcNow,
            "normal",
            "normal",
            "normal",
            "ac",
            Array.Empty<string>(),
            "test",
            new[]
            {
                new PerformanceSignal("processor-total-percent", 12, "percent", "pdh"),
                new PerformanceSignal("processor-dpc-percent", 6, "percent", "pdh"),
                new PerformanceSignal("processor-dpc-percent-max-core", 34, "percent", "pdh-per-instance-max"),
                new PerformanceSignal("physical-disk-queue-length", 0.2, "count", "pdh")
            });

        var host = new HostContext(
            DateTimeOffset.UtcNow,
            2,
            0,
            1,
            new[] { new DisplayInfo("Dock Monitor", "indirect-wired-usb", false, true, 1920, 1080, 60) },
            true,
            true,
            2,
            "source=ac;battery=100%",
            true,
            new[] { "DisplayLinkManager" },
            DockStates.DockedLikely,
            "test");

        var result = new RiskEngine().Analyze(
            new[] { inventory },
            EmptyTelemetry(),
            pressure,
            EmptyHealth(),
            Array.Empty<EventLogSummary>(),
            host);

        Assert.Equal(DifferentialDiagnosis.NonOneDrivePressureSuspected, result.Diagnosis);

        var top = result.Hypotheses[0];
        Assert.Contains(top.Kind, new[] { HypothesisKind.DriverInterruptLatency, HypothesisKind.DisplayOrDockPipeline });

        var oneDrive = result.Hypotheses.Single(hypothesis => hypothesis.Kind == HypothesisKind.OneDriveSync);
        Assert.True(top.Score > oneDrive.Score);
    }

    [Fact]
    public void AnalyzeRulesOutBluetoothWhenRadioIsOff()
    {
        var inventory = new InventorySummary(
            "C:\\Users\\test\\OneDrive",
            1,
            1,
            0,
            1,
            false,
            Array.Empty<string>(),
            Array.Empty<TopLevelInventory>(),
            Array.Empty<DirectoryRisk>(),
            Array.Empty<SyncBlocker>());

        var host = new HostContext(
            DateTimeOffset.UtcNow,
            1,
            0,
            0,
            Array.Empty<DisplayInfo>(),
            false,
            false,
            0,
            "source=battery;battery=80%",
            false,
            Array.Empty<string>(),
            DockStates.UndockedLikely,
            "test");

        var result = new RiskEngine().Analyze(
            new[] { inventory },
            EmptyTelemetry(),
            EmptyPressure(),
            EmptyHealth(),
            Array.Empty<EventLogSummary>(),
            host);

        var bluetooth = result.Hypotheses.Single(hypothesis => hypothesis.Kind == HypothesisKind.BluetoothOrInputRadio);
        Assert.Equal(HypothesisVerdict.RuledOut, bluetooth.Verdict);
    }

    [Fact]
    public void AnalyzeDoesNotTreatAnUnknownBluetoothDeviceCountAsNoDevices()
    {
        // Windows' classic Bluetooth enumeration cannot see LE devices, so the count comes back unknown
        // rather than zero. With the radio live and interrupts elevated, that must not exonerate Bluetooth.
        var inventory = new InventorySummary(
            "C:\\Users\\test\\OneDrive",
            1,
            1,
            0,
            1,
            false,
            Array.Empty<string>(),
            Array.Empty<TopLevelInventory>(),
            Array.Empty<DirectoryRisk>(),
            Array.Empty<SyncBlocker>());

        var pressure = new SystemPressureSnapshot(
            DateTimeOffset.UtcNow,
            "normal",
            "normal",
            "normal",
            "ac",
            Array.Empty<string>(),
            "test",
            new[] { new PerformanceSignal("processor-dpc-percent-max-core", 26, "percent", "pdh-per-instance-max") });

        var host = new HostContext(
            DateTimeOffset.UtcNow,
            1,
            0,
            0,
            Array.Empty<DisplayInfo>(),
            true,
            true,
            null,
            "source=battery;battery=80%",
            false,
            Array.Empty<string>(),
            DockStates.UndockedLikely,
            "test");

        var result = new RiskEngine().Analyze(
            new[] { inventory },
            EmptyTelemetry(),
            pressure,
            EmptyHealth(),
            Array.Empty<EventLogSummary>(),
            host);

        var bluetooth = result.Hypotheses.Single(hypothesis => hypothesis.Kind == HypothesisKind.BluetoothOrInputRadio);
        Assert.NotEqual(HypothesisVerdict.RuledOut, bluetooth.Verdict);
        Assert.True(bluetooth.Score >= 40);
        Assert.Contains(bluetooth.Opposing, evidence => evidence.Contains("Bluetooth LE", StringComparison.Ordinal));
    }

    [Fact]
    public void AnalyzeReportsInsufficientEvidenceForAnEmptyCapture()
    {
        var inventory = new InventorySummary(
            "C:\\Users\\test\\OneDrive",
            900,
            200,
            0,
            3,
            false,
            Array.Empty<string>(),
            Array.Empty<TopLevelInventory>(),
            Array.Empty<DirectoryRisk>(),
            Array.Empty<SyncBlocker>());

        var result = new RiskEngine().Analyze(new[] { inventory }, EmptyTelemetry(), EmptyPressure(), EmptyHealth());

        Assert.Equal(EvidenceGrade.Insufficient, result.EvidenceQuality.Grade);
        Assert.NotEmpty(result.EvidenceQuality.Gaps);
        Assert.Contains(result.Recommendations, recommendation => recommendation.Title == "Capture evidence while the machine is actually lagging");
    }

    private static InventorySummary HighItemCountInventory()
    {
        return new InventorySummary(
            "C:\\Users\\test\\OneDrive",
            300_000,
            1,
            0,
            1,
            false,
            Array.Empty<string>(),
            Array.Empty<TopLevelInventory>(),
            Array.Empty<DirectoryRisk>(),
            Array.Empty<SyncBlocker>());
    }

    [Fact]
    public void AnalyzeClassifiesStaticAndLiveEvidenceAsLikely()
    {
        var inventory = new InventorySummary(
            "C:\\Users\\test\\OneDrive",
            1,
            1,
            0,
            1,
            false,
            Array.Empty<string>(),
            Array.Empty<TopLevelInventory>(),
            new[] { new DirectoryRisk("C:\\Users\\test\\OneDrive\\repo\\.git", ".git", "test", 1) },
            Array.Empty<SyncBlocker>());

        var telemetry = new TelemetrySnapshot(
            DateTimeOffset.UtcNow,
            new[] { new ProcessSample("OneDrive", 123, 100, TimeSpan.FromSeconds(1), null) },
            5,
            null,
            "test");

        var result = new RiskEngine().Analyze(new[] { inventory }, telemetry, EmptyPressure(), EmptyHealth());

        Assert.Equal(DifferentialDiagnosis.OneDriveLikely, result.Diagnosis);
    }

    [Fact]
    public void AnalyzeClassifiesStaticRiskAndOneDriveCpuAsLikely()
    {
        var inventory = new InventorySummary(
            "C:\\Users\\test\\OneDrive",
            1,
            1,
            0,
            1,
            false,
            Array.Empty<string>(),
            Array.Empty<TopLevelInventory>(),
            new[] { new DirectoryRisk("C:\\Users\\test\\OneDrive\\repo\\node_modules", "node_modules", "test", 1) },
            Array.Empty<SyncBlocker>());

        var telemetry = new TelemetrySnapshot(
            DateTimeOffset.UtcNow,
            new[] { new ProcessSample("OneDrive", 123, 100, TimeSpan.FromSeconds(1), null, 22.5) },
            0,
            null,
            "test");

        var result = new RiskEngine().Analyze(new[] { inventory }, telemetry, EmptyPressure(), EmptyHealth());

        Assert.Equal(DifferentialDiagnosis.OneDriveLikely, result.Diagnosis);
        Assert.Contains(result.Findings, finding => finding.Title == "OneDrive CPU usage was elevated during the sample");
    }

    [Fact]
    public void AnalyzeDoesNotBlameOneDriveWithoutEvidence()
    {
        var inventory = new InventorySummary(
            "C:\\Users\\test\\OneDrive",
            1,
            1,
            0,
            1,
            false,
            Array.Empty<string>(),
            Array.Empty<TopLevelInventory>(),
            Array.Empty<DirectoryRisk>(),
            Array.Empty<SyncBlocker>());

        var result = new RiskEngine().Analyze(new[] { inventory }, EmptyTelemetry(), EmptyPressure(), EmptyHealth());

        Assert.Equal(DifferentialDiagnosis.OneDriveNotProven, result.Diagnosis);
    }

    [Fact]
    public void AnalyzeAddsKnownRestrictionFindingForSpecificSyncBlockers()
    {
        var inventory = new InventorySummary(
            "C:\\Users\\test\\OneDrive",
            1,
            1,
            0,
            1,
            false,
            Array.Empty<string>(),
            Array.Empty<TopLevelInventory>(),
            Array.Empty<DirectoryRisk>(),
            new[] { new SyncBlocker("C:\\Users\\test\\OneDrive\\bad:name.txt", "invalid-character", "test", Severity.HighRisk) });

        var result = new RiskEngine().Analyze(new[] { inventory }, EmptyTelemetry(), EmptyPressure(), EmptyHealth());

        // Restriction issues still produce their hygiene finding and rename guidance, but they no longer
        // move the lag diagnosis on their own.
        Assert.Equal(DifferentialDiagnosis.OneDriveNotProven, result.Diagnosis);
        Assert.Contains(result.Findings, finding => finding.Title == "Known OneDrive restriction issues were found");
        Assert.Contains(result.Recommendations, recommendation => recommendation.Title == "Rename or shorten items that violate OneDrive restrictions");
    }

    [Fact]
    public void AnalyzeRecommendsSupportedResetForClientHealthWarnings()
    {
        var inventory = new InventorySummary(
            "C:\\Users\\test\\OneDrive",
            1,
            1,
            0,
            1,
            false,
            Array.Empty<string>(),
            Array.Empty<TopLevelInventory>(),
            Array.Empty<DirectoryRisk>(),
            Array.Empty<SyncBlocker>());

        var health = new OneDriveClientHealthSnapshot(
            DateTimeOffset.UtcNow,
            false,
            "test",
            new[] { new ClientHealthSignal(Severity.Warning, "onedrive-log-churn-high", "test", "test") },
            new[] { new OneDriveResetCommand("C:\\Users\\test\\AppData\\Local\\Microsoft\\OneDrive\\OneDrive.exe", "/reset", "test") });

        var result = new RiskEngine().Analyze(new[] { inventory }, EmptyTelemetry(), EmptyPressure(), health);

        // Reset-worthy client-health metadata is a repair signal, not proof that OneDrive caused the lag,
        // so the reset guidance still surfaces while the diagnosis stays honest.
        Assert.Equal(DifferentialDiagnosis.OneDriveNotProven, result.Diagnosis);
        Assert.Contains(result.Findings, finding => finding.Title == "OneDrive client cache reset is worth considering");
        Assert.Contains(result.Recommendations, recommendation => recommendation.Kind == RecommendationKind.ResetOneDrive);
    }

    [Fact]
    public void AnalyzeClassifiesRecentEventLogsAsNonOneDrivePressure()
    {
        var inventory = new InventorySummary(
            "C:\\Users\\test\\OneDrive",
            1,
            1,
            0,
            1,
            false,
            Array.Empty<string>(),
            Array.Empty<TopLevelInventory>(),
            Array.Empty<DirectoryRisk>(),
            Array.Empty<SyncBlocker>());

        var eventLogs = new[]
        {
            new EventLogSummary("System", "Disk", 153, "Warning", 3, DateTimeOffset.UtcNow)
        };

        var result = new RiskEngine().Analyze(new[] { inventory }, EmptyTelemetry(), EmptyPressure(), EmptyHealth(), eventLogs);

        Assert.Equal(DifferentialDiagnosis.NonOneDrivePressureSuspected, result.Diagnosis);
        Assert.Contains(result.Findings, finding => finding.Title == "Recent Windows reliability events were observed");
        Assert.Contains(result.Recommendations, recommendation => recommendation.Kind == RecommendationKind.EscalateToEventViewer);
    }

    [Fact]
    public void AnalyzeClassifiesWholeSystemPressureAsNonOneDrivePressure()
    {
        var inventory = new InventorySummary(
            "C:\\Users\\test\\OneDrive",
            1,
            1,
            0,
            1,
            false,
            Array.Empty<string>(),
            Array.Empty<TopLevelInventory>(),
            Array.Empty<DirectoryRisk>(),
            Array.Empty<SyncBlocker>());
        var pressure = new SystemPressureSnapshot(
            DateTimeOffset.UtcNow,
            "processor=40%;queue=0",
            "available=512MB;commit=91%",
            "queue=0;active=0%",
            "source=ac",
            Array.Empty<string>(),
            "windows-pdh-process-and-win32-memory-snapshot",
            new[]
            {
                new PerformanceSignal("memory-available-mb", 512, "megabytes", "test"),
                new PerformanceSignal("memory-commit-percent", 91, "percent", "test")
            },
            Array.Empty<ProcessPressureSample>());

        var result = new RiskEngine().Analyze(new[] { inventory }, EmptyTelemetry(), pressure, EmptyHealth());

        Assert.Equal(DifferentialDiagnosis.NonOneDrivePressureSuspected, result.Diagnosis);
        Assert.Contains(result.Findings, finding => finding.Title == "Whole-system performance pressure was observed");
        Assert.Contains(result.Recommendations, recommendation => recommendation.Title == "Correlate system pressure before changing OneDrive data");
    }

    private static TelemetrySnapshot EmptyTelemetry()
    {
        return new TelemetrySnapshot(DateTimeOffset.UtcNow, Array.Empty<ProcessSample>(), 0, null, "test");
    }

    private static SystemPressureSnapshot EmptyPressure()
    {
        return new SystemPressureSnapshot(DateTimeOffset.UtcNow, "unknown", "unknown", "unknown", "unknown", Array.Empty<string>(), "test");
    }

    private static OneDriveClientHealthSnapshot EmptyHealth()
    {
        return new OneDriveClientHealthSnapshot(DateTimeOffset.UtcNow, false, "test", Array.Empty<ClientHealthSignal>(), Array.Empty<OneDriveResetCommand>());
    }
}

using OneLag.Core;

namespace OneLag.Core.Tests;

public sealed class RiskEngineTests
{
    [Fact]
    public void AnalyzeClassifiesHighRiskInventoryAsOneDrivePossible()
    {
        var inventory = new InventorySummary(
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

        var result = new RiskEngine().Analyze(
            new[] { inventory },
            EmptyTelemetry(),
            EmptyPressure(),
            EmptyHealth());

        Assert.Equal(DifferentialDiagnosis.OneDrivePossible, result.Diagnosis);
        Assert.Contains(result.Findings, finding => finding.Severity == Severity.HighRisk);
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

        Assert.Equal(DifferentialDiagnosis.OneDrivePossible, result.Diagnosis);
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

        Assert.Equal(DifferentialDiagnosis.OneDrivePossible, result.Diagnosis);
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

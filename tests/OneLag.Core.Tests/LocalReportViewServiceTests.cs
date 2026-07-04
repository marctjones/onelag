using OneLag.Core;

namespace OneLag.Core.Tests;

public sealed class LocalReportViewServiceTests
{
    [Fact]
    public void SummarizesDiagnosticJsonReport()
    {
        var tempRoot = CreateTempRoot();
        try
        {
            var report = new DiagnosticReport(
                DateTimeOffset.Parse("2026-07-04T12:00:00Z"),
                DateTimeOffset.Parse("2026-07-04T12:01:00Z"),
                new[] { new RootCandidate(Path.Combine(tempRoot, "OneDrive"), "test", "high", "personal") },
                new[]
                {
                    new InventorySummary(
                        Path.Combine(tempRoot, "OneDrive"),
                        10,
                        2,
                        1234,
                        3,
                        false,
                        Array.Empty<string>(),
                        Array.Empty<TopLevelInventory>(),
                        new[] { new DirectoryRisk(Path.Combine(tempRoot, "OneDrive", "node_modules"), "node_modules", "development dependency cache", 1000) },
                        new[] { new SyncBlocker(Path.Combine(tempRoot, "OneDrive", "bad.tmp"), "temporary-file", "Temporary file", Severity.Warning) })
                },
                new TelemetrySnapshot(DateTimeOffset.Parse("2026-07-04T12:00:10Z"), Array.Empty<ProcessSample>(), 0, null, "available"),
                new SystemPressureSnapshot(DateTimeOffset.Parse("2026-07-04T12:00:10Z"), "normal", "normal", "normal", "ac", Array.Empty<string>(), "available"),
                new OneDriveClientHealthSnapshot(DateTimeOffset.Parse("2026-07-04T12:00:10Z"), false, "available", Array.Empty<ClientHealthSignal>(), Array.Empty<OneDriveResetCommand>()),
                new[] { new EventLogSummary("System", "Disk", 153, "Warning", 2, DateTimeOffset.Parse("2026-07-04T12:00:30Z")) },
                DifferentialDiagnosis.OneDrivePossible,
                new[] { new Finding(Severity.Warning, "High-risk directory", "node_modules was found.", "high") },
                new[] { new Recommendation(RecommendationKind.MoveOutOfOneDrive, "Move development folders", "Dependency caches churn heavily.", "Review before moving.") });

            var path = Path.Combine(tempRoot, "report.json");
            File.WriteAllText(path, ReportWriter.ToJson(report));

            var summary = new LocalReportViewService().Summarize(path);

            Assert.Equal(LocalReportKind.Diagnostic, summary.Kind);
            Assert.Contains("Diagnosis: OneDrivePossible", summary.KeyFacts);
            Assert.Contains(summary.KeyFacts, fact => fact == "High-risk directories: 1");
            Assert.Contains(summary.Timeline, item => item.Kind == "windows-event" && item.Timestamp.HasValue);
            Assert.Contains(summary.NextActions, action => action.Contains("Move development folders", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void SummarizesWatchMarkdownTimeline()
    {
        var tempRoot = CreateTempRoot();
        try
        {
            var path = Path.Combine(tempRoot, "watch.md");
            File.WriteAllText(path, """
                # OneLag Watch Report

                - Samples: `2`
                - Markers: `1`
                - Max timer drift: `700.0 ms`

                ## Markers
                - `2026-07-04T12:00:00.0000000+00:00` from `cli`: lag

                ## Episodes
                - `2026-07-04T12:00:00.0000000+00:00` to `2026-07-04T12:00:02.0000000+00:00` `OneDrivePossible` confidence `medium`: OneDrive log churn 8/min

                ## Largest Timer Delays
                - `2026-07-04T12:00:02.0000000+00:00` drift `700.0 ms`, foreground `word`, telemetry `available`
                """);

            var summary = new LocalReportViewService().Summarize(path);

            Assert.Equal(LocalReportKind.Watch, summary.Kind);
            Assert.Contains("Samples: 2", summary.KeyFacts);
            Assert.Contains(summary.Timeline, item => item.Kind == "lag-marker");
            Assert.Contains(summary.Timeline, item => item.Kind == "lag-episode" && item.Summary == "OneDrivePossible episode");
            Assert.Contains(summary.NextActions, action => action.Contains("fresh scan", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static string CreateTempRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "onelag-report-view-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}

using OneLag.Core;

namespace OneLag.Core.Tests;

public sealed class SupportBundleWriterTests : IDisposable
{
    private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "onelag-support-bundle-tests", Guid.NewGuid().ToString("N"));

    public SupportBundleWriterTests()
    {
        Directory.CreateDirectory(tempRoot);
    }

    [Fact]
    public void WritesBundleWithReportsSummariesTracePlanManifestAndZip()
    {
        var reportPath = WriteDiagnosticMarkdown("diagnostic.md");
        var output = Path.Combine(tempRoot, "bundle");

        var result = new SupportBundleWriter(versionProvider: _ => "OneLag test")
            .Write(new SupportBundleOptions(
                output,
                new[] { reportPath },
                IncludeTracePlan: true,
                CreateZip: true,
                UserNote: "typing froze in Word",
                OneDriveStatus: "processing changes"));

        Assert.Equal(output, result.OutputDirectory);
        Assert.Equal(output + ".zip", result.ZipPath);
        Assert.True(File.Exists(Path.Combine(output, "README.md")));
        Assert.True(File.Exists(Path.Combine(output, "ANALYZE_WITH_CODEX_OR_CLAUDE.md")));
        Assert.True(File.Exists(Path.Combine(output, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(output, "privacy-checklist.md")));
        Assert.True(File.Exists(Path.Combine(output, "user-notes.md")));
        Assert.True(File.Exists(Path.Combine(output, "environment.md")));
        Assert.True(File.Exists(Path.Combine(output, "reports", "001-diagnostic.md")));
        Assert.True(File.Exists(Path.Combine(output, "reports", "001-diagnostic.summary.md")));
        Assert.True(File.Exists(Path.Combine(output, "trace-plan", "README.md")));
        Assert.True(File.Exists(output + ".zip"));
        Assert.Contains("Codex or Claude Code", File.ReadAllText(Path.Combine(output, "ANALYZE_WITH_CODEX_OR_CLAUDE.md")));
        Assert.Contains("typing froze in Word", File.ReadAllText(Path.Combine(output, "user-notes.md")));
        Assert.Contains("OneLag test", File.ReadAllText(Path.Combine(output, "manifest.json")));
        Assert.Contains("001-diagnostic.md", File.ReadAllText(Path.Combine(output, "manifest.json")));
        Assert.Contains("sha256", File.ReadAllText(Path.Combine(output, "manifest.json")), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reports/001-diagnostic.summary.md", result.Files);
    }

    [Fact]
    public void RefusesNonEmptyOutputDirectoryUnlessOverwriteIsExplicit()
    {
        var reportPath = WriteDiagnosticMarkdown("diagnostic.md");
        var output = Path.Combine(tempRoot, "bundle");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "existing.txt"), "keep");

        var error = Assert.Throws<IOException>(() => new SupportBundleWriter()
            .Write(new SupportBundleOptions(output, new[] { reportPath })));

        Assert.Contains("--overwrite", error.Message);
        Assert.True(File.Exists(Path.Combine(output, "existing.txt")));
    }

    [Fact]
    public void CanGenerateWatchReportFromWatchDirectory()
    {
        var watchDirectory = Path.Combine(tempRoot, "watch");
        new WatchService().Mark(watchDirectory, "test", "lag happened now");
        var output = Path.Combine(tempRoot, "bundle");

        var result = new SupportBundleWriter()
            .Write(new SupportBundleOptions(output, Array.Empty<string>(), WatchDirectory: watchDirectory));

        Assert.True(File.Exists(Path.Combine(output, "reports", "watch-generated-report.md")));
        Assert.True(File.Exists(Path.Combine(output, "reports", "watch-generated-report.summary.md")));
        Assert.Contains("watch-generated-report.md", File.ReadAllText(Path.Combine(output, "manifest.json")));
        Assert.Contains("reports/watch-generated-report.summary.md", result.Files);
        Assert.Contains("lag happened now", File.ReadAllText(Path.Combine(output, "reports", "watch-generated-report.md")));
    }

    public void Dispose()
    {
        if (Directory.Exists(tempRoot))
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private string WriteDiagnosticMarkdown(string fileName)
    {
        var path = Path.Combine(tempRoot, fileName);
        File.WriteAllText(path, """
            # OneLag Diagnostic Report

            - Started: `2026-07-05T10:00:00.0000000+00:00`
            - Finished: `2026-07-05T10:01:00.0000000+00:00`
            - Diagnosis: `OneDrivePossible`
            - Telemetry: `windows-process-cpu-and-log-metadata`
            - System pressure: `windows-pdh-process-and-win32-memory-snapshot`

            ## Findings

            - Warning: Recent Windows reliability events were observed. Evidence: Event Viewer had recent warnings. Confidence: medium.

            ## Recommendations

            - **Review evidence** (`Observe`): Review before changing files. Safety: No data is modified.
            """);
        return path;
    }
}

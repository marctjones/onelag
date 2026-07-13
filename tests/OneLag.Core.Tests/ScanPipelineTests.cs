using OneLag.Core;

namespace OneLag.Core.Tests;

/// <summary>
/// Whole-pipeline tests over a simulated Windows machine: discovery, inventory, telemetry, pressure, host
/// context, shell, driver trace, ranking, and report rendering. ScanRunner previously had no coverage at all,
/// so nothing checked that the evidence a probe returns actually reaches the verdict a user reads.
/// </summary>
public sealed class ScanPipelineTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "onelag-scan-pipeline", Guid.NewGuid().ToString("N"));

    public ScanPipelineTests()
    {
        Directory.CreateDirectory(root);
    }

    [Fact]
    public void AHealthyIdleMachineIsNotBlamedOnAnything()
    {
        WriteTree(files: 40);
        var probe = Probe();

        var report = Run(probe);

        Assert.Equal(DifferentialDiagnosis.OneDriveNotProven, report.Diagnosis);
        Assert.NotNull(report.Hypotheses);
        Assert.All(
            report.Hypotheses!,
            hypothesis => Assert.True(
                hypothesis.Verdict is HypothesisVerdict.NotSupported or HypothesisVerdict.Unknown or HypothesisVerdict.RuledOut,
                $"{hypothesis.Kind} was {hypothesis.Verdict} on a healthy machine"));
    }

    [Fact]
    public void AThrashingOneDriveWithAHungShellIsBlamedOnOneDrive()
    {
        WriteTree(files: 40, devDirectory: true);
        var probe = Probe();
        probe.Telemetry = Snapshots.ThrashingOneDrive();
        probe.Pressure = Snapshots.SaturatedDisk();
        probe.Shell = Snapshots.HungShell();

        var report = Run(probe);

        Assert.Equal(DifferentialDiagnosis.OneDriveLikely, report.Diagnosis);

        var oneDrive = report.Hypotheses!.Single(hypothesis => hypothesis.Kind == HypothesisKind.OneDriveSync);
        Assert.True(oneDrive.Verdict is HypothesisVerdict.Likely or HypothesisVerdict.StronglySupported);
        Assert.Contains(oneDrive.Supporting, evidence => evidence.Contains("log files changed", StringComparison.Ordinal));
        Assert.Contains(report.Recommendations, recommendation => recommendation.Kind == RecommendationKind.PauseSync);
    }

    [Fact]
    public void ADockedMachineWithAPinnedCoreIsBlamedOnTheDockNotOneDrive()
    {
        // The case this project exists to get right: identical OneDrive, but the lag follows the dock.
        WriteTree(files: 40, devDirectory: true);
        var probe = Probe();
        probe.Pressure = Snapshots.PinnedCoreInterruptPressure();
        probe.HostContext = Snapshots.DockedHostWithDisplayLink();

        var report = Run(probe);

        Assert.Equal(DifferentialDiagnosis.NonOneDrivePressureSuspected, report.Diagnosis);

        var top = report.Hypotheses![0];
        Assert.Contains(top.Kind, new[] { HypothesisKind.DisplayOrDockPipeline, HypothesisKind.DriverInterruptLatency, HypothesisKind.BluetoothOrInputRadio });

        var oneDrive = report.Hypotheses!.Single(hypothesis => hypothesis.Kind == HypothesisKind.OneDriveSync);
        Assert.True(top.Score > oneDrive.Score);
        Assert.Contains(report.Findings, finding => finding.Title == "Kernel interrupt or DPC latency is elevated");
    }

    [Fact]
    public void AKernelTraceNamesTheDriverAndOnlyRunsWhenAsked()
    {
        WriteTree(files: 40);

        var withoutTrace = Probe();
        Run(withoutTrace);
        Assert.Equal(0, withoutTrace.DriverTraceCalls);

        var withTrace = Probe();
        withTrace.Pressure = Snapshots.PinnedCoreInterruptPressure();
        withTrace.HostContext = Snapshots.DockedHostWithDisplayLink();
        withTrace.DriverLatency = Snapshots.DisplayLinkStorm();

        var report = Run(withTrace, traceDrivers: TimeSpan.FromSeconds(30));

        Assert.Equal(1, withTrace.DriverTraceCalls);

        var display = report.Hypotheses!.Single(hypothesis => hypothesis.Kind == HypothesisKind.DisplayOrDockPipeline);
        Assert.Contains(display.Supporting, evidence => evidence.Contains("dlkmdldr.sys", StringComparison.Ordinal));
        Assert.Contains("dlkmdldr.sys", display.NextStep, StringComparison.Ordinal);

        // The core OS image accumulated more raw DPC time than the culprit, and must never be named.
        Assert.DoesNotContain("ntoskrnl", display.NextStep, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(report.Findings, finding => finding.Title == "A kernel driver was named as the source of high-IRQL time");
    }

    [Fact]
    public void TheRenderedReportLeadsWithEvidenceQualityAndRankedCauses()
    {
        WriteTree(files: 40);
        var probe = Probe();
        probe.Pressure = Snapshots.PinnedCoreInterruptPressure();
        probe.HostContext = Snapshots.DockedHostWithDisplayLink();

        var report = Run(probe);
        var markdown = ReportWriter.ToMarkdown(report, new Redactor(fullPaths: false, report.Roots.Select(candidate => candidate.Path)));

        var qualityIndex = markdown.IndexOf("## Evidence Quality", StringComparison.Ordinal);
        var causesIndex = markdown.IndexOf("## Ranked Causes", StringComparison.Ordinal);
        var inventoryIndex = markdown.IndexOf("## Inventory", StringComparison.Ordinal);

        Assert.True(qualityIndex >= 0 && causesIndex >= 0 && inventoryIndex >= 0);
        Assert.True(qualityIndex < causesIndex, "evidence quality must come before the verdict");
        Assert.True(causesIndex < inventoryIndex, "the ranked causes must come before the OneDrive inventory");

        Assert.Contains("## Host Context", markdown, StringComparison.Ordinal);
        Assert.Contains("indirect-wired-usb", markdown, StringComparison.Ordinal);
        Assert.Contains("## Explorer Shell Responsiveness", markdown, StringComparison.Ordinal);

        // OneDrive is always written out, even when rejected: the reasons against it are the point.
        Assert.Contains("### OneDriveSync", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void AProbeThatMeasuredNothingProducesAnInsufficientReportRatherThanAVerdict()
    {
        WriteTree(files: 40);

        // A machine where every collector degraded: exactly the report that came back from the real laptop.
        var probe = new FakePlatformProbe
        {
            Telemetry = new TelemetrySnapshot(Snapshots.Now, Array.Empty<ProcessSample>(), 0, null, "unavailable-on-this-platform"),
            Pressure = new SystemPressureSnapshot(Snapshots.Now, "unknown", "unknown", "unknown", "unknown", Array.Empty<string>(), "portable-fallback"),
            HostContext = HostContext.Unavailable("unavailable-on-this-platform"),
            Shell = ShellResponsiveness.Unavailable("unavailable-on-this-platform")
        };

        var report = Run(probe);

        Assert.Equal(EvidenceGrade.Insufficient, report.EvidenceQuality!.Grade);
        Assert.Equal(DifferentialDiagnosis.OneDriveNotProven, report.Diagnosis);
        Assert.Contains(report.Findings, finding => finding.Title == "This capture contains too little live evidence to diagnose anything");
        Assert.Contains(report.Recommendations, recommendation => recommendation.Title == "Capture evidence while the machine is actually lagging");
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private FakePlatformProbe Probe()
    {
        return new FakePlatformProbe
        {
            Roots = new[] { new RootCandidate(root, "test", "high", "work-or-school") }
        };
    }

    private DiagnosticReport Run(FakePlatformProbe probe, TimeSpan? traceDrivers = null)
    {
        var runner = new ScanRunner(probe, new InventoryScanner(), new RiskEngine());
        var options = new ScanOptions(
            Array.Empty<string>(),
            Path.Combine(root, "report.md"),
            "markdown",
            FullPaths: false,
            MaxItems: 100_000,
            DriverTraceDuration: traceDrivers);

        return runner.Run(options, CancellationToken.None);
    }

    private void WriteTree(int files, bool devDirectory = false)
    {
        var documents = Directory.CreateDirectory(Path.Combine(root, "Documents"));
        for (var index = 0; index < files; index++)
        {
            File.WriteAllText(Path.Combine(documents.FullName, $"file-{index}.txt"), "content");
        }

        if (devDirectory)
        {
            var modules = Directory.CreateDirectory(Path.Combine(root, "repo", "node_modules"));
            File.WriteAllText(Path.Combine(modules.FullName, "index.js"), "module.exports = {};");
        }
    }
}

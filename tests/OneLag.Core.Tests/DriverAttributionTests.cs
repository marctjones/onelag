using OneLag.Core;

namespace OneLag.Core.Tests;

public sealed class DriverAttributionTests
{
    [Fact]
    public void ClassifyMapsDisplayLinkToTheDisplayAndDockHypothesis()
    {
        var (kind, subsystem) = DriverClassifier.Classify("dlkmdldr.sys");

        Assert.Equal(HypothesisKind.DisplayOrDockPipeline, kind);
        Assert.Contains("DisplayLink", subsystem!, StringComparison.Ordinal);
    }

    [Fact]
    public void ClassifyMapsWifiToTheBluetoothCoexistenceHypothesis()
    {
        // 2.4 GHz coexistence: the Wi-Fi radio and the Bluetooth peripherals share the band, and the Wi-Fi
        // driver is where the DPC time lands.
        var (kind, _) = DriverClassifier.Classify("Netwtw10.sys");

        Assert.Equal(HypothesisKind.BluetoothOrInputRadio, kind);
    }

    [Fact]
    public void SignificantDropsNoiseAndUnresolvedAddresses()
    {
        var attribution = new DriverLatencyAttribution(
            DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(30),
            new[]
            {
                // Unresolved addresses and the core OS images are not actionable: telling someone to roll
                // back ntoskrnl.exe is not advice.
                new DriverLatencySample("unresolved", "dpc", 900, 0.1, 9000),
                new DriverLatencySample("ntoskrnl.exe", "dpc", 800, 2.4, 40_000),
                new DriverLatencySample("dlkmdldr.sys", "dpc", 640, 3.8, 1200)
            },
            "windows-kernel-etw-dpc-isr");

        var driver = Assert.Single(DriverClassifier.Significant(attribution));
        Assert.Equal("dlkmdldr.sys", driver.Driver);
    }

    [Fact]
    public void SignificantDoesNotManufactureACulpritOnAHealthyMachine()
    {
        // Every machine accumulates a little DPC time. An absolute floor would flag these as significant on
        // a completely healthy laptop and produce a culprit on every single run.
        var attribution = new DriverLatencyAttribution(
            DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(30),
            new[]
            {
                new DriverLatencySample("storport.sys", "dpc", 42, 0.4, 8_000),
                new DriverLatencySample("dxgkrnl.sys", "dpc", 30, 0.6, 5_000),
                new DriverLatencySample("ndis.sys", "isr", 11, 0.2, 3_000)
            },
            "windows-kernel-etw-dpc-isr");

        Assert.Empty(DriverClassifier.Significant(attribution));
    }

    [Fact]
    public void SignificantCountsADriverOnceWhenItHasBothDpcAndIsrTime()
    {
        var attribution = new DriverLatencyAttribution(
            DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(30),
            new[]
            {
                new DriverLatencySample("dlkmdldr.sys", "dpc", 400, 3.8, 1200),
                new DriverLatencySample("dlkmdldr.sys", "isr", 200, 1.4, 900)
            },
            "windows-kernel-etw-dpc-isr");

        var driver = Assert.Single(DriverClassifier.Significant(attribution));
        Assert.Equal(600, driver.TotalMilliseconds);
        Assert.Equal(3.8, driver.MaxMilliseconds);
        Assert.Equal("dpc+isr", driver.Kind);
    }

    [Fact]
    public void ANamedDriverPromotesTheSubsystemThatOwnsItAboveOneDrive()
    {
        var inventory = new InventorySummary(
            "C:\\Users\\test\\OneDrive",
            250_000,
            5_000,
            0,
            6,
            false,
            Array.Empty<string>(),
            Array.Empty<TopLevelInventory>(),
            new[] { new DirectoryRisk("C:\\Users\\test\\OneDrive\\repo\\node_modules", "node_modules", "test", 1) },
            Array.Empty<SyncBlocker>());

        var driverLatency = new DriverLatencyAttribution(
            DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(30),
            new[] { new DriverLatencySample("dlkmdldr.sys", "dpc", 310, 4.2, 1500) },
            "windows-kernel-etw-dpc-isr");

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
            EmptyPressure(),
            EmptyHealth(),
            Array.Empty<EventLogSummary>(),
            host,
            shell: null,
            driverLatency: driverLatency);

        var display = result.Hypotheses.Single(hypothesis => hypothesis.Kind == HypothesisKind.DisplayOrDockPipeline);
        var oneDrive = result.Hypotheses.Single(hypothesis => hypothesis.Kind == HypothesisKind.OneDriveSync);

        Assert.True(display.Score > oneDrive.Score);
        Assert.Contains(display.Supporting, evidence => evidence.Contains("dlkmdldr.sys", StringComparison.Ordinal));
        Assert.Contains("dlkmdldr.sys", display.NextStep, StringComparison.Ordinal);
        Assert.Equal(DifferentialDiagnosis.NonOneDrivePressureSuspected, result.Diagnosis);
        Assert.Contains(result.Findings, finding => finding.Title == "A kernel driver was named as the source of high-IRQL time");

        // A direct measurement must retire the "this could not be measured" evidence, or the report would
        // call the same hypothesis strongly supported and untested in the same breath.
        var driverLatencyHypothesis = result.Hypotheses.Single(hypothesis => hypothesis.Kind == HypothesisKind.DriverInterruptLatency);
        Assert.DoesNotContain(driverLatencyHypothesis.Opposing, evidence => evidence.Contains("untested", StringComparison.OrdinalIgnoreCase));
        Assert.NotEqual(HypothesisVerdict.Unknown, driverLatencyHypothesis.Verdict);
        Assert.DoesNotContain(display.Supporting, evidence => evidence.EndsWith(" - .", StringComparison.Ordinal));
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
        return new OneDriveClientHealthSnapshot(
            DateTimeOffset.UtcNow,
            false,
            "test",
            Array.Empty<ClientHealthSignal>(),
            Array.Empty<OneDriveResetCommand>());
    }
}

using OneLag.Core;
using OneLag.Windows;

namespace OneLag.Windows.Tests;

/// <summary>
/// Drives realistic Windows event XML through the parser and on into the differential, so the chain that
/// actually matters is covered end to end: Windows renders an event, OneLag parses it, and a hypothesis
/// moves because of it. Testing the parser alone would prove that events are read, not that they are used.
/// </summary>
public sealed class WindowsEventEvidenceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-13T10:00:00Z");

    [Fact]
    public void ADisplayDriverResetReachesTheDisplayAndDockHypothesis()
    {
        var xml = string.Concat(
            WindowsEventFixtures.DisplayDriverReset(Now.AddMinutes(-3)),
            WindowsEventFixtures.DisplayDriverReset(Now.AddMinutes(-1)));

        var summaries = EventLogXmlParser.Parse("System", xml);

        var display = Assert.Single(summaries);
        Assert.Equal("Display", display.Provider);
        Assert.Equal(4101, display.EventId);
        Assert.Equal("Warning", display.Level);
        Assert.Equal(2, display.Count);

        var result = Analyze(summaries, ExternalDisplayHost());

        var hypothesis = result.Hypotheses.Single(candidate => candidate.Kind == HypothesisKind.DisplayOrDockPipeline);
        Assert.Contains(hypothesis.Supporting, evidence => evidence.Contains("display-driver event", StringComparison.Ordinal));
        Assert.True(hypothesis.Score >= 20);
    }

    [Fact]
    public void AClassicProviderRenderedWithOnlyAGuidIsNotLost()
    {
        // A provider reported as "unknown" is invisible to every hypothesis that matches on provider name.
        var summaries = EventLogXmlParser.Parse("System", WindowsEventFixtures.GuidOnlyProvider(Now));

        var summary = Assert.Single(summaries);
        Assert.NotEqual("unknown", summary.Provider);
        Assert.Contains("9c205a39", summary.Provider, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AClassicProviderWithQualifiersOnTheEventIdParsesTheIdNotTheQualifier()
    {
        var summaries = EventLogXmlParser.Parse("System", WindowsEventFixtures.DiskIoRetried(Now));

        var summary = Assert.Single(summaries);
        Assert.Equal(153, summary.EventId);
        Assert.Equal("disk", summary.Provider);
        Assert.Equal("Warning", summary.Level);
    }

    [Fact]
    public void ARecordWithNoLevelOrTimestampDoesNotThrowOrInventEvidence()
    {
        var summaries = EventLogXmlParser.Parse("Application", WindowsEventFixtures.MissingLevelAndTime());

        var summary = Assert.Single(summaries);
        Assert.Equal("Unknown", summary.Level);
        Assert.Null(summary.NewestTimestamp);

        // "Unknown" is not Critical, Error, or Warning, so it must not be counted as a reliability event.
        var result = Analyze(summaries, ExternalDisplayHost());
        Assert.DoesNotContain(result.Findings, finding => finding.Title == "Recent Windows reliability events were observed");
    }

    [Fact]
    public void MalformedXmlYieldsNoEventsRatherThanThrowing()
    {
        Assert.Empty(EventLogXmlParser.Parse("System", WindowsEventFixtures.Malformed()));
        Assert.Empty(EventLogXmlParser.Parse("System", string.Empty));
    }

    [Fact]
    public void ThermalAndProcessorPowerEventsReachTheThrottlingHypothesis()
    {
        var xml = string.Concat(
            WindowsEventFixtures.WheaCorrectedError(Now.AddMinutes(-5)),
            WindowsEventFixtures.KernelPower(Now.AddMinutes(-2)));

        var summaries = EventLogXmlParser.Parse("System", xml);
        Assert.Equal(2, summaries.Count);

        var result = Analyze(summaries, ExternalDisplayHost());

        var thermal = result.Hypotheses.Single(candidate => candidate.Kind == HypothesisKind.ThermalOrPowerThrottling);
        Assert.Contains(thermal.Supporting, evidence => evidence.Contains("hardware-error", StringComparison.Ordinal));
    }

    [Fact]
    public void ABluetoothTransportErrorIsCarriedThroughAsAWarning()
    {
        var summaries = EventLogXmlParser.Parse("System", WindowsEventFixtures.BluetoothTransportError(Now));

        var summary = Assert.Single(summaries);
        Assert.Equal("BTHUSB", summary.Provider);
        Assert.Equal("Error", summary.Level);

        var result = Analyze(summaries, ExternalDisplayHost());
        Assert.Contains(result.Findings, finding => finding.Title == "Recent Windows reliability events were observed");
    }

    private static RiskAnalysis Analyze(IReadOnlyList<EventLogSummary> eventLogs, HostContext host)
    {
        var inventory = new InventorySummary(
            "C:\\Users\\test\\OneDrive",
            10,
            2,
            0,
            1,
            false,
            Array.Empty<string>(),
            Array.Empty<TopLevelInventory>(),
            Array.Empty<DirectoryRisk>(),
            Array.Empty<SyncBlocker>());

        return new RiskEngine().Analyze(
            new[] { inventory },
            new TelemetrySnapshot(Now, Array.Empty<ProcessSample>(), 0, null, "test"),
            new SystemPressureSnapshot(Now, "normal", "normal", "normal", "ac", Array.Empty<string>(), "test"),
            new OneDriveClientHealthSnapshot(Now, false, "test", Array.Empty<ClientHealthSignal>(), Array.Empty<OneDriveResetCommand>()),
            eventLogs,
            host);
    }

    private static HostContext ExternalDisplayHost()
    {
        return new HostContext(
            Now,
            2,
            1,
            0,
            new[] { new DisplayInfo("Dell U2720Q", "displayport-external", false, false, 3840, 2160, 60) },
            true,
            true,
            1,
            "source=ac;battery=100%",
            true,
            Array.Empty<string>(),
            DockStates.DockedLikely,
            "test");
    }
}

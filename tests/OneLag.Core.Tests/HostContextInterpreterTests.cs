using OneLag.Core;

namespace OneLag.Core.Tests;

/// <summary>
/// Validates the display/dock/Bluetooth interpretation for the hardware the CI runner cannot produce.
///
/// A headless Windows Server VM has no dock, no external monitors, and no DisplayLink driver, so the code
/// that recognises an indirect/USB display and concludes "docked" never executes on the runner. These feed
/// the interpreter the exact output-technology values a real DisplayLink dock, a Thunderbolt monitor, or a
/// laptop eDP panel produces, so the classification your laptop depends on is covered without the hardware.
/// The struct marshalling that produces these values is pinned separately by the interop-layout tests and
/// validated live on the runner.
/// </summary>
public sealed class HostContextInterpreterTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-13T10:00:00Z");
    private static readonly RawBluetooth BluetoothOff = new(false, 0, "bluetooth-radio-absent-or-disabled");

    [Fact]
    public void ADisplayLinkDockMonitorIsClassifiedAsIndirectUsbAndDocked()
    {
        var host = Build(
            new RawDisplay(HostContextInterpreter.Internal, "Built-in", 2880, 1800, 120),
            new RawDisplay(HostContextInterpreter.IndirectWired, "Dell U2720Q", 3840, 2160, 60));

        Assert.Equal(2, host.DisplayCount);
        Assert.Equal(1, host.IndirectDisplayCount);
        Assert.Equal(0, host.ExternalDisplayCount);
        Assert.Equal(DockStates.DockedLikely, host.DockState);

        var indirect = host.Displays.Single(display => display.IsIndirect);
        Assert.Equal("indirect-wired-usb", indirect.OutputTechnology);
        Assert.False(indirect.IsInternal);
    }

    [Fact]
    public void AThunderboltMonitorIsClassifiedAsExternalAndDocked()
    {
        var host = Build(
            new RawDisplay(HostContextInterpreter.Internal, "Built-in", 2880, 1800, 120),
            new RawDisplay(HostContextInterpreter.DisplayPortExternal, "LG UltraFine", 3840, 2160, 60));

        Assert.Equal(1, host.ExternalDisplayCount);
        Assert.Equal(0, host.IndirectDisplayCount);
        Assert.Equal(DockStates.DockedLikely, host.DockState);
        Assert.Equal("displayport-external", host.Displays[1].OutputTechnology);
    }

    [Theory]
    [InlineData(HostContextInterpreter.Internal)]
    [InlineData(HostContextInterpreter.DisplayPortEmbedded)]
    [InlineData(HostContextInterpreter.UdiEmbedded)]
    [InlineData(HostContextInterpreter.Lvds)]
    public void EveryFormOfBuiltInPanelCountsAsInternalNotExternal(uint technology)
    {
        // The regression this guards: a laptop eDP panel reporting DISPLAYPORT_EMBEDDED instead of INTERNAL
        // was counted as an external monitor, reporting an undocked laptop as docked.
        var host = Build(new RawDisplay(technology, "Built-in", 1920, 1080, 60));

        Assert.True(host.Displays[0].IsInternal);
        Assert.Equal(0, host.ExternalDisplayCount);
        Assert.Equal(0, host.IndirectDisplayCount);
    }

    [Fact]
    public void AnUndockedLaptopOnBatteryIsUndocked()
    {
        var host = HostContextInterpreter.Build(
            Now,
            new[] { new RawDisplay(HostContextInterpreter.Internal, "Built-in", 2880, 1800, 120) },
            BluetoothOff,
            wiredUp: false,
            powerState: "source=battery;battery=72%",
            indirectDrivers: Array.Empty<string>(),
            displayEvidence: "display-config-paths-1");

        Assert.Equal(DockStates.UndockedLikely, host.DockState);
    }

    [Fact]
    public void ADockWithWiredEthernetOnAcButNoExternalDisplayIsStillDocked()
    {
        // A closed-lid dock driving only external monitors, or a dock whose displays are off, still shows up
        // as a wired NIC on AC power.
        var host = HostContextInterpreter.Build(
            Now,
            new[] { new RawDisplay(HostContextInterpreter.Internal, "Built-in", 2880, 1800, 120) },
            BluetoothOff,
            wiredUp: true,
            powerState: "source=ac;battery=100%",
            indirectDrivers: Array.Empty<string>(),
            displayEvidence: "display-config-paths-1");

        Assert.Equal(DockStates.DockedLikely, host.DockState);
    }

    [Fact]
    public void MixedRefreshRatesArePreservedPerDisplay()
    {
        // The hypothesis engine adds risk when attached displays run at mixed refresh rates; the per-display
        // rate has to survive interpretation for that to fire.
        var host = Build(
            new RawDisplay(HostContextInterpreter.Internal, "Built-in", 2880, 1800, 120),
            new RawDisplay(HostContextInterpreter.DisplayPortExternal, "External", 3840, 2160, 60));

        var rates = host.Displays.Select(display => Math.Round(display.RefreshHz)).OrderBy(rate => rate).ToArray();
        Assert.Equal(new[] { 60.0, 120.0 }, rates);
    }

    [Fact]
    public void AConnectedBluetoothMouseIsCarriedIntoHostContext()
    {
        var host = HostContextInterpreter.Build(
            Now,
            new[] { new RawDisplay(HostContextInterpreter.Internal, "Built-in", 2880, 1800, 120) },
            new RawBluetooth(true, 2, "cfgmgr-pnp-classic-and-le;present=2"),
            wiredUp: false,
            powerState: "source=battery;battery=80%",
            indirectDrivers: Array.Empty<string>(),
            displayEvidence: "display-config-paths-1");

        Assert.True(host.BluetoothRadioPresent);
        Assert.True(host.BluetoothRadioEnabled);
        Assert.Equal(2, host.ConnectedBluetoothDevices);
        Assert.Contains("cfgmgr-pnp-classic-and-le", host.EvidenceState, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownDisplayNameFallsBackToAStableLabel()
    {
        var host = Build(new RawDisplay(HostContextInterpreter.Hdmi, null, 1920, 1080, 60));
        Assert.Equal("display-1", host.Displays[0].Name);
    }

    [Fact]
    public void TheEvidenceStringComposesDisplayAndBluetoothEvidence()
    {
        var host = HostContextInterpreter.Build(
            Now,
            Array.Empty<RawDisplay>(),
            new RawBluetooth(null, null, "bluetooth-api-unavailable"),
            wiredUp: null,
            powerState: "unknown",
            indirectDrivers: Array.Empty<string>(),
            displayEvidence: "display-config-sizes-failed-0");

        Assert.Equal("windows-display-config;display-config-sizes-failed-0;bluetooth-api-unavailable", host.EvidenceState);
        Assert.Equal(DockStates.Unknown, host.DockState);
    }

    [Fact]
    public void DescribeTechnologyLabelsAnUnknownValueRatherThanThrowing()
    {
        Assert.Equal("other-99", HostContextInterpreter.DescribeTechnology(99));
    }

    private static HostContext Build(params RawDisplay[] displays)
    {
        return HostContextInterpreter.Build(
            Now,
            displays,
            BluetoothOff,
            wiredUp: true,
            powerState: "source=ac;battery=100%",
            indirectDrivers: Array.Empty<string>(),
            displayEvidence: $"display-config-paths-{displays.Length}");
    }
}

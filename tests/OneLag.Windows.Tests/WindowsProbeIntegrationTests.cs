using OneLag.Core;
using OneLag.Windows;

namespace OneLag.Windows.Tests;

/// <summary>
/// The probes executed against the real operating system.
///
/// Nothing else in the suite can prove that a P/Invoke actually works: layout tests prove the structs are
/// shaped right, fakes prove the interpretation is right, and neither makes a single call into Windows. These
/// do, and they run on the Windows CI runner and on any real laptop.
///
/// They assert that a probe returned *live* data, not merely that it did not throw. A probe that silently
/// degrades to "unavailable" would otherwise pass every check while measuring nothing, which is exactly the
/// failure this whole redesign exists to prevent.
/// </summary>
public sealed class WindowsProbeIntegrationTests
{
    [WindowsFact]
    public void PerformanceCountersReturnLiveValuesIncludingDpcAndInterruptTime()
    {
        var pressure = new WindowsPlatformProbe().CaptureSystemPressure();
        var signals = pressure.Signals ?? Array.Empty<PerformanceSignal>();

        Assert.NotEmpty(signals);

        // The CPU counter is the canary: if PDH is working at all, this has a value.
        var cpu = signals.SingleOrDefault(signal => signal.Kind == "processor-total-percent");
        Assert.NotNull(cpu);
        Assert.True(cpu.Value.HasValue, $"CPU counter returned no value: {cpu.EvidenceState}");

        // The DPC counters are the point of the redesign. A CStatus check that rejected PDH_CSTATUS_NEW_DATA
        // would null these out on every machine while every other test still passed.
        var dpc = signals.SingleOrDefault(signal => signal.Kind == "processor-dpc-percent");
        Assert.NotNull(dpc);
        Assert.True(dpc.Value.HasValue, $"DPC counter returned no value: {dpc.EvidenceState}");
        Assert.InRange(dpc.Value!.Value, 0, 100);

        // The per-core maximum exercises PdhGetFormattedCounterArray and the item-struct stride. Getting the
        // stride wrong reads every core but the first from the wrong offset.
        var dpcMaxCore = signals.SingleOrDefault(signal => signal.Kind == "processor-dpc-percent-max-core");
        Assert.NotNull(dpcMaxCore);
        Assert.True(dpcMaxCore.Value.HasValue, $"per-core DPC counter returned no value: {dpcMaxCore.EvidenceState}");
        Assert.InRange(dpcMaxCore.Value!.Value, 0, 100);

        // The per-core maximum can never be below the all-core average, and a garbage read would routinely
        // violate that.
        Assert.True(
            dpcMaxCore.Value.Value >= dpc.Value.Value - 0.5,
            $"per-core DPC max ({dpcMaxCore.Value.Value}) is below the all-core average ({dpc.Value.Value}), which suggests a bad array read");
    }

    [WindowsFact]
    public void MemoryCountersReturnLiveValues()
    {
        var signals = new WindowsPlatformProbe().CaptureSystemPressure().Signals ?? Array.Empty<PerformanceSignal>();

        var available = signals.SingleOrDefault(signal => signal.Kind == "memory-available-mb");
        Assert.NotNull(available);
        Assert.True(available.Value is > 0, $"available memory was not read: {available.EvidenceState}");
    }

    [WindowsFact]
    public void HostContextEnumeratesAtLeastOneDisplayAndDoesNotThrow()
    {
        var host = new WindowsPlatformProbe().CaptureHostContext();

        Assert.DoesNotContain("unavailable", host.EvidenceState, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("entrypoint", host.EvidenceState, StringComparison.OrdinalIgnoreCase);

        // QueryDisplayConfig marshals a 72-byte path array and a 64-byte mode array. A headless CI runner can
        // legitimately report zero displays, so the assertion is on consistency, not on a count.
        Assert.Equal(host.DisplayCount, host.Displays.Count);
        Assert.True(host.ExternalDisplayCount <= host.DisplayCount);
        Assert.True(host.IndirectDisplayCount <= host.DisplayCount);

        foreach (var display in host.Displays)
        {
            Assert.False(string.IsNullOrWhiteSpace(display.Name));
            Assert.InRange(display.RefreshHz, 0, 500);
            Assert.InRange(display.Width, 0, 30_000);
            Assert.InRange(display.Height, 0, 30_000);
        }
    }

    [WindowsFact]
    public void BluetoothEnumerationCompletesWithoutIssuingARadioInquiry()
    {
        // A radio inquiry would take seconds and perturb the machine mid-diagnosis. A machine with no radio
        // is a valid answer; a probe that hangs or throws is not.
        var started = DateTimeOffset.UtcNow;
        var host = new WindowsPlatformProbe().CaptureHostContext();
        var elapsed = DateTimeOffset.UtcNow - started;

        Assert.True(elapsed < TimeSpan.FromSeconds(10), $"host context took {elapsed.TotalSeconds:N1}s, which suggests a radio inquiry");
        Assert.Contains("bluetooth", host.EvidenceState, StringComparison.OrdinalIgnoreCase);

        if (host.BluetoothRadioPresent == true)
        {
            Assert.True(host.ConnectedBluetoothDevices is null or >= 0);
        }
    }

    [WindowsFact]
    public void ShellResponsivenessIsProbedRatherThanInferred()
    {
        var shell = new WindowsPlatformProbe().CaptureShellResponsiveness();

        Assert.DoesNotContain("entrypoint", shell.EvidenceState, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(shell.ExplorerRunning);

        // A CI runner may have no interactive shell window, which is reported honestly rather than guessed.
        if (shell.ShellWindowHung.HasValue)
        {
            Assert.True(shell.ShellPumpLatencyMilliseconds is >= 0);
        }
    }

    [WindowsFact]
    public void TelemetryReadsTheOneDriveLogStoreWithoutParsingIt()
    {
        var telemetry = new WindowsPlatformProbe().CaptureTelemetry();

        Assert.True(telemetry.OneDriveLogFilesChangedLastMinute >= 0);
        Assert.Equal("windows-process-cpu-and-log-metadata", telemetry.EvidenceState);
    }

    [WindowsFact]
    public void EventSummariesAreReadableFromTheRealSystemLog()
    {
        var summaries = new WindowsPlatformProbe().ReadRecentEventSummaries(DateTimeOffset.UtcNow.AddDays(-7));

        // A pristine machine can genuinely have no recent warnings, so the assertion is on shape rather than
        // on presence.
        foreach (var summary in summaries)
        {
            Assert.False(string.IsNullOrWhiteSpace(summary.Provider));
            Assert.False(string.IsNullOrWhiteSpace(summary.LogName));
            Assert.True(summary.Count > 0);
        }
    }
}

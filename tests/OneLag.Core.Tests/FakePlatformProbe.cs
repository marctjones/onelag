using OneLag.Core;

namespace OneLag.Core.Tests;

/// <summary>
/// A scriptable stand-in for Windows.
///
/// Everything OneLag learns about a machine arrives through IPlatformProbe, so a fake here can put the whole
/// pipeline — scan, risk analysis, hypothesis ranking, report rendering, watch recording — under a machine
/// whose telemetry, pressure, host context, shell state, and driver trace are all dictated by the test. It
/// cannot prove the real probes read Windows correctly; it proves that when they do, the conclusions follow.
/// </summary>
internal sealed class FakePlatformProbe : IPlatformProbe
{
    public IReadOnlyList<RootCandidate> Roots { get; set; } = Array.Empty<RootCandidate>();

    public TelemetrySnapshot Telemetry { get; set; } = Snapshots.QuietTelemetry();

    public SystemPressureSnapshot Pressure { get; set; } = Snapshots.QuietPressure();

    public OneDriveClientHealthSnapshot ClientHealth { get; set; } = Snapshots.HealthyClient();

    public IReadOnlyList<EventLogSummary> EventLogs { get; set; } = Array.Empty<EventLogSummary>();

    public HostContext HostContext { get; set; } = Snapshots.UndockedHost();

    public ShellResponsiveness Shell { get; set; } = Snapshots.ResponsiveShell();

    public DriverLatencyAttribution DriverLatency { get; set; } = DriverLatencyAttribution.Unavailable("not-requested");

    public string? ForegroundProcess { get; set; } = "explorer";

    public int DriverTraceCalls { get; private set; }

    public IReadOnlyList<RootCandidate> DiscoverOneDriveRoots() => Roots;

    public TelemetrySnapshot CaptureTelemetry() => Telemetry;

    public SystemPressureSnapshot CaptureSystemPressure() => Pressure;

    public OneDriveClientHealthSnapshot CaptureOneDriveClientHealth(IReadOnlyList<RootCandidate> roots, TelemetrySnapshot telemetry) => ClientHealth;

    public IReadOnlyList<EventLogSummary> ReadRecentEventSummaries(DateTimeOffset since) => EventLogs;

    public string? GetForegroundProcessName() => ForegroundProcess;

    public HostContext CaptureHostContext() => HostContext;

    public ShellResponsiveness CaptureShellResponsiveness() => Shell;

    public DriverLatencyAttribution CaptureDriverLatency(TimeSpan duration, CancellationToken cancellationToken)
    {
        DriverTraceCalls++;
        return DriverLatency;
    }
}

internal static class Snapshots
{
    public static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-13T10:00:00Z");

    public static TelemetrySnapshot QuietTelemetry() =>
        new(Now, Array.Empty<ProcessSample>(), 0, null, "windows-process-cpu-and-log-metadata");

    /// <summary>OneDrive running hot: the source guide's live signals, all firing.</summary>
    public static TelemetrySnapshot ThrashingOneDrive() =>
        new(
            Now,
            new[] { new ProcessSample("OneDrive", 4242, 900_000_000, TimeSpan.FromMinutes(12), @"C:\Program Files\Microsoft OneDrive\OneDrive.exe", 62.5) },
            14,
            "24.201.1006.0005",
            "windows-process-cpu-and-log-metadata");

    public static SystemPressureSnapshot QuietPressure() =>
        Pressure(
            ("processor-total-percent", 8),
            ("processor-queue-length", 0),
            ("memory-available-mb", 18_000),
            ("memory-commit-percent", 34),
            ("physical-disk-queue-length", 0.1),
            ("physical-disk-active-percent", 3),
            ("processor-dpc-percent", 0.4),
            ("processor-dpc-percent-max-core", 1.1),
            ("processor-interrupt-percent", 0.9),
            ("processor-interrupt-percent-max-core", 2.0));

    /// <summary>A driver pinning one core: invisible in the all-core average, fatal to the desktop.</summary>
    public static SystemPressureSnapshot PinnedCoreInterruptPressure() =>
        Pressure(
            ("processor-total-percent", 14),
            ("processor-queue-length", 1),
            ("memory-available-mb", 16_000),
            ("memory-commit-percent", 38),
            ("physical-disk-queue-length", 0.2),
            ("physical-disk-active-percent", 5),
            ("processor-dpc-percent", 2.1),
            ("processor-dpc-percent-max-core", 34.0),
            ("processor-interrupt-percent", 3.0),
            ("processor-interrupt-percent-max-core", 19.0));

    public static SystemPressureSnapshot SaturatedDisk() =>
        Pressure(
            ("processor-total-percent", 30),
            ("memory-available-mb", 9_000),
            ("physical-disk-queue-length", 6.2),
            ("physical-disk-active-percent", 98),
            ("processor-dpc-percent", 0.5),
            ("processor-dpc-percent-max-core", 1.4));

    public static OneDriveClientHealthSnapshot HealthyClient() =>
        new(Now, false, "windows-metadata-only", Array.Empty<ClientHealthSignal>(), Array.Empty<OneDriveResetCommand>());

    public static ShellResponsiveness ResponsiveShell() =>
        new(Now, true, false, 0, 1.2, "windows-shell-message-pump-probe");

    public static ShellResponsiveness HungShell() =>
        new(Now, true, true, 4, 2_000, "windows-shell-message-pump-timed-out");

    public static HostContext UndockedHost() =>
        new(
            Now,
            1,
            0,
            0,
            new[] { new DisplayInfo("Built-in Display", "internal", true, false, 2880, 1800, 120) },
            true,
            false,
            0,
            "source=battery;battery=78%",
            false,
            Array.Empty<string>(),
            DockStates.UndockedLikely,
            "windows-display-config;display-config-paths-1;bluetooth-radio-absent-or-disabled");

    /// <summary>The configuration under investigation: dock, a DisplayLink monitor, and a Bluetooth mouse.</summary>
    public static HostContext DockedHostWithDisplayLink() =>
        new(
            Now,
            2,
            0,
            1,
            new[]
            {
                new DisplayInfo("Built-in Display", "internal", true, false, 2880, 1800, 120),
                new DisplayInfo("Dell U2720Q", "indirect-wired-usb", false, true, 3840, 2160, 60)
            },
            true,
            true,
            2,
            "source=ac;battery=100%",
            true,
            new[] { "DisplayLinkManager" },
            DockStates.DockedLikely,
            "windows-display-config;display-config-paths-2;cfgmgr-pnp-classic-and-le");

    public static DriverLatencyAttribution DisplayLinkStorm() =>
        new(
            Now,
            TimeSpan.FromSeconds(30),
            new[]
            {
                new DriverLatencySample("dlkmdldr.sys", "dpc", 780, 4.6, 3_100),
                new DriverLatencySample("ntoskrnl.exe", "dpc", 610, 1.9, 48_000),
                new DriverLatencySample("storport.sys", "dpc", 12, 0.3, 4_000)
            },
            "windows-kernel-etw-dpc-isr");

    private static SystemPressureSnapshot Pressure(params (string Kind, double Value)[] signals)
    {
        return new SystemPressureSnapshot(
            Now,
            "sampled",
            "sampled",
            "sampled",
            "source=ac",
            Array.Empty<string>(),
            "windows-pdh-process-and-win32-memory-snapshot",
            signals.Select(signal => new PerformanceSignal(signal.Kind, signal.Value, "unit", "pdh")).ToArray());
    }
}

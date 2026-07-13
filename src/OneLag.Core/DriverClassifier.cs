namespace OneLag.Core;

/// <summary>
/// Maps a driver image name to the subsystem it belongs to, so DPC time attributed to a specific `.sys` file
/// can strengthen the hypothesis that actually owns it rather than sitting in the report as trivia.
/// </summary>
public static class DriverClassifier
{
    private static readonly (string Fragment, HypothesisKind Kind, string Subsystem)[] Signatures =
    {
        // DisplayLink-class USB graphics: renders on the CPU and pushes frames over USB.
        ("dlkmd", HypothesisKind.DisplayOrDockPipeline, "DisplayLink USB graphics"),
        ("displaylink", HypothesisKind.DisplayOrDockPipeline, "DisplayLink USB graphics"),
        ("usbdisplay", HypothesisKind.DisplayOrDockPipeline, "USB display"),
        ("iddcx", HypothesisKind.DisplayOrDockPipeline, "indirect display driver"),

        // GPU kernel-mode drivers.
        ("nvlddmkm", HypothesisKind.DisplayOrDockPipeline, "NVIDIA display driver"),
        ("igdkmd", HypothesisKind.DisplayOrDockPipeline, "Intel display driver"),
        ("amdkmdag", HypothesisKind.DisplayOrDockPipeline, "AMD display driver"),
        ("atikmdag", HypothesisKind.DisplayOrDockPipeline, "AMD display driver"),
        ("dxgkrnl", HypothesisKind.DisplayOrDockPipeline, "DirectX graphics kernel"),

        // Dock and USB4/Thunderbolt transport.
        ("thunderbolt", HypothesisKind.DisplayOrDockPipeline, "Thunderbolt controller"),
        ("usb4", HypothesisKind.DisplayOrDockPipeline, "USB4 controller"),
        ("ucx01000", HypothesisKind.DisplayOrDockPipeline, "USB host controller extension"),
        ("usbxhci", HypothesisKind.DisplayOrDockPipeline, "USB xHCI host controller"),
        ("usbhub3", HypothesisKind.DisplayOrDockPipeline, "USB 3 hub"),

        // Bluetooth stack and radios.
        ("bthport", HypothesisKind.BluetoothOrInputRadio, "Bluetooth port driver"),
        ("bthusb", HypothesisKind.BluetoothOrInputRadio, "Bluetooth USB transport"),
        ("bthmini", HypothesisKind.BluetoothOrInputRadio, "Bluetooth miniport"),
        ("ibtusb", HypothesisKind.BluetoothOrInputRadio, "Intel Bluetooth"),
        ("btha2dp", HypothesisKind.BluetoothOrInputRadio, "Bluetooth audio"),
        ("hidbth", HypothesisKind.BluetoothOrInputRadio, "Bluetooth HID"),

        // Wi-Fi: shares the 2.4 GHz band with Bluetooth, and coexistence is a classic DPC source.
        ("netwtw", HypothesisKind.BluetoothOrInputRadio, "Intel Wi-Fi (2.4 GHz coexistence with Bluetooth)"),
        ("netwlv", HypothesisKind.BluetoothOrInputRadio, "Intel Wi-Fi (2.4 GHz coexistence with Bluetooth)"),
        ("athw", HypothesisKind.BluetoothOrInputRadio, "Qualcomm Wi-Fi (2.4 GHz coexistence with Bluetooth)"),
        ("rtwlan", HypothesisKind.BluetoothOrInputRadio, "Realtek Wi-Fi (2.4 GHz coexistence with Bluetooth)"),
        ("mrvlpcie", HypothesisKind.BluetoothOrInputRadio, "Marvell Wi-Fi (2.4 GHz coexistence with Bluetooth)"),

        // Storage.
        ("storport", HypothesisKind.StorageSaturation, "storage port driver"),
        ("stornvme", HypothesisKind.StorageSaturation, "NVMe storage"),
        ("iastor", HypothesisKind.StorageSaturation, "Intel RST storage"),
        ("storahci", HypothesisKind.StorageSaturation, "AHCI storage"),

        // Filter drivers that sit in the file I/O path, which is where sync engines and scanners live.
        ("cldflt", HypothesisKind.OneDriveSync, "cloud files filter (OneDrive Files On-Demand)"),
        ("wcifs", HypothesisKind.OneDriveSync, "Windows container isolation filter"),
        ("wdfilter", HypothesisKind.SecurityOrSearchScanner, "Microsoft Defender filter")
    };

    public static (HypothesisKind? Kind, string? Subsystem) Classify(string driver)
    {
        if (string.IsNullOrWhiteSpace(driver))
        {
            return (null, null);
        }

        var name = Path.GetFileNameWithoutExtension(driver);

        foreach (var (fragment, kind, subsystem) in Signatures)
        {
            if (name.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return (kind, subsystem);
            }
        }

        return (null, null);
    }

    /// <summary>
    /// The core OS images. Timer DPCs and the scheduler always land here, on every machine, healthy or not.
    /// Naming them would produce advice like "update or roll back ntoskrnl.exe", which is not advice.
    /// </summary>
    private static readonly string[] NotActionable = { "ntoskrnl", "ntkrnlmp", "hal", "unresolved" };

    /// <summary>
    /// The drivers whose DPC or ISR time is high enough to matter, aggregated per driver and ordered worst
    /// first.
    ///
    /// The floor is relative to the trace window, not absolute. Every machine accumulates a few milliseconds
    /// of DPC time across a 30-second trace; an absolute threshold of a few ms would mark storport.sys and
    /// dxgkrnl.sys as significant on a completely healthy laptop and manufacture a culprit on every run. A
    /// driver has to hold a meaningful share of the window *and* land at least one routine long enough to
    /// drop a frame before it is worth naming.
    /// </summary>
    public static IReadOnlyList<DriverLatencySample> Significant(
        DriverLatencyAttribution? attribution,
        double minimumWindowShare = 0.01,
        double minimumWorstMilliseconds = 1.0)
    {
        if (attribution is null || attribution.Drivers.Count == 0)
        {
            return Array.Empty<DriverLatencySample>();
        }

        var windowMilliseconds = attribution.Duration.TotalMilliseconds;
        var minimumTotal = windowMilliseconds > 0
            ? windowMilliseconds * minimumWindowShare
            : double.MaxValue;

        return attribution.Drivers
            .Where(driver => !IsNotActionable(driver.Driver))
            // A driver with both DPC and ISR time is one root cause, not two. Aggregating here stops it
            // being credited twice downstream.
            .GroupBy(driver => driver.Driver, StringComparer.OrdinalIgnoreCase)
            .Select(group => new DriverLatencySample(
                group.Key,
                string.Join("+", group.Select(driver => driver.Kind).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)),
                group.Sum(driver => driver.TotalMilliseconds),
                group.Max(driver => driver.MaxMilliseconds),
                group.Sum(driver => driver.Count)))
            .Where(driver => driver.TotalMilliseconds >= minimumTotal && driver.MaxMilliseconds >= minimumWorstMilliseconds)
            .OrderByDescending(driver => driver.TotalMilliseconds)
            .ToArray();
    }

    private static bool IsNotActionable(string driver)
    {
        var name = Path.GetFileNameWithoutExtension(driver);
        return NotActionable.Any(excluded => name.Equals(excluded, StringComparison.OrdinalIgnoreCase));
    }
}

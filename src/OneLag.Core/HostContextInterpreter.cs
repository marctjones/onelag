namespace OneLag.Core;

/// <summary>
/// A single display as read from the OS, before OneLag has classified it. The raw output-technology value is
/// what Windows reports in DISPLAYCONFIG_PATH_TARGET_INFO; everything downstream is derived from it.
/// </summary>
public sealed record RawDisplay(uint OutputTechnology, string? Name, int Width, int Height, double RefreshHz);

public sealed record RawBluetooth(bool? RadioPresent, int? ConnectedDevices, string EvidenceState);

/// <summary>
/// Turns raw display topology and radio state into a <see cref="HostContext"/>.
///
/// This is the half of host-context capture that the CI runner cannot exercise: a headless Windows Server VM
/// has no dock, no external monitors, and no DisplayLink driver, so the code that recognises an indirect/USB
/// display and concludes "docked" never runs there. Splitting it out from the P/Invoke read means the exact
/// logic your laptop hits — for the exact output-technology values a DisplayLink dock or a Thunderbolt
/// monitor produces — is testable on any machine, with the struct marshalling separately pinned by the
/// interop-layout tests and validated live on the runner.
/// </summary>
public static class HostContextInterpreter
{
    // DISPLAYCONFIG_OUTPUT_TECHNOLOGY values (documented Win32 constants).
    public const uint Vga = 0;
    public const uint Dvi = 4;
    public const uint Hdmi = 5;
    public const uint Lvds = 6;
    public const uint DisplayPortExternal = 10;
    public const uint DisplayPortEmbedded = 11;
    public const uint UdiExternal = 12;
    public const uint UdiEmbedded = 13;
    public const uint Miracast = 15;

    /// <summary>DisplayLink-class USB graphics: renders on the CPU, pushes frames over USB.</summary>
    public const uint IndirectWired = 16;
    public const uint IndirectVirtual = 17;
    public const uint Internal = 0x80000000;

    /// <summary>
    /// Not every built-in panel reports INTERNAL. Laptop eDP panels commonly report DISPLAYPORT_EMBEDDED,
    /// and older ones LVDS. Treating those as external would count the laptop's own screen as an attached
    /// monitor and report an undocked machine as docked.
    /// </summary>
    public static bool IsInternalPanel(uint technology)
    {
        return technology is Internal or DisplayPortEmbedded or UdiEmbedded or Lvds;
    }

    public static bool IsIndirect(uint technology)
    {
        return technology is IndirectWired or IndirectVirtual;
    }

    public static string DescribeTechnology(uint technology)
    {
        return technology switch
        {
            Vga => "vga",
            Dvi => "dvi",
            Hdmi => "hdmi",
            Lvds => "lvds",
            DisplayPortExternal => "displayport-external",
            DisplayPortEmbedded => "displayport-embedded",
            UdiExternal => "udi-external",
            UdiEmbedded => "udi-embedded",
            Miracast => "miracast",
            IndirectWired => "indirect-wired-usb",
            IndirectVirtual => "indirect-virtual",
            Internal => "internal",
            _ => $"other-{technology}"
        };
    }

    public static DisplayInfo ToDisplayInfo(RawDisplay raw, int index)
    {
        return new DisplayInfo(
            string.IsNullOrWhiteSpace(raw.Name) ? $"display-{index + 1}" : raw.Name,
            DescribeTechnology(raw.OutputTechnology),
            IsInternalPanel(raw.OutputTechnology),
            IsIndirect(raw.OutputTechnology),
            raw.Width,
            raw.Height,
            raw.RefreshHz);
    }

    /// <summary>
    /// Dock state is inferred, not read: Windows has no single "docked" bit. An external or indirect display
    /// is the strongest signal; a wired NIC on AC power is a weaker one; a single internal panel on battery
    /// is undocked. Anything else is left unknown rather than guessed.
    /// </summary>
    public static string DeriveDockState(int displayCount, int external, int indirect, bool? wiredUp, string powerState)
    {
        if (external > 0 || indirect > 0)
        {
            return DockStates.DockedLikely;
        }

        if (wiredUp == true && powerState.Contains("source=ac", StringComparison.OrdinalIgnoreCase))
        {
            return DockStates.DockedLikely;
        }

        if (displayCount <= 1 && wiredUp == false)
        {
            return DockStates.UndockedLikely;
        }

        return DockStates.Unknown;
    }

    public static HostContext Build(
        DateTimeOffset timestamp,
        IReadOnlyList<RawDisplay> rawDisplays,
        RawBluetooth bluetooth,
        bool? wiredUp,
        string powerState,
        IReadOnlyList<string> indirectDrivers,
        string displayEvidence)
    {
        var displays = rawDisplays
            .Select((raw, index) => ToDisplayInfo(raw, index))
            .ToArray();

        var external = displays.Count(display => !display.IsInternal && !display.IsIndirect);
        var indirect = displays.Count(display => display.IsIndirect);
        var dockState = DeriveDockState(displays.Length, external, indirect, wiredUp, powerState);

        return new HostContext(
            timestamp,
            displays.Length,
            external,
            indirect,
            displays,
            bluetooth.RadioPresent,
            bluetooth.RadioPresent,
            bluetooth.ConnectedDevices,
            powerState,
            wiredUp,
            indirectDrivers,
            dockState,
            $"windows-display-config;{displayEvidence};{bluetooth.EvidenceState}");
    }
}

using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using OneLag.Core;

namespace OneLag.Windows;

/// <summary>
/// Captures what is physically attached to the machine.
///
/// Lag that appears only when docked, only with external displays, or only with Bluetooth peripherals is a
/// driver problem, and no amount of OneDrive folder inventory will ever surface it. This probe records the
/// configuration alongside every sample so lag can be correlated with the hardware it actually tracks.
///
/// The signal worth the most here is <c>INDIRECT_WIRED</c>: displays driven by DisplayLink-class USB
/// graphics, which render frames on the CPU and push them over USB, and are a well-documented source of
/// long DPC routines.
/// </summary>
internal static class WindowsHostContextProbe
{
    public static HostContext Capture(string powerState)
    {
        if (!OperatingSystem.IsWindows())
        {
            return HostContext.Unavailable("unavailable-on-this-platform");
        }

        try
        {
            var displays = QueryDisplays(out var displayEvidence);
            var external = displays.Count(display => !display.IsInternal && !display.IsIndirect);
            var indirect = displays.Count(display => display.IsIndirect);
            var wiredUp = TryGetWiredNetworkUp();
            var (radioPresent, connectedDevices, bluetoothEvidence) = QueryBluetooth();
            var indirectDrivers = FindIndirectDisplaySoftware();

            var dockState = DeriveDockState(displays.Count, external, indirect, wiredUp, powerState);

            return new HostContext(
                DateTimeOffset.UtcNow,
                displays.Count,
                external,
                indirect,
                displays,
                radioPresent,
                radioPresent,
                connectedDevices,
                powerState,
                wiredUp,
                indirectDrivers,
                dockState,
                $"windows-display-config;{displayEvidence};{bluetoothEvidence}");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return HostContext.Unavailable("windows-host-context-entrypoint-unavailable");
        }
    }

    private static string DeriveDockState(int displayCount, int external, int indirect, bool? wiredUp, string powerState)
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

    private static bool? TryGetWiredNetworkUp()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Any(nic => nic.OperationalStatus == OperationalStatus.Up
                    && nic.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet
                    && !nic.Description.Contains("virtual", StringComparison.OrdinalIgnoreCase)
                    && !nic.Description.Contains("loopback", StringComparison.OrdinalIgnoreCase));
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> FindIndirectDisplaySoftware()
    {
        try
        {
            return Process.GetProcesses()
                .Select(process =>
                {
                    try
                    {
                        return process.ProcessName;
                    }
                    catch (InvalidOperationException)
                    {
                        return string.Empty;
                    }
                    finally
                    {
                        process.Dispose();
                    }
                })
                .Where(name => name.Contains("displaylink", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("dl_manager", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (InvalidOperationException)
        {
            return Array.Empty<string>();
        }
    }

    private static IReadOnlyList<DisplayInfo> QueryDisplays(out string evidenceState)
    {
        var sizeStatus = GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out var pathCount, out var modeCount);
        if (sizeStatus != ErrorSuccess || pathCount == 0)
        {
            evidenceState = $"display-config-sizes-failed-{sizeStatus}";
            return Array.Empty<DisplayInfo>();
        }

        var paths = new DisplayConfigPathInfo[pathCount];
        var modes = new DisplayConfigModeInfo[modeCount];
        var queryStatus = QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
        if (queryStatus != ErrorSuccess)
        {
            evidenceState = $"query-display-config-failed-{queryStatus}";
            return Array.Empty<DisplayInfo>();
        }

        var displays = new List<DisplayInfo>();
        for (var index = 0; index < pathCount; index++)
        {
            var path = paths[index];
            var technology = path.TargetInfo.OutputTechnology;

            var (width, height) = ResolveSourceSize(path, modes, (int)modeCount);
            var refreshHz = path.TargetInfo.RefreshRate.Denominator > 0
                ? (double)path.TargetInfo.RefreshRate.Numerator / path.TargetInfo.RefreshRate.Denominator
                : 0;

            displays.Add(new DisplayInfo(
                ResolveName(path) ?? $"display-{index + 1}",
                DescribeTechnology(technology),
                IsInternalPanel(technology),
                technology is OutputTechnologyIndirectWired or OutputTechnologyIndirectVirtual,
                width,
                height,
                refreshHz));
        }

        evidenceState = $"display-config-paths-{displays.Count}";
        return displays;
    }

    private static (int Width, int Height) ResolveSourceSize(DisplayConfigPathInfo path, DisplayConfigModeInfo[] modes, int modeCount)
    {
        var index = path.SourceInfo.ModeInfoIdx;
        if (index == PathModeIdxInvalid || index >= modeCount)
        {
            return (0, 0);
        }

        var mode = modes[index];
        if (mode.InfoType != ModeInfoTypeSource)
        {
            return (0, 0);
        }

        return ((int)mode.Mode.SourceMode.Width, (int)mode.Mode.SourceMode.Height);
    }

    private static string? ResolveName(DisplayConfigPathInfo path)
    {
        var request = new DisplayConfigTargetDeviceName
        {
            Header = new DisplayConfigDeviceInfoHeader
            {
                Type = DeviceInfoGetTargetName,
                Size = (uint)Marshal.SizeOf<DisplayConfigTargetDeviceName>(),
                AdapterId = path.TargetInfo.AdapterId,
                Id = path.TargetInfo.Id
            }
        };

        return DisplayConfigGetDeviceInfo(ref request) == ErrorSuccess && !string.IsNullOrWhiteSpace(request.MonitorFriendlyDeviceName)
            ? request.MonitorFriendlyDeviceName
            : null;
    }

    /// <summary>
    /// Not every built-in panel reports INTERNAL. Laptop eDP panels commonly report DISPLAYPORT_EMBEDDED,
    /// and older ones report LVDS. Treating those as external would count the laptop's own screen as an
    /// attached monitor and report an undocked machine as docked.
    /// </summary>
    private static bool IsInternalPanel(uint technology)
    {
        return technology is OutputTechnologyInternal
            or OutputTechnologyDisplayPortEmbedded
            or OutputTechnologyUdiEmbedded
            or OutputTechnologyLvds;
    }

    private static string DescribeTechnology(uint technology)
    {
        return technology switch
        {
            0 => "vga",
            4 => "dvi",
            5 => "hdmi",
            6 => "lvds",
            10 => "displayport-external",
            11 => "displayport-embedded",
            12 => "udi-external",
            13 => "udi-embedded",
            15 => "miracast",
            OutputTechnologyIndirectWired => "indirect-wired-usb",
            OutputTechnologyIndirectVirtual => "indirect-virtual",
            OutputTechnologyInternal => "internal",
            _ => $"other-{technology}"
        };
    }

    private static (bool? RadioPresent, int? ConnectedDevices, string EvidenceState) QueryBluetooth()
    {
        IntPtr radioHandle;
        IntPtr findHandle;
        try
        {
            var findParams = new BluetoothFindRadioParams { Size = (uint)Marshal.SizeOf<BluetoothFindRadioParams>() };
            findHandle = BluetoothFindFirstRadio(ref findParams, out radioHandle);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return (null, null, "bluetooth-api-unavailable");
        }

        if (findHandle == IntPtr.Zero)
        {
            // Windows does not enumerate a radio that is switched off, so "absent" and "turned off" are the
            // same observation here. Either way the radio cannot be causing lag right now.
            return (false, 0, "bluetooth-radio-absent-or-disabled");
        }

        try
        {
            // Prefer the PnP device tree, which sees Bluetooth LE peripherals. BluetoothFindFirstDevice is
            // documented not to enumerate LE devices, and most modern mice and keyboards are LE, so a count
            // from it alone would miss exactly the peripherals worth correlating with lag.
            var pnp = WindowsBluetoothDeviceProbe.Count();
            if (pnp.Connected.HasValue)
            {
                return (true, pnp.Connected, $"bluetooth-radio-enumerated;{pnp.EvidenceState};present={pnp.Present}");
            }

            var classicConnected = CountConnectedDevices(radioHandle);
            return classicConnected > 0
                ? (true, classicConnected, $"bluetooth-radio-enumerated;classic-only-fallback;{pnp.EvidenceState}")
                : (true, null, $"bluetooth-radio-enumerated;no-classic-devices;ble-not-enumerated;{pnp.EvidenceState}");
        }
        finally
        {
            if (radioHandle != IntPtr.Zero)
            {
                _ = CloseHandle(radioHandle);
            }

            _ = BluetoothFindRadioClose(findHandle);
        }
    }

    private static int CountConnectedDevices(IntPtr radioHandle)
    {
        var searchParams = new BluetoothDeviceSearchParams
        {
            Size = (uint)Marshal.SizeOf<BluetoothDeviceSearchParams>(),
            ReturnAuthenticated = false,
            ReturnRemembered = false,
            ReturnUnknown = false,
            ReturnConnected = true,

            // An inquiry would put a radio scan in the middle of a diagnostic sample. Only the already
            // connected set is read, which is cached and returns immediately.
            IssueInquiry = false,
            TimeoutMultiplier = 0,
            Radio = radioHandle
        };

        var deviceInfo = new BluetoothDeviceInfo { Size = (uint)Marshal.SizeOf<BluetoothDeviceInfo>() };
        var find = BluetoothFindFirstDevice(ref searchParams, ref deviceInfo);
        if (find == IntPtr.Zero)
        {
            return 0;
        }

        try
        {
            var count = 0;
            do
            {
                if (deviceInfo.Connected)
                {
                    count++;
                }

                deviceInfo = new BluetoothDeviceInfo { Size = (uint)Marshal.SizeOf<BluetoothDeviceInfo>() };
            }
            while (BluetoothFindNextDevice(find, ref deviceInfo) && count < 64);

            return count;
        }
        finally
        {
            _ = BluetoothFindDeviceClose(find);
        }
    }

    private const uint ErrorSuccess = 0;
    private const uint QdcOnlyActivePaths = 0x00000002;
    private const uint PathModeIdxInvalid = 0xffffffff;
    private const uint ModeInfoTypeSource = 1;
    private const uint DeviceInfoGetTargetName = 2;
    private const uint OutputTechnologyLvds = 6;
    private const uint OutputTechnologyDisplayPortEmbedded = 11;
    private const uint OutputTechnologyUdiEmbedded = 13;
    private const uint OutputTechnologyIndirectWired = 16;
    private const uint OutputTechnologyIndirectVirtual = 17;
    private const uint OutputTechnologyInternal = 0x80000000;

    [DllImport("user32.dll")]
    private static extern uint GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern uint QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements,
        [Out] DisplayConfigPathInfo[] pathArray,
        ref uint numModeInfoArrayElements,
        [Out] DisplayConfigModeInfo[] modeInfoArray,
        IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    private static extern uint DisplayConfigGetDeviceInfo(ref DisplayConfigTargetDeviceName requestPacket);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DisplayConfigRational
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DisplayConfigPathSourceInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DisplayConfigPathTargetInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint OutputTechnology;
        public uint Rotation;
        public uint Scaling;
        public DisplayConfigRational RefreshRate;
        public uint ScanLineOrdering;
        public int TargetAvailable;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DisplayConfigPathInfo
    {
        public DisplayConfigPathSourceInfo SourceInfo;
        public DisplayConfigPathTargetInfo TargetInfo;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DisplayConfigSourceMode
    {
        public uint Width;
        public uint Height;
        public uint PixelFormat;
        public int PositionX;
        public int PositionY;
    }

    /// <summary>
    /// The native union is sized by its largest member, DISPLAYCONFIG_TARGET_MODE (48 bytes). Only the
    /// source mode is read, but the size must match or the surrounding array stride is wrong.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 48)]
    internal struct DisplayConfigModeUnion
    {
        [FieldOffset(0)]
        public DisplayConfigSourceMode SourceMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DisplayConfigModeInfo
    {
        public uint InfoType;
        public uint Id;
        public Luid AdapterId;
        public DisplayConfigModeUnion Mode;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DisplayConfigDeviceInfoHeader
    {
        public uint Type;
        public uint Size;
        public Luid AdapterId;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DisplayConfigTargetDeviceName
    {
        public DisplayConfigDeviceInfoHeader Header;
        public uint Flags;
        public uint OutputTechnology;
        public ushort EdidManufactureId;
        public ushort EdidProductCodeId;
        public uint ConnectorInstance;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string MonitorFriendlyDeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string MonitorDevicePath;
    }

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern IntPtr BluetoothFindFirstRadio(ref BluetoothFindRadioParams findParams, out IntPtr radio);

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern bool BluetoothFindRadioClose(IntPtr find);

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern IntPtr BluetoothFindFirstDevice(ref BluetoothDeviceSearchParams searchParams, ref BluetoothDeviceInfo deviceInfo);

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern bool BluetoothFindNextDevice(IntPtr find, ref BluetoothDeviceInfo deviceInfo);

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern bool BluetoothFindDeviceClose(IntPtr find);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    internal struct BluetoothFindRadioParams
    {
        public uint Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BluetoothDeviceSearchParams
    {
        public uint Size;

        [MarshalAs(UnmanagedType.Bool)]
        public bool ReturnAuthenticated;

        [MarshalAs(UnmanagedType.Bool)]
        public bool ReturnRemembered;

        [MarshalAs(UnmanagedType.Bool)]
        public bool ReturnUnknown;

        [MarshalAs(UnmanagedType.Bool)]
        public bool ReturnConnected;

        [MarshalAs(UnmanagedType.Bool)]
        public bool IssueInquiry;

        public byte TimeoutMultiplier;

        public IntPtr Radio;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct BluetoothDeviceInfo
    {
        public uint Size;
        public ulong Address;
        public uint ClassOfDevice;

        [MarshalAs(UnmanagedType.Bool)]
        public bool Connected;

        [MarshalAs(UnmanagedType.Bool)]
        public bool Remembered;

        [MarshalAs(UnmanagedType.Bool)]
        public bool Authenticated;

        public SystemTime LastSeen;
        public SystemTime LastUsed;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)]
        public string Name;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SystemTime
    {
        public ushort Year;
        public ushort Month;
        public ushort DayOfWeek;
        public ushort Day;
        public ushort Hour;
        public ushort Minute;
        public ushort Second;
        public ushort Milliseconds;
    }
}

using System.Runtime.InteropServices;

namespace OneLag.Windows;

internal sealed record BluetoothDeviceCount(int? Connected, int? Present, string EvidenceState);

/// <summary>
/// Counts connected Bluetooth devices, including Bluetooth LE.
///
/// The Bluetooth API's own <c>BluetoothFindFirstDevice</c> is documented not to enumerate LE devices, and
/// most modern mice and keyboards are LE — precisely the peripherals whose radio traffic is worth correlating
/// with lag. This walks the PnP device tree instead, where classic (<c>BTHENUM</c>) and LE (<c>BTHLE</c>,
/// <c>BTHLEDevice</c>) peripherals both appear, and reads each node's connected state.
/// </summary>
internal static class WindowsBluetoothDeviceProbe
{
    /// <summary>
    /// Classic peripherals appear under BTHENUM, LE peripherals under BTHLE. BTHLEDevice is deliberately not
    /// listed: it yields one devnode per GATT service, so an LE mouse would be counted six or eight times.
    /// </summary>
    private static readonly string[] Enumerators = { "BTHENUM", "BTHLE" };

    public static BluetoothDeviceCount Count()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new BluetoothDeviceCount(null, null, "unavailable-on-this-platform");
        }

        try
        {
            var connected = 0;
            var present = 0;
            var readAnyEnumerator = false;

            foreach (var enumerator in Enumerators)
            {
                if (!TryGetDeviceIds(enumerator, out var deviceIds))
                {
                    continue;
                }

                readAnyEnumerator = true;

                foreach (var deviceId in deviceIds.Where(IsDeviceNode))
                {
                    present++;
                    if (IsConnected(deviceId) == true)
                    {
                        connected++;
                    }
                }
            }

            return readAnyEnumerator
                ? new BluetoothDeviceCount(connected, present, "cfgmgr-pnp-classic-and-le")
                : new BluetoothDeviceCount(null, null, "cfgmgr-no-bluetooth-enumerators");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return new BluetoothDeviceCount(null, null, "cfgmgr-entrypoint-unavailable");
        }
    }

    /// <summary>
    /// Each enumerator also exposes a devnode per RFCOMM service or GATT service. Only the DEV_ nodes are
    /// the peripherals themselves; counting the service nodes would multiply every device several times.
    /// </summary>
    private static bool IsDeviceNode(string deviceId)
    {
        var segments = deviceId.Split('\\');
        return segments.Length >= 2 && segments[1].StartsWith("DEV_", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetDeviceIds(string enumerator, out IReadOnlyList<string> deviceIds)
    {
        deviceIds = Array.Empty<string>();

        var sizeStatus = CM_Get_Device_ID_List_Size(out var length, enumerator, FilterEnumerator | FilterPresent);
        if (sizeStatus != ConfigRetSuccess)
        {
            return false;
        }

        // Success with a length of one is the lone terminating null: the enumerator exists and has no present
        // devices. That is a real answer of zero, not a failure to read, and must not be reported as unknown.
        if (length <= 1)
        {
            return true;
        }

        var buffer = new char[length];
        var listStatus = CM_Get_Device_ID_List(enumerator, buffer, length, FilterEnumerator | FilterPresent);
        if (listStatus != ConfigRetSuccess)
        {
            return false;
        }

        // The result is a double-null-terminated sequence of null-separated device instance IDs.
        deviceIds = new string(buffer)
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .ToArray();

        return true;
    }

    private static bool? IsConnected(string deviceId)
    {
        if (CM_Locate_DevNode(out var devInst, deviceId, LocateDevNodeNormal) != ConfigRetSuccess)
        {
            return null;
        }

        var key = DevPropKeyDeviceIsConnected;
        var size = (uint)sizeof(byte);
        var value = new byte[1];

        var status = CM_Get_DevNode_Property(devInst, ref key, out var propertyType, value, ref size, 0);
        if (status != ConfigRetSuccess || propertyType != DevPropTypeBoolean)
        {
            return null;
        }

        // DEVPROP_TRUE is 0xFF, not 1.
        return value[0] != 0;
    }

    private const uint ConfigRetSuccess = 0;
    private const uint FilterEnumerator = 0x00000001;
    private const uint FilterPresent = 0x00000100;
    private const uint LocateDevNodeNormal = 0;
    private const uint DevPropTypeBoolean = 0x00000011;

    private static DevPropKey DevPropKeyDeviceIsConnected => new()
    {
        Fmtid = new Guid(0x83da6326, 0x97a6, 0x4088, 0x94, 0x53, 0xa1, 0x92, 0x3f, 0x57, 0x3b, 0x29),
        Pid = 15
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey
    {
        public Guid Fmtid;
        public uint Pid;
    }

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_List_SizeW", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_ID_List_Size(out uint length, string? filter, uint flags);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_ListW", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_ID_List(string? filter, [Out] char[] buffer, uint bufferLength, uint flags);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", CharSet = CharSet.Unicode)]
    private static extern uint CM_Locate_DevNode(out uint devInst, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_DevNode_Property(
        uint devInst,
        ref DevPropKey propertyKey,
        out uint propertyType,
        [Out] byte[] buffer,
        ref uint bufferSize,
        uint flags);
}

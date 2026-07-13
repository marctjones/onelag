using System.Runtime.InteropServices;
using OneLag.Windows;

namespace OneLag.Windows.Tests;

/// <summary>
/// Pins the memory layout of every interop struct against its native contract.
///
/// This is the highest-risk code in the project and the only code that cannot be executed off Windows: a
/// single wrong field order or a missing padding byte reads garbage out of the OS and produces a confident,
/// wrong diagnosis rather than an error. Layout, however, is managed metadata — Marshal.SizeOf and
/// Marshal.OffsetOf resolve it identically on macOS — so it can be pinned from a dev machine even though the
/// call itself cannot be made. A future edit that reorders a field now fails the build here instead of
/// corrupting a reading on a real laptop.
///
/// Expected sizes and offsets below are taken from the Win32 headers for x64.
/// </summary>
public sealed class InteropLayoutTests
{
    [Fact]
    public void DisplayConfigStructsMatchTheNativeLayout()
    {
        // LUID { DWORD LowPart; LONG HighPart; }
        Assert.Equal(8, Marshal.SizeOf<WindowsHostContextProbe.Luid>());

        // DISPLAYCONFIG_PATH_SOURCE_INFO { LUID adapterId; UINT32 id; UINT32 modeInfoIdx; UINT32 statusFlags; }
        Assert.Equal(20, Marshal.SizeOf<WindowsHostContextProbe.DisplayConfigPathSourceInfo>());

        // DISPLAYCONFIG_PATH_TARGET_INFO: adapterId(8) id(4) modeInfoIdx(4) outputTechnology(4) rotation(4)
        // scaling(4) refreshRate(8) scanLineOrdering(4) targetAvailable(4) statusFlags(4)
        Assert.Equal(48, Marshal.SizeOf<WindowsHostContextProbe.DisplayConfigPathTargetInfo>());
        Assert.Equal(16, (int)Marshal.OffsetOf<WindowsHostContextProbe.DisplayConfigPathTargetInfo>(nameof(WindowsHostContextProbe.DisplayConfigPathTargetInfo.OutputTechnology)));
        Assert.Equal(28, (int)Marshal.OffsetOf<WindowsHostContextProbe.DisplayConfigPathTargetInfo>(nameof(WindowsHostContextProbe.DisplayConfigPathTargetInfo.RefreshRate)));

        // DISPLAYCONFIG_PATH_INFO { sourceInfo(20); targetInfo(48); UINT32 flags; }
        Assert.Equal(72, Marshal.SizeOf<WindowsHostContextProbe.DisplayConfigPathInfo>());
        Assert.Equal(20, (int)Marshal.OffsetOf<WindowsHostContextProbe.DisplayConfigPathInfo>(nameof(WindowsHostContextProbe.DisplayConfigPathInfo.TargetInfo)));

        // DISPLAYCONFIG_MODE_INFO { infoType(4); id(4); adapterId(8); union(48) } — the union is sized by its
        // largest member, DISPLAYCONFIG_TARGET_MODE. Getting this wrong changes the array stride and every
        // mode after the first is read from the wrong offset.
        Assert.Equal(48, Marshal.SizeOf<WindowsHostContextProbe.DisplayConfigModeUnion>());
        Assert.Equal(64, Marshal.SizeOf<WindowsHostContextProbe.DisplayConfigModeInfo>());
        Assert.Equal(16, (int)Marshal.OffsetOf<WindowsHostContextProbe.DisplayConfigModeInfo>(nameof(WindowsHostContextProbe.DisplayConfigModeInfo.Mode)));

        // DISPLAYCONFIG_DEVICE_INFO_HEADER { type(4); size(4); adapterId(8); id(4); }
        Assert.Equal(20, Marshal.SizeOf<WindowsHostContextProbe.DisplayConfigDeviceInfoHeader>());

        // DISPLAYCONFIG_TARGET_DEVICE_NAME: header(20) flags(4) outputTechnology(4) edidManufactureId(2)
        // edidProductCodeId(2) connectorInstance(4) monitorFriendlyDeviceName[64](128) monitorDevicePath[128](256)
        Assert.Equal(420, Marshal.SizeOf<WindowsHostContextProbe.DisplayConfigTargetDeviceName>());
        Assert.Equal(36, (int)Marshal.OffsetOf<WindowsHostContextProbe.DisplayConfigTargetDeviceName>(nameof(WindowsHostContextProbe.DisplayConfigTargetDeviceName.MonitorFriendlyDeviceName)));
    }

    [Fact]
    public void BluetoothStructsMatchTheNativeLayout()
    {
        Assert.Equal(4, Marshal.SizeOf<WindowsHostContextProbe.BluetoothFindRadioParams>());

        // BLUETOOTH_DEVICE_SEARCH_PARAMS: dwSize(4) + five BOOLs(20) + UCHAR(1) + pad(3) + HANDLE(8)
        Assert.Equal(40, Marshal.SizeOf<WindowsHostContextProbe.BluetoothDeviceSearchParams>());
        Assert.Equal(32, (int)Marshal.OffsetOf<WindowsHostContextProbe.BluetoothDeviceSearchParams>(nameof(WindowsHostContextProbe.BluetoothDeviceSearchParams.Radio)));

        // BLUETOOTH_DEVICE_INFO: dwSize(4) pad(4) Address(8) ulClassofDevice(4) three BOOLs(12)
        // stLastSeen(16) stLastUsed(16) szName[248](496). dwSize must equal 560 or the API rejects the call.
        Assert.Equal(560, Marshal.SizeOf<WindowsHostContextProbe.BluetoothDeviceInfo>());
        Assert.Equal(8, (int)Marshal.OffsetOf<WindowsHostContextProbe.BluetoothDeviceInfo>(nameof(WindowsHostContextProbe.BluetoothDeviceInfo.Address)));
        Assert.Equal(64, (int)Marshal.OffsetOf<WindowsHostContextProbe.BluetoothDeviceInfo>(nameof(WindowsHostContextProbe.BluetoothDeviceInfo.Name)));

        Assert.Equal(16, Marshal.SizeOf<WindowsHostContextProbe.SystemTime>());
    }

    [Fact]
    public void PdhStructsMatchTheNativeLayout()
    {
        // PDH_FMT_COUNTERVALUE { DWORD CStatus; union { ... double doubleValue; } } — the double forces
        // 8-byte alignment, so the value sits at offset 8, not 4.
        Assert.Equal(16, Marshal.SizeOf<WindowsPerformanceSampler.PdhFormattedCounterValue>());
        Assert.Equal(8, (int)Marshal.OffsetOf<WindowsPerformanceSampler.PdhFormattedCounterValue>(nameof(WindowsPerformanceSampler.PdhFormattedCounterValue.DoubleValue)));

        // PDH_FMT_COUNTERVALUE_ITEM_W { LPWSTR szName; PDH_FMT_COUNTERVALUE FmtValue; } — this is the array
        // stride for the per-core DPC read. A wrong size reads every core but the first from the wrong place.
        Assert.Equal(24, Marshal.SizeOf<WindowsPerformanceSampler.PdhFormattedCounterValueItem>());
        Assert.Equal(8, (int)Marshal.OffsetOf<WindowsPerformanceSampler.PdhFormattedCounterValueItem>(nameof(WindowsPerformanceSampler.PdhFormattedCounterValueItem.Value)));
    }

    [Fact]
    public void DevPropKeyMatchesTheNativeLayout()
    {
        // DEVPROPKEY { DEVPROPGUID fmtid; DEVPROPID pid; }
        Assert.Equal(20, Marshal.SizeOf<WindowsBluetoothDeviceProbe.DevPropKey>());
        Assert.Equal(16, (int)Marshal.OffsetOf<WindowsBluetoothDeviceProbe.DevPropKey>(nameof(WindowsBluetoothDeviceProbe.DevPropKey.Pid)));
    }

    [Fact]
    public void SystemPowerStatusMatchesTheNativeLayout()
    {
        Assert.Equal(12, Marshal.SizeOf<WindowsPerformanceSampler.SystemPowerStatus>());
    }
}

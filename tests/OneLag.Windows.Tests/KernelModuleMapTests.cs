using OneLag.Windows;

namespace OneLag.Windows.Tests;

/// <summary>
/// Driver attribution reduces to one question: which loaded kernel image contains this routine address?
/// Extracting it from the ETW session means the answer can be checked without running a kernel trace, using
/// the address ranges Windows actually reports.
/// </summary>
public sealed class KernelModuleMapTests
{
    private const ulong KernelBase = 0xFFFF_F801_0000_0000UL;

    [Fact]
    public void ResolvesARoutineAddressToTheDriverThatContainsIt()
    {
        var map = new KernelModuleMap();
        map.TryAdd(KernelBase, 0x800_000, @"C:\Windows\system32\ntoskrnl.exe");
        map.TryAdd(KernelBase + 0x800_000, 0x40_000, @"C:\Windows\system32\drivers\dlkmdldr.sys");
        map.TryAdd(KernelBase + 0x900_000, 0x20_000, @"C:\Windows\system32\drivers\storport.sys");

        Assert.Equal("ntoskrnl.exe", map.Resolve(KernelBase + 0x1234));
        Assert.Equal("dlkmdldr.sys", map.Resolve(KernelBase + 0x800_000));
        Assert.Equal("dlkmdldr.sys", map.Resolve(KernelBase + 0x83F_FFF));
        Assert.Equal("storport.sys", map.Resolve(KernelBase + 0x905_000));
    }

    [Fact]
    public void AnAddressInAGapBetweenModulesIsUnresolvedRatherThanGuessed()
    {
        // The upper bound is exclusive, and an address past the end of a module belongs to no module. Naming
        // the nearest one would attribute a stall to an innocent driver.
        var map = new KernelModuleMap();
        map.TryAdd(KernelBase, 0x10_000, "ntoskrnl.exe");
        map.TryAdd(KernelBase + 0x100_000, 0x10_000, "dlkmdldr.sys");

        Assert.Equal(KernelModuleMap.Unresolved, map.Resolve(KernelBase + 0x10_000));
        Assert.Equal(KernelModuleMap.Unresolved, map.Resolve(KernelBase + 0x50_000));
        Assert.Equal(KernelModuleMap.Unresolved, map.Resolve(KernelBase - 1));
    }

    [Fact]
    public void UserModeImagesAreRejected()
    {
        // The ImageLoad keyword reports every image in every process. A desktop has tens of thousands of
        // user-mode modules, and keeping them would make each of several hundred thousand DPC lookups walk
        // that list on the single thread consuming the event stream, so ETW would drop events and the totals
        // would silently under-report.
        var map = new KernelModuleMap();

        Assert.False(map.TryAdd(0x0000_7FF6_0000_0000UL, 0x100_000, "chrome.exe"));
        Assert.False(map.TryAdd(0x0000_0180_0000_0000UL, 0x50_000, "kernel32.dll"));
        Assert.True(map.TryAdd(KernelBase, 0x10_000, "ntoskrnl.exe"));

        Assert.Equal(1, map.Count);
        Assert.Equal(KernelModuleMap.Unresolved, map.Resolve(0x0000_7FF6_0000_1000UL));
    }

    [Fact]
    public void TheRundownReportingTheSameModuleTwiceDoesNotDuplicateIt()
    {
        // ImageDCStart reports every resident kernel module at session start and ImageDCStop reports them
        // again at the end.
        var map = new KernelModuleMap();

        Assert.True(map.TryAdd(KernelBase, 0x10_000, "dlkmdldr.sys"));
        Assert.False(map.TryAdd(KernelBase, 0x10_000, "dlkmdldr.sys"));

        Assert.Equal(1, map.Count);
        Assert.Equal("dlkmdldr.sys", map.Resolve(KernelBase + 0x100));
    }

    [Fact]
    public void ZeroSizedAndUnnamedImagesAreRejected()
    {
        var map = new KernelModuleMap();

        Assert.False(map.TryAdd(KernelBase, 0, "empty.sys"));
        Assert.False(map.TryAdd(KernelBase, -1, "negative.sys"));
        Assert.False(map.TryAdd(KernelBase, 0x1000, "   "));

        Assert.Equal(0, map.Count);
    }

    [Fact]
    public void AnEmptyMapResolvesNothing()
    {
        Assert.Equal(KernelModuleMap.Unresolved, new KernelModuleMap().Resolve(KernelBase));
    }

    [Fact]
    public void ModulesAddedOutOfOrderStillResolveCorrectly()
    {
        // ETW does not report images in address order, and the lookup is a binary search over a sorted view,
        // so the view has to be rebuilt whenever a module arrives.
        var map = new KernelModuleMap();
        map.TryAdd(KernelBase + 0x900_000, 0x20_000, "storport.sys");
        map.TryAdd(KernelBase, 0x800_000, "ntoskrnl.exe");

        Assert.Equal("storport.sys", map.Resolve(KernelBase + 0x905_000));

        map.TryAdd(KernelBase + 0x800_000, 0x40_000, "dlkmdldr.sys");

        Assert.Equal("dlkmdldr.sys", map.Resolve(KernelBase + 0x810_000));
        Assert.Equal("ntoskrnl.exe", map.Resolve(KernelBase + 0x10));
        Assert.Equal("storport.sys", map.Resolve(KernelBase + 0x905_000));
    }
}

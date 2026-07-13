namespace OneLag.Windows;

/// <summary>
/// Maps a routine address recorded by a DPC or ISR event back to the kernel driver that contains it.
///
/// This is the whole substance of driver attribution, and it is pure arithmetic over an address range table,
/// so it lives here rather than inside the ETW session closure where it could only be exercised by running a
/// kernel trace.
///
/// Two things make it more than a lookup. The ImageLoad keyword reports every image in every process, and a
/// desktop has tens of thousands of user-mode modules; keeping them would make each of several hundred
/// thousand DPC lookups walk a list of that size, on the single thread that has to keep up with the event
/// stream. And the rundown reports the same kernel module more than once, so it must be idempotent.
/// </summary>
public sealed class KernelModuleMap
{
    /// <summary>
    /// Kernel-mode addresses on x64 and arm64 live in the upper half of the address space. Everything below
    /// this is a user-mode image and cannot host a DPC or ISR routine.
    /// </summary>
    public const ulong KernelAddressFloor = 0xFFFF_8000_0000_0000UL;

    public const string Unresolved = "unresolved";

    private readonly Dictionary<ulong, Module> modulesByBase = new();
    private Module[] sorted = Array.Empty<Module>();
    private bool dirty;

    public int Count => modulesByBase.Count;

    /// <summary>
    /// Returns false when the image was ignored, so a caller can tell "not a kernel image" from "added".
    /// </summary>
    public bool TryAdd(ulong imageBase, long imageSize, string fileName)
    {
        if (imageSize <= 0 || imageBase < KernelAddressFloor || string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        if (!modulesByBase.TryAdd(imageBase, new Module(imageBase, imageBase + (ulong)imageSize, DriverFileName(fileName))))
        {
            return false;
        }

        dirty = true;
        return true;
    }

    /// <summary>
    /// ETW reports a full Windows path such as <c>C:\Windows\system32\drivers\dlkmdldr.sys</c>. Path.GetFileName
    /// resolves separators against the *host* operating system, so on a non-Windows machine it would treat the
    /// backslashes as ordinary characters and hand back the entire path as the driver name. This data comes
    /// from Windows regardless of where the code runs, so the separators are split explicitly.
    /// </summary>
    private static string DriverFileName(string path)
    {
        var separator = path.LastIndexOfAny(new[] { '\\', '/' });
        return separator >= 0 ? path[(separator + 1)..] : path;
    }

    public string Resolve(ulong address)
    {
        if (address < KernelAddressFloor || modulesByBase.Count == 0)
        {
            return Unresolved;
        }

        if (dirty)
        {
            sorted = modulesByBase.Values.OrderBy(module => module.Start).ToArray();
            dirty = false;
        }

        // Binary search for the last module whose start is at or below the address. The event rate is high
        // enough that a linear scan here shows up as dropped events rather than as a slow test.
        var low = 0;
        var high = sorted.Length - 1;
        var candidate = -1;

        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            if (sorted[middle].Start <= address)
            {
                candidate = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        if (candidate < 0)
        {
            return Unresolved;
        }

        var module = sorted[candidate];
        return address < module.End ? module.Name : Unresolved;
    }

    private readonly record struct Module(ulong Start, ulong End, string Name);
}

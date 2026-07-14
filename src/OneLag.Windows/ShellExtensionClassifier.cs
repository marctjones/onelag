namespace OneLag.Windows;

/// <summary>
/// Classifies a registered shell extension as first-party (Microsoft/in-box) or third-party.
///
/// Kept pure so it is testable off Windows: the registry enumeration is an unavoidable Windows shell
/// (<see cref="WindowsShellExtensionProbe"/>); everything that can be decided from the resolved DLL path and
/// display name belongs here instead, matching the split already used for the filter driver stack
/// (<see cref="FltmcOutputParser"/>).
/// </summary>
internal static class ShellExtensionClassifier
{
    private const string DefaultWindowsRoot = @"C:\Windows";

    /// <summary>
    /// Convenience overload for the live probe: resolves %SystemRoot% from the environment. Tests should call
    /// the three-argument overload directly with a fixed root so results are deterministic off Windows.
    /// </summary>
    public static bool IsMicrosoft(string? name, string? dllPath) => IsMicrosoft(name, dllPath, ResolveWindowsRoot());

    public static bool IsMicrosoft(string? name, string? dllPath, string windowsRoot)
    {
        if (PathContainment.IsUnderRoot(dllPath, windowsRoot))
        {
            return true;
        }

        // A DLL outside %SystemRoot% does not rule out Microsoft — OneDrive itself, Office, and Visual Studio
        // all install shell extensions under Program Files — so a name that plainly says Microsoft is treated
        // as corroborating evidence rather than ignored. A false negative here (calling a Microsoft extension
        // third-party) is worse than a rare false positive, because it inflates the crowded-overlay-list
        // finding with something the user cannot uninstall.
        return !string.IsNullOrWhiteSpace(name) && name.Contains("Microsoft", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveWindowsRoot()
    {
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
        return string.IsNullOrWhiteSpace(systemRoot) ? DefaultWindowsRoot : systemRoot;
    }
}

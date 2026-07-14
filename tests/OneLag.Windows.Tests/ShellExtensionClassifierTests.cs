using OneLag.Windows;

namespace OneLag.Windows.Tests;

/// <summary>
/// These run everywhere, which is the whole reason the Microsoft/third-party classification is pulled out of
/// <see cref="WindowsShellExtensionProbe"/> into a pure predicate. Every case is given a fixed
/// <c>windowsRoot</c> so the result does not depend on the machine running the test.
/// </summary>
public sealed class ShellExtensionClassifierTests
{
    private const string WindowsRoot = @"C:\Windows";

    [Fact]
    public void Dll_under_system32_is_classified_as_microsoft()
    {
        var isMicrosoft = ShellExtensionClassifier.IsMicrosoft(
            " OverlayIconHandler",
            @"C:\Windows\System32\shell32.dll",
            WindowsRoot);

        Assert.True(isMicrosoft);
    }

    [Fact]
    public void Dll_under_a_windows_subfolder_with_different_case_is_still_microsoft()
    {
        var isMicrosoft = ShellExtensionClassifier.IsMicrosoft(
            "Windows Explorer overlay",
            @"c:\windows\system32\windows.storage.dll",
            WindowsRoot);

        Assert.True(isMicrosoft);
    }

    [Fact]
    public void Third_party_dll_under_program_files_with_generic_name_is_not_microsoft()
    {
        var isMicrosoft = ShellExtensionClassifier.IsMicrosoft(
            "DropboxExt15",
            @"C:\Program Files (x86)\Dropbox\Client\DropboxExt.65.0.dll",
            WindowsRoot);

        Assert.False(isMicrosoft);
    }

    [Fact]
    public void Path_outside_windows_root_but_name_says_microsoft_is_still_classified_as_microsoft()
    {
        // First-party components (OneDrive, Office) commonly install outside %SystemRoot%, so the name is
        // used as corroborating evidence rather than requiring the path check to carry the whole decision.
        var isMicrosoft = ShellExtensionClassifier.IsMicrosoft(
            "Microsoft OneDrive Sync Status",
            @"C:\Program Files\Microsoft OneDrive\OneDrive.SyncEngine.dll",
            WindowsRoot);

        Assert.True(isMicrosoft);
    }

    [Fact]
    public void Sibling_folder_with_windows_prefixed_name_is_not_mistaken_for_the_windows_root()
    {
        // "C:\WindowsApps" is a sibling of "C:\Windows", not a child of it — this is the same containment
        // trap PathContainmentTests exercises directly, checked here at the classifier boundary.
        var isMicrosoft = ShellExtensionClassifier.IsMicrosoft(
            "Third Party Overlay",
            @"C:\WindowsApps\SomeVendor\overlay.dll",
            WindowsRoot);

        Assert.False(isMicrosoft);
    }

    [Fact]
    public void Unresolved_dll_path_with_no_microsoft_name_is_not_microsoft()
    {
        var isMicrosoft = ShellExtensionClassifier.IsMicrosoft("SomeVendorOverlay", null, WindowsRoot);

        Assert.False(isMicrosoft);
    }
}

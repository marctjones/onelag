using OneLag.Windows;

namespace OneLag.Windows.Tests;

/// <summary>
/// These run everywhere, which is the whole reason the <see cref="OneLag.Core.KnownFolderRedirect"/> decision
/// is pulled out of <see cref="WindowsFileSystemContextProbe"/> — every path in that probe comes from a real
/// Win32 call or registry read and cannot be exercised on macOS, so the wiring that turns a resolved path into
/// the contract type is proven here instead. This is the exact scenario that motivated
/// <see cref="OneLag.Core.FileSystemContext"/>: Documents redirected by OneDrive's Known Folder Move into a
/// work OneDrive tenant folder, so a native open-file dialog that defaults to Documents silently lands inside a
/// cloud-synced root.
/// </summary>
public sealed class KnownFolderRedirectClassifierTests
{
    private static readonly string[] CiscoOneDriveRoots = { @"C:\Users\x\OneDrive - Cisco" };

    [Fact]
    public void Documents_redirected_into_the_cisco_OneDrive_tenant_is_flagged_as_redirected()
    {
        var redirect = KnownFolderRedirectClassifier.Classify(
            "Documents",
            @"C:\Users\x\OneDrive - Cisco\Documents",
            CiscoOneDriveRoots);

        Assert.NotNull(redirect);
        Assert.Equal("Documents", redirect!.KnownFolder);
        Assert.Equal(@"C:\Users\x\OneDrive - Cisco\Documents", redirect.Path);
        Assert.True(redirect.RedirectedIntoCloudRoot);
    }

    [Fact]
    public void Desktop_left_in_the_default_user_profile_location_is_not_flagged()
    {
        var redirect = KnownFolderRedirectClassifier.Classify(
            "Desktop",
            @"C:\Users\x\Desktop",
            CiscoOneDriveRoots);

        Assert.NotNull(redirect);
        Assert.False(redirect!.RedirectedIntoCloudRoot);
    }

    [Fact]
    public void A_sibling_folder_that_merely_shares_a_name_prefix_with_the_root_is_not_flagged()
    {
        // Regression guard for the exact containment trap PathContainmentTests exercises directly, checked
        // here through the actual classification path a probe result flows through.
        var redirect = KnownFolderRedirectClassifier.Classify(
            "Documents",
            @"C:\Users\x\OneDriveFoo\Documents",
            new[] { @"C:\Users\x\OneDrive" });

        Assert.NotNull(redirect);
        Assert.False(redirect!.RedirectedIntoCloudRoot);
    }

    [Fact]
    public void A_folder_under_any_of_several_discovered_roots_is_flagged()
    {
        var roots = new[] { @"C:\Users\x\OneDrive - Cisco", @"C:\Users\x\OneDrive Personal" };

        var redirect = KnownFolderRedirectClassifier.Classify(
            "Pictures",
            @"C:\Users\x\OneDrive Personal\Pictures",
            roots);

        Assert.NotNull(redirect);
        Assert.True(redirect!.RedirectedIntoCloudRoot);
    }

    [Fact]
    public void An_unresolved_folder_path_produces_no_record_instead_of_a_fabricated_one()
    {
        var redirect = KnownFolderRedirectClassifier.Classify("Downloads", null, CiscoOneDriveRoots);

        Assert.Null(redirect);
    }

    [Fact]
    public void No_discovered_OneDrive_roots_means_nothing_can_be_flagged_as_redirected()
    {
        var redirect = KnownFolderRedirectClassifier.Classify(
            "Documents",
            @"C:\Users\x\OneDrive - Cisco\Documents",
            Array.Empty<string>());

        Assert.NotNull(redirect);
        Assert.False(redirect!.RedirectedIntoCloudRoot);
    }
}

using OneLag.Windows;

namespace OneLag.Windows.Tests;

/// <summary>
/// These run everywhere, which is the whole reason the containment check is pulled out of
/// <see cref="WindowsFileSystemContextProbe"/> into a pure predicate. The fixture below reproduces the actual
/// user situation that motivated <see cref="OneLag.Core.KnownFolderRedirect"/>: Documents redirected by
/// OneDrive's Known Folder Move into a work OneDrive tenant folder, so every native open-file dialog — which
/// defaults to Documents — silently lands inside a cloud-synced root.
/// </summary>
public sealed class PathContainmentTests
{
    [Fact]
    public void Documents_redirected_into_OneDrive_tenant_folder_is_detected()
    {
        const string documentsPath = @"C:\Users\x\OneDrive - Cisco\Documents";
        const string oneDriveRoot = @"C:\Users\x\OneDrive - Cisco";

        Assert.True(PathContainment.IsUnderRoot(documentsPath, oneDriveRoot));
    }

    [Fact]
    public void Sibling_folder_with_prefix_matching_name_is_not_treated_as_contained()
    {
        // "OneDriveFoo" is a string-prefix of "OneDrive" plus extra characters, but it is a sibling folder,
        // not a child of the OneDrive root. A naive StartsWith would get this wrong.
        const string path = @"C:\Users\x\OneDriveFoo\Documents";
        const string root = @"C:\Users\x\OneDrive";

        Assert.False(PathContainment.IsUnderRoot(path, root));
    }

    [Fact]
    public void Trailing_separator_on_root_does_not_change_the_result()
    {
        const string path = @"C:\Users\x\OneDrive - Cisco\Documents";
        const string rootWithTrailingSeparator = @"C:\Users\x\OneDrive - Cisco\";

        Assert.True(PathContainment.IsUnderRoot(path, rootWithTrailingSeparator));
    }

    [Fact]
    public void Case_differences_do_not_change_the_result()
    {
        const string path = @"c:\users\x\onedrive - cisco\documents";
        const string root = @"C:\Users\X\OneDrive - Cisco";

        Assert.True(PathContainment.IsUnderRoot(path, root));
    }

    [Fact]
    public void The_root_itself_counts_as_contained()
    {
        const string root = @"C:\Users\x\OneDrive - Cisco";

        Assert.True(PathContainment.IsUnderRoot(root, root));
    }

    [Fact]
    public void Forward_slashes_are_treated_the_same_as_backslashes()
    {
        const string path = "C:/Users/x/OneDrive - Cisco/Documents";
        const string root = @"C:\Users\x\OneDrive - Cisco";

        Assert.True(PathContainment.IsUnderRoot(path, root));
    }

    [Fact]
    public void Unrelated_path_is_not_contained()
    {
        const string path = @"C:\Users\x\Desktop";
        const string root = @"C:\Users\x\OneDrive - Cisco";

        Assert.False(PathContainment.IsUnderRoot(path, root));
    }

    [Theory]
    [InlineData(null, @"C:\OneDrive")]
    [InlineData(@"C:\OneDrive\Documents", null)]
    [InlineData("", @"C:\OneDrive")]
    [InlineData(@"C:\OneDrive\Documents", "")]
    public void Missing_inputs_never_report_contained(string? path, string? root)
    {
        Assert.False(PathContainment.IsUnderRoot(path, root));
    }
}

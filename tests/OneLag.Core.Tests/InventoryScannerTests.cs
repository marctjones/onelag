using OneLag.Core;

namespace OneLag.Core.Tests;

public sealed class InventoryScannerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "onelag-tests", Guid.NewGuid().ToString("N"));

    public InventoryScannerTests()
    {
        Directory.CreateDirectory(root);
    }

    [Fact]
    public void ScanDetectsHighRiskDevelopmentDirectories()
    {
        Directory.CreateDirectory(Path.Combine(root, "project", "node_modules"));
        File.WriteAllText(Path.Combine(root, "project", "node_modules", "package.txt"), "x");

        var summary = new InventoryScanner().Scan(root, 10_000, CancellationToken.None);

        Assert.Contains(summary.HighRiskDirectories, risk => risk.Name == "node_modules");
        Assert.Equal(1, summary.FileCount);
    }

    [Fact]
    public void ScanDetectsTemporaryLargeAndReparseBlockersWithoutThrowing()
    {
        File.WriteAllText(Path.Combine(root, "build.tmp"), "temp");
        File.WriteAllBytes(Path.Combine(root, "mail.pst"), new byte[1024]);

        var summary = new InventoryScanner().Scan(root, 10_000, CancellationToken.None);

        Assert.Contains(summary.SyncBlockers, blocker => blocker.Kind == "temporary-file");
        Assert.Contains(summary.SyncBlockers, blocker => blocker.Kind == "mail-data-file");
        Assert.DoesNotContain(summary.SyncBlockers, blocker => blocker.Kind == "large-risk-file");
    }

    [Fact]
    public void ScanDetectsKnownOneDriveBlockedNames()
    {
        Directory.CreateDirectory(Path.Combine(root, "forms"));
        Directory.CreateDirectory(Path.Combine(root, "project_vti_cache"));
        File.WriteAllText(Path.Combine(root, ".lock"), "lock");
        File.WriteAllText(Path.Combine(root, "desktop.ini"), "desktop");
        File.WriteAllText(Path.Combine(root, "~$draft.docx"), "office");
        File.WriteAllText(Path.Combine(root, "notes.one"), "onenote");

        var summary = new InventoryScanner().Scan(root, 10_000, CancellationToken.None);

        Assert.Contains(summary.SyncBlockers, blocker => blocker.Kind == "blocked-name");
        Assert.Contains(summary.SyncBlockers, blocker => blocker.Kind == "root-forms-name");
        Assert.Contains(summary.SyncBlockers, blocker => blocker.Kind == "onenote-notebook-file");
    }

    [Fact]
    public void KnownIssueRulesFlagInvalidCharactersAndLongPathsWithoutFilesystemSupport()
    {
        var rootPath = @"C:\Users\test\OneDrive";
        var relativePath = new string('a', OneDriveKnownIssueRules.MaximumDecodedRelativePathLength + 1);
        var path = Path.Combine(rootPath, relativePath, "bad:name.txt");

        var blockers = OneDriveKnownIssueRules.InspectEntry(rootPath, path, "bad:name.txt", FileAttributes.Archive, false, 1);

        Assert.Contains(blockers, blocker => blocker.Kind == "invalid-character");
        Assert.Contains(blockers, blocker => blocker.Kind == "long-onedrive-relative-path");
    }

    [Fact]
    public void ScanCapsLargeTrees()
    {
        for (var i = 0; i < 20; i++)
        {
            File.WriteAllText(Path.Combine(root, $"{i}.txt"), "x");
        }

        var summary = new InventoryScanner().Scan(root, 5, CancellationToken.None);

        Assert.True(summary.WasCapped);
        Assert.True(summary.TotalItems >= 5);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

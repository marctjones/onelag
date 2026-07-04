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
        Assert.DoesNotContain(summary.SyncBlockers, blocker => blocker.Kind == "large-risk-file");
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

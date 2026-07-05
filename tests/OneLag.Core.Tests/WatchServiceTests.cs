using OneLag.Core;

namespace OneLag.Core.Tests;

public sealed class WatchServiceTests : IDisposable
{
    private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "onelag-watch-service-tests", Guid.NewGuid().ToString("N"));

    public WatchServiceTests()
    {
        Directory.CreateDirectory(tempRoot);
    }

    [Fact]
    public void MarkAndReportUseSharedStorage()
    {
        var service = new WatchService();
        var marker = service.Mark(tempRoot, "test", "typing froze");
        var reportPath = Path.Combine(tempRoot, "watch-report.md");

        var fullReportPath = service.WriteReport(tempRoot, reportPath);

        Assert.True(File.Exists(fullReportPath));
        Assert.Equal(marker.Timestamp, Assert.Single(service.ReadMarkers(tempRoot)).Timestamp);
        var report = File.ReadAllText(fullReportPath);
        Assert.Contains("OneLag Watch Report", report);
        Assert.Contains("typing froze", report);
        Assert.Contains("## Episodes", report);
    }

    public void Dispose()
    {
        if (Directory.Exists(tempRoot))
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }
}

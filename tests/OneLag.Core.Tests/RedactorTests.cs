using OneLag.Core;

namespace OneLag.Core.Tests;

public sealed class RedactorTests
{
    [Fact]
    public void PathValueRedactsKnownRootWhenFullPathsDisabled()
    {
        var root = Path.Combine(Path.GetTempPath(), "OneDrive");
        var redactor = new Redactor(fullPaths: false, new[] { root });

        var redacted = redactor.PathValue(Path.Combine(root, "project", "file.txt"));

        Assert.StartsWith("<root:1>", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void PathValueKeepsPathWhenFullPathsEnabled()
    {
        var root = Path.Combine(Path.GetTempPath(), "OneDrive");
        var path = Path.Combine(root, "project", "file.txt");
        var redactor = new Redactor(fullPaths: true, new[] { root });

        Assert.Equal(path, redactor.PathValue(path));
    }
}

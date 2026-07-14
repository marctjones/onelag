using OneLag.Core;

namespace OneLag.Core.Tests;

/// <summary>
/// OutputPaths is what stops a second capture from destroying the first, so its two behaviours are tested
/// directly and deterministically: no wall-clock DateTimeOffset.Now in an assertion, and no reliance on a
/// real filesystem beyond a disposable temp directory.
/// </summary>
public sealed class OutputPathsTests : IDisposable
{
    private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "onelag-output-paths-tests", Guid.NewGuid().ToString("N"));

    public OutputPathsTests()
    {
        Directory.CreateDirectory(tempRoot);
    }

    [Fact]
    public void TimestampedFormatsSortableLocalTimestampWithExtension()
    {
        // A fixed offset, not DateTimeOffset.Now: the assertion has to be reproducible regardless of when or
        // in what timezone the test runs.
        var now = new DateTimeOffset(2026, 7, 14, 17, 52, 30, TimeSpan.FromHours(-4));

        var result = OutputPaths.Timestamped("onelag-report", "md", now);

        var expectedStamp = now.ToLocalTime().ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal($"onelag-report-{expectedStamp}.md", result);
    }

    [Fact]
    public void TimestampedAcceptsExtensionWithLeadingDot()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var withDot = OutputPaths.Timestamped("onelag-report", ".md", now);
        var withoutDot = OutputPaths.Timestamped("onelag-report", "md", now);

        Assert.Equal(withoutDot, withDot);
    }

    [Fact]
    public void TimestampedWithEmptyExtensionProducesNoTrailingDot()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var result = OutputPaths.Timestamped("onelag-trace-plan", string.Empty, now);

        Assert.DoesNotContain('.', result);
        Assert.StartsWith("onelag-trace-plan-", result, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureWritableThrowsOnExistingFileWithoutOverwrite()
    {
        var path = Path.Combine(tempRoot, "existing.md");
        File.WriteAllText(path, "previous capture");

        var ex = Assert.Throws<InvalidOperationException>(() => OutputPaths.EnsureWritable(path, overwrite: false));
        Assert.Contains("--overwrite", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureWritableThrowsOnNonEmptyExistingDirectoryWithoutOverwrite()
    {
        var directory = Path.Combine(tempRoot, "existing-dir");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "file.txt"), "previous capture");

        Assert.Throws<InvalidOperationException>(() => OutputPaths.EnsureWritable(directory, overwrite: false));
    }

    [Fact]
    public void EnsureWritableDoesNotThrowOnEmptyExistingDirectory()
    {
        var directory = Path.Combine(tempRoot, "empty-dir");
        Directory.CreateDirectory(directory);

        var exception = Record.Exception(() => OutputPaths.EnsureWritable(directory, overwrite: false));

        Assert.Null(exception);
    }

    [Fact]
    public void EnsureWritableDoesNotThrowWhenPathIsAbsent()
    {
        var path = Path.Combine(tempRoot, "does-not-exist.md");

        var exception = Record.Exception(() => OutputPaths.EnsureWritable(path, overwrite: false));

        Assert.Null(exception);
    }

    [Fact]
    public void EnsureWritableDoesNotThrowWhenOverwriteIsTrue()
    {
        var path = Path.Combine(tempRoot, "existing.md");
        File.WriteAllText(path, "previous capture");

        var exception = Record.Exception(() => OutputPaths.EnsureWritable(path, overwrite: true));

        Assert.Null(exception);
    }

    public void Dispose()
    {
        if (Directory.Exists(tempRoot))
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }
}

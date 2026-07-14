using OneLag.Core;

namespace OneLag.Core.Tests;

/// <summary>
/// The unaccounted-commit section is the one figure Task Manager cannot show at all, so the report has to
/// surface it unmissably whenever it clears the 4 GB bar FreezeCaptureService.BuildFindings also uses.
/// </summary>
public sealed class FreezeReportWriterTests
{
    private static Redactor Redactor() => new(fullPaths: true, Array.Empty<string>());

    [Fact]
    public void MarkdownRendersTheUnaccountedCommitSectionWhenItIsAtLeastFourGigabytes()
    {
        var probe = new FakePlatformProbe { Memory = Snapshots.HeavyUnaccountedCommit() };
        var capture = new FreezeCaptureService(probe).Capture(new FreezeCaptureOptions(TimeSpan.FromSeconds(1), null, SkipDriverTrace: true));

        Assert.True(capture.Memory.UnaccountedCommitBytes >= 4L * 1024 * 1024 * 1024);

        var markdown = FreezeReportWriter.ToMarkdown(capture, Redactor());

        Assert.Contains("### Unaccounted commit:", markdown);
        Assert.Contains("No user-mode process holds this memory", markdown);
    }

    [Fact]
    public void MarkdownOmitsTheUnaccountedCommitSectionWhenMemoryIsUnavailable()
    {
        var probe = new FakePlatformProbe();
        var capture = new FreezeCaptureService(probe).Capture(new FreezeCaptureOptions(TimeSpan.FromSeconds(1), null, SkipDriverTrace: true));

        var markdown = FreezeReportWriter.ToMarkdown(capture, Redactor());

        Assert.DoesNotContain("### Unaccounted commit:", markdown);
    }

    [Fact]
    public void JsonRoundTripsWithoutThrowing()
    {
        var probe = new FakePlatformProbe { Memory = Snapshots.HeavyUnaccountedCommit() };
        var capture = new FreezeCaptureService(probe).Capture(new FreezeCaptureOptions(TimeSpan.FromSeconds(1), "note", SkipDriverTrace: true));

        var json = FreezeReportWriter.ToJson(capture);

        Assert.Contains("\"Note\"", json);
    }
}

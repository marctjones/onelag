using OneLag.Core;

namespace OneLag.Core.Tests;

/// <summary>
/// The detector decides whether the machine is frozen without the user's help, so its false-positive and
/// false-negative behaviour is the whole product: firing on jitter would fill an all-day run with captures of
/// nothing, and failing to fire would leave the user exactly where he started — needing to act during a freeze.
/// </summary>
public sealed class FreezeDetectorTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-07-13T10:00:00Z");

    [Fact]
    public void DriftBelowThresholdDoesNotFire()
    {
        var detector = new FreezeDetector();

        for (var i = 0; i < 10; i++)
        {
            var detection = detector.Evaluate(Sample(i, driftMilliseconds: 120));
            Assert.False(detection.Triggered);
        }

        Assert.Equal(0, detector.DeepCaptures);
    }

    [Fact]
    public void SingleLateTickDoesNotFire()
    {
        // One late tick on a 1s timer is ordinary scheduler noise. Treating it as a freeze would make the
        // watcher cry wolf on every GC pause on the machine.
        var detector = new FreezeDetector();

        Assert.False(detector.Evaluate(Sample(0, driftMilliseconds: 50)).Triggered);
        Assert.False(detector.Evaluate(Sample(1, driftMilliseconds: 900)).Triggered);
        Assert.False(detector.Evaluate(Sample(2, driftMilliseconds: 40)).Triggered);
        Assert.Equal(0, detector.DeepCaptures);
    }

    [Fact]
    public void SustainedDriftFires()
    {
        var detector = new FreezeDetector();

        Assert.False(detector.Evaluate(Sample(0, driftMilliseconds: 900)).Triggered);
        var detection = detector.Evaluate(Sample(1, driftMilliseconds: 1_400));

        Assert.True(detection.Triggered);
        Assert.True(detection.ShouldCapture);
        Assert.Equal("timer-drift-sustained", detection.Trigger);
        Assert.Contains("1,400 ms", detection.Note);
        Assert.Equal(1, detector.DeepCaptures);
    }

    [Fact]
    public void SevereDriftFiresOnASingleTick()
    {
        // Four seconds lost on a one-second timer is not jitter under any load, and waiting for a confirming
        // tick would mean waiting out the freeze we exist to capture.
        var detection = new FreezeDetector().Evaluate(Sample(0, driftMilliseconds: 6_000));

        Assert.True(detection.Triggered);
        Assert.Equal("timer-drift-severe", detection.Trigger);
    }

    [Fact]
    public void HungShellFiresOnItsOwn()
    {
        // The shell not pumping messages IS the freeze the user is describing, whatever the timer says.
        var detection = new FreezeDetector().Evaluate(Sample(0, driftMilliseconds: 5, shell: Snapshots.HungShell()));

        Assert.True(detection.Triggered);
        Assert.Equal("shell-hung", detection.Trigger);
    }

    [Fact]
    public void ExhaustedCommitHeadroomFires()
    {
        var memory = Snapshots.HeavyUnaccountedCommit() with
        {
            CommitTotalBytes = 31_900L * 1024 * 1024,
            CommitLimitBytes = 32_000L * 1024 * 1024
        };

        var detection = new FreezeDetector().Evaluate(Sample(0, driftMilliseconds: 5, memory: memory));

        Assert.True(detection.Triggered);
        Assert.Equal("commit-headroom-exhausted", detection.Trigger);
    }

    [Fact]
    public void OneFreezeProducesOneDetection()
    {
        // A 30-second freeze is one episode. Re-arming on every tick it persists would produce thirty.
        var detector = new FreezeDetector();
        var triggered = 0;

        for (var i = 0; i < 30; i++)
        {
            if (detector.Evaluate(Sample(i, driftMilliseconds: 5_000)).Triggered)
            {
                triggered++;
            }
        }

        Assert.Equal(1, triggered);
        Assert.Equal(1, detector.DeepCaptures);
    }

    [Fact]
    public void CooldownSuppressesTheDeepCaptureButStillRecordsTheFreeze()
    {
        var detector = new FreezeDetector(new FreezeDetectorOptions { DeepCaptureCooldown = TimeSpan.FromMinutes(2) });

        Assert.True(detector.Evaluate(Sample(0, driftMilliseconds: 5_000)).ShouldCapture);
        Assert.False(detector.Evaluate(Sample(1, driftMilliseconds: 10)).Triggered);

        var second = detector.Evaluate(Sample(30, driftMilliseconds: 5_000));

        Assert.True(second.Triggered);
        Assert.False(second.ShouldCapture);
        Assert.True(second.SkippedByCooldown);
        Assert.Contains(FreezeDetector.CooldownSkipToken, second.Note);
        Assert.Equal(1, detector.DeepCaptures);

        // Once the cooldown has elapsed, the next freeze is captured again.
        Assert.False(detector.Evaluate(Sample(200, driftMilliseconds: 10)).Triggered);
        Assert.True(detector.Evaluate(Sample(201, driftMilliseconds: 5_000)).ShouldCapture);
        Assert.Equal(2, detector.DeepCaptures);
    }

    [Fact]
    public void CapIsEnforcedAndTheDroppedCapturesAreReported()
    {
        // A silent cap reads as "this only happened three times", which would be a lie. The detection is still
        // recorded and the drop is written into the note the report is rebuilt from.
        var detector = new FreezeDetector(new FreezeDetectorOptions
        {
            MaxDeepCaptures = 3,
            DeepCaptureCooldown = TimeSpan.Zero
        });

        var captured = 0;
        var dropped = 0;

        for (var freeze = 0; freeze < 10; freeze++)
        {
            var detection = detector.Evaluate(Sample(freeze * 10, driftMilliseconds: 5_000));
            Assert.True(detection.Triggered);

            if (detection.ShouldCapture)
            {
                captured++;
            }
            else
            {
                dropped++;
                Assert.True(detection.SkippedByCap);
                Assert.Contains(FreezeDetector.CapSkipToken, detection.Note);
            }

            // Recovery between freezes, so each one is a fresh edge.
            detector.Evaluate(Sample((freeze * 10) + 1, driftMilliseconds: 10));
        }

        Assert.Equal(3, captured);
        Assert.Equal(7, dropped);
        Assert.Equal(3, detector.DeepCaptures);
        Assert.Equal(7, detector.DeepCapturesDroppedByCap);
    }

    private static WatchSample Sample(
        int secondsFromStart,
        double driftMilliseconds,
        ShellResponsiveness? shell = null,
        MemoryPressureDetail? memory = null)
    {
        return new WatchSample(
            Start.AddSeconds(secondsFromStart),
            driftMilliseconds,
            Snapshots.QuietTelemetry(),
            Snapshots.QuietPressure(),
            "explorer",
            Snapshots.UndockedHost(),
            shell ?? Snapshots.ResponsiveShell(),
            memory);
    }
}

using OneLag.Windows;

namespace OneLag.Windows.Tests;

/// <summary>
/// These run everywhere, which is the whole reason the event parsing is separated from the event reading.
/// The events modelled here are the ones actually found on the machine that motivated this probe: Windows had
/// already fired RADAR against dwm.exe 58 seconds before the capture, and OneLag scored memory pressure at
/// zero because it never read them.
/// </summary>
public sealed class RadarLeakEventParserTests
{
    /// <summary>The moment of the capture that these events were taken from.</summary>
    private static readonly DateTimeOffset Now = new(2026, 7, 14, 14, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// WER 1001 with the RADAR_PRE_LEAK_64 bucket: Windows' own leak detector naming the process. This is the
    /// single strongest piece of evidence available, and it names dwm.exe.
    /// </summary>
    private const string WerRadarPreLeak = """
    <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
      <System>
        <Provider Name="Windows Error Reporting" />
        <EventID Qualifiers="0">1001</EventID>
        <Level>4</Level>
        <Channel>Application</Channel>
        <TimeCreated SystemTime="2026-07-14T13:59:02.0000000Z" />
      </System>
      <EventData>
        <Data Name="Bucket">1234567890</Data>
        <Data Name="BucketType">5</Data>
        <Data Name="EventName">RADAR_PRE_LEAK_64</Data>
        <Data Name="Response">Not available</Data>
        <Data Name="CabId">0</Data>
        <Data Name="P1">dwm.exe</Data>
        <Data Name="P2">10.0.26100.8521</Data>
        <Data Name="P3">10.0.26100.2454</Data>
        <Data Name="P4"></Data>
      </EventData>
    </Event>
    """;

    /// <summary>
    /// An ordinary application crash, also reported as WER 1001. If the parser matched on the event id alone
    /// it would report every crashing application as a memory leak.
    /// </summary>
    private const string WerAppCrash = """
    <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
      <System>
        <Provider Name="Windows Error Reporting" />
        <EventID Qualifiers="0">1001</EventID>
        <Level>4</Level>
        <Channel>Application</Channel>
        <TimeCreated SystemTime="2026-07-14T09:12:00.0000000Z" />
      </System>
      <EventData>
        <Data Name="Bucket">0987654321</Data>
        <Data Name="BucketType">4</Data>
        <Data Name="EventName">APPCRASH</Data>
        <Data Name="Response">Not available</Data>
        <Data Name="CabId">0</Data>
        <Data Name="P1">notepad.exe</Data>
        <Data Name="P2">10.0.26100.1</Data>
      </EventData>
    </Event>
    """;

    /// <summary>
    /// Resource-Exhaustion-Resolver 1014, which names the process and carries its creation time. The process
    /// had been up 8.4 days at the moment of the capture — commit that climbs across days of process uptime
    /// is the definition of a leak.
    /// </summary>
    private const string ResolverLeak = """
    <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
      <System>
        <Provider Name="Microsoft-Windows-Resource-Exhaustion-Resolver" />
        <EventID>1014</EventID>
        <Level>4</Level>
        <Channel>System</Channel>
        <TimeCreated SystemTime="2026-07-14T13:58:00.0000000Z" />
      </System>
      <EventData>
        <Data Name="ProcessImageName">dwm.exe</Data>
        <Data Name="ProcessId">22608</Data>
        <Data Name="ProcessCreationTime">2026-07-06T04:18:58Z</Data>
      </EventData>
    </Event>
    """;

    [Fact]
    public void WerRadarPreLeakBucketNamesTheLeakingProcess()
    {
        var candidates = RadarLeakEventParser.Parse(WerRadarPreLeak, Now);

        var candidate = Assert.Single(candidates);
        Assert.Equal("dwm.exe", candidate.ProcessName);
        Assert.Equal("wer-radar-pre-leak-64", candidate.Source);
        Assert.Equal(new DateTimeOffset(2026, 7, 14, 13, 59, 2, TimeSpan.Zero), candidate.ObservedAt);

        // WER reports the image, not the process, so there is no pid or process uptime to claim.
        Assert.Null(candidate.ProcessId);
        Assert.Null(candidate.ProcessUptime);
    }

    [Fact]
    public void WerCrashThatIsNotALeakBucketIsIgnored()
    {
        var candidates = RadarLeakEventParser.Parse(WerAppCrash, Now);

        Assert.Empty(candidates);
    }

    [Fact]
    public void ResolverEventDerivesProcessUptimeFromCreationTime()
    {
        var candidates = RadarLeakEventParser.Parse(ResolverLeak, Now);

        var candidate = Assert.Single(candidates);
        Assert.Equal("dwm.exe", candidate.ProcessName);
        Assert.Equal(22608, candidate.ProcessId);
        Assert.Equal("resource-exhaustion-resolver-1014", candidate.Source);

        Assert.NotNull(candidate.ProcessUptime);
        Assert.Equal(8.4, candidate.ProcessUptime!.Value.TotalDays, precision: 1);
    }

    [Fact]
    public void BothDetectorsNamingTheSameProcessAreReportedSeparately()
    {
        var candidates = RadarLeakEventParser.Parse(WerRadarPreLeak + WerAppCrash + ResolverLeak, Now);

        Assert.Equal(2, candidates.Count);
        Assert.All(candidates, candidate => Assert.Equal("dwm.exe", candidate.ProcessName));
        Assert.Contains(candidates, candidate => candidate.Source == "wer-radar-pre-leak-64");
        Assert.Contains(candidates, candidate => candidate.Source == "resource-exhaustion-resolver-1014");

        // Newest first: the accusation closest to the freeze is the one worth reading.
        Assert.Equal("wer-radar-pre-leak-64", candidates[0].Source);
    }

    /// <summary>
    /// Not every renderer emits named Data elements — without the provider manifest installed, WER 1001
    /// renders positionally, and a parser that only reads Data/@Name would see nothing on exactly the machine
    /// that most needs the answer.
    /// </summary>
    [Fact]
    public void PositionallyRenderedWerEventStillNamesTheProcess()
    {
        const string xml = """
        <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
          <System>
            <Provider Name="Windows Error Reporting" />
            <EventID Qualifiers="0">1001</EventID>
            <TimeCreated SystemTime="2026-07-14T13:59:02.0000000Z" />
          </System>
          <EventData>
            <Data>1234567890</Data>
            <Data>5</Data>
            <Data>RADAR_PRE_LEAK_64</Data>
            <Data>Not available</Data>
            <Data>0</Data>
            <Data>dwm.exe</Data>
            <Data>10.0.26100.8521</Data>
          </EventData>
        </Event>
        """;

        var candidate = Assert.Single(RadarLeakEventParser.Parse(xml, Now));
        Assert.Equal("dwm.exe", candidate.ProcessName);
        Assert.Equal("wer-radar-pre-leak-64", candidate.Source);
    }

    [Fact]
    public void ResolverEventReportingAFullImagePathIsReducedToTheProcessName()
    {
        const string xml = """
        <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
          <System>
            <Provider Name="Microsoft-Windows-Resource-Exhaustion-Resolver" />
            <EventID>1014</EventID>
            <TimeCreated SystemTime="2026-07-14T13:58:00.0000000Z" />
          </System>
          <EventData>
            <Data Name="ProcessImageName">C:\Windows\System32\dwm.exe</Data>
            <Data Name="ProcessId">22608</Data>
          </EventData>
        </Event>
        """;

        var candidate = Assert.Single(RadarLeakEventParser.Parse(xml, Now));
        Assert.Equal("dwm.exe", candidate.ProcessName);
        Assert.Null(candidate.ProcessUptime);
    }

    [Fact]
    public void DetectorEventNamesTheTopCommitters()
    {
        const string xml = """
        <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
          <System>
            <Provider Name="Microsoft-Windows-Resource-Exhaustion-Detector" />
            <EventID>2004</EventID>
            <TimeCreated SystemTime="2026-07-14T13:57:00.0000000Z" />
          </System>
          <EventData>
            <Data Name="Process1">dwm.exe</Data>
            <Data Name="Commit1">4096</Data>
            <Data Name="Process2">chrome.exe</Data>
            <Data Name="Commit2">2048</Data>
          </EventData>
        </Event>
        """;

        var candidates = RadarLeakEventParser.Parse(xml, Now);

        Assert.Equal(2, candidates.Count);
        Assert.All(candidates, candidate => Assert.Equal("resource-exhaustion-detector-2004", candidate.Source));
        Assert.Contains(candidates, candidate => candidate.ProcessName == "dwm.exe");
        Assert.Contains(candidates, candidate => candidate.ProcessName == "chrome.exe");
    }

    [Fact]
    public void UnrelatedEventIsIgnored()
    {
        const string xml = """
        <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
          <System>
            <Provider Name="Disk" />
            <EventID>153</EventID>
            <TimeCreated SystemTime="2026-07-14T13:00:00.0000000Z" />
          </System>
        </Event>
        """;

        Assert.Empty(RadarLeakEventParser.Parse(xml, Now));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<Event><System><EventID>1001</EventID>")]
    [InlineData("not xml at all")]
    [InlineData("<Event xmlns=\"http://schemas.microsoft.com/win/2004/08/events/event\"><System /></Event>")]
    public void MalformedOrEmptyXmlYieldsNoCandidatesAndDoesNotThrow(string xml)
    {
        Assert.Empty(RadarLeakEventParser.Parse(xml, Now));
    }
}

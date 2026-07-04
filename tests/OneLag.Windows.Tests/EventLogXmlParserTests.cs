using OneLag.Windows;

namespace OneLag.Windows.Tests;

public sealed class EventLogXmlParserTests
{
    [Fact]
    public void ParseGroupsEventsByProviderEventAndLevel()
    {
        const string xml = """
        <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
          <System>
            <Provider Name="Disk" />
            <EventID>153</EventID>
            <Level>3</Level>
            <TimeCreated SystemTime="2026-07-04T15:00:00.0000000Z" />
          </System>
        </Event>
        <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
          <System>
            <Provider Name="Disk" />
            <EventID>153</EventID>
            <Level>3</Level>
            <TimeCreated SystemTime="2026-07-04T15:05:00.0000000Z" />
          </System>
        </Event>
        <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
          <System>
            <Provider Name="Application Hang" />
            <EventID>1002</EventID>
            <Level>2</Level>
            <TimeCreated SystemTime="2026-07-04T15:03:00.0000000Z" />
          </System>
        </Event>
        """;

        var summaries = EventLogXmlParser.Parse("System", xml);

        var disk = Assert.Single(summaries, summary => summary.Provider == "Disk");
        Assert.Equal(153, disk.EventId);
        Assert.Equal("Warning", disk.Level);
        Assert.Equal(2, disk.Count);
        Assert.Equal(DateTimeOffset.Parse("2026-07-04T15:05:00.0000000Z"), disk.NewestTimestamp);

        var applicationHang = Assert.Single(summaries, summary => summary.Provider == "Application Hang");
        Assert.Equal(1002, applicationHang.EventId);
        Assert.Equal("Error", applicationHang.Level);
    }

    [Fact]
    public void ParseInvalidXmlReturnsEmptySummaries()
    {
        var summaries = EventLogXmlParser.Parse("System", "<Event>");

        Assert.Empty(summaries);
    }
}

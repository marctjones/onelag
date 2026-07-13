using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using OneLag.Core;

namespace OneLag.Windows;

internal static class EventLogXmlParser
{
    public static IReadOnlyList<EventLogSummary> Parse(string logName, string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return Array.Empty<EventLogSummary>();
        }

        XDocument document;
        try
        {
            document = XDocument.Parse($"<Events>{xml}</Events>", LoadOptions.None);
        }
        catch (XmlException)
        {
            return Array.Empty<EventLogSummary>();
        }

        return document
            .Root?
            .Elements()
            .Where(element => element.Name.LocalName == "Event")
            .Select(element => TryParseEvent(logName, element))
            .Where(summary => summary is not null)
            .Cast<EventLogSummary>()
            .GroupBy(summary => (summary.LogName, summary.Provider, summary.EventId, summary.Level))
            .Select(group => new EventLogSummary(
                group.Key.LogName,
                group.Key.Provider,
                group.Key.EventId,
                group.Key.Level,
                group.Sum(summary => summary.Count),
                group.Select(summary => summary.NewestTimestamp).Max()))
            .OrderByDescending(summary => summary.NewestTimestamp)
            .ThenByDescending(summary => summary.Count)
            .ThenBy(summary => summary.Provider, StringComparer.OrdinalIgnoreCase)
            .ToArray()
            ?? Array.Empty<EventLogSummary>();
    }

    private static EventLogSummary? TryParseEvent(string logName, XElement eventElement)
    {
        var system = Child(eventElement, "System");
        if (system is null)
        {
            return null;
        }

        // Manifest-based providers carry a Name. Classic and some kernel providers render only a Guid, and a
        // provider reported as "unknown" is invisible to the display, storage, and Bluetooth event matching
        // that the hypotheses depend on.
        var providerElement = Child(system, "Provider");
        var provider = providerElement?.Attribute("Name")?.Value;
        if (string.IsNullOrWhiteSpace(provider))
        {
            provider = providerElement?.Attribute("EventSourceName")?.Value;
        }

        if (string.IsNullOrWhiteSpace(provider))
        {
            provider = providerElement?.Attribute("Guid")?.Value;
        }

        var eventIdText = Child(system, "EventID")?.Value;
        if (!int.TryParse(eventIdText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var eventId))
        {
            eventId = 0;
        }

        var levelText = Child(system, "Level")?.Value;
        var level = int.TryParse(levelText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var levelNumber)
            ? LevelName(levelNumber)
            : "Unknown";

        var timeText = Child(system, "TimeCreated")?.Attribute("SystemTime")?.Value;
        DateTimeOffset? timestamp = null;
        if (DateTimeOffset.TryParse(timeText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedTimestamp))
        {
            timestamp = parsedTimestamp.ToUniversalTime();
        }

        return new EventLogSummary(
            logName,
            string.IsNullOrWhiteSpace(provider) ? "unknown" : provider,
            eventId,
            level,
            1,
            timestamp);
    }

    private static XElement? Child(XElement element, string localName)
    {
        return element.Elements().FirstOrDefault(child => child.Name.LocalName == localName);
    }

    private static string LevelName(int level) => level switch
    {
        1 => "Critical",
        2 => "Error",
        3 => "Warning",
        4 => "Information",
        5 => "Verbose",
        _ => $"Level{level}"
    };
}

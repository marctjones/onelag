using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using OneLag.Core;

namespace OneLag.Windows;

/// <summary>
/// Extracts the processes Windows itself has already accused of leaking.
///
/// Windows ships its own leak detector, RADAR, and when it fires it names the process — which is strictly
/// better evidence than any heuristic OneLag can compute from a single sample, because RADAR has watched the
/// private-byte trend since boot and OneLag has not. On the capture that motivated this probe, RADAR had
/// named dwm.exe 58 seconds before the scan ran and OneLag never looked, so the strongest available evidence
/// was thrown away.
///
/// The parsing is kept pure — XML in, candidates out — so it is testable off Windows, matching
/// <see cref="EventLogXmlParser"/>. The probe is responsible for getting the XML out of the event log.
/// </summary>
internal static class RadarLeakEventParser
{
    public const string WerRadarSource = "wer-radar-pre-leak-64";
    public const string ResolverSource = "resource-exhaustion-resolver-1014";
    public const string DetectorSource = "resource-exhaustion-detector-2004";

    private const int WerEventId = 1001;
    private const int ResolverEventId = 1014;
    private const int DetectorEventId = 2004;

    /// <summary>
    /// The WER bucket that means "this process's private bytes are climbing without bound". WER 1001 is the
    /// generic "problem reported" event and also covers ordinary app crashes, so the bucket is what
    /// separates a leak report from an APPCRASH — matching on the event ID alone would report every crashing
    /// application as a memory leak.
    /// </summary>
    private const string RadarBucketSignature = "RADAR_PRE_LEAK";

    public static IReadOnlyList<MemoryLeakCandidate> Parse(string xml, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return Array.Empty<MemoryLeakCandidate>();
        }

        XDocument document;
        try
        {
            document = XDocument.Parse($"<Events>{xml}</Events>", LoadOptions.None);
        }
        catch (XmlException)
        {
            return Array.Empty<MemoryLeakCandidate>();
        }

        var candidates = document
            .Root?
            .Elements()
            .Where(element => element.Name.LocalName == "Event")
            .SelectMany(element => TryParseEvent(element, now))
            .ToArray() ?? Array.Empty<MemoryLeakCandidate>();

        // The same leak is often reported repeatedly as the process keeps growing. One row per process per
        // source, holding the most recent sighting, keeps the report readable without losing a distinct
        // accusation from a distinct detector.
        return candidates
            .GroupBy(candidate => (candidate.ProcessName, candidate.Source), StringTupleComparer.Instance)
            .Select(group => group.OrderByDescending(candidate => candidate.ObservedAt).First())
            .OrderByDescending(candidate => candidate.ObservedAt)
            .ThenBy(candidate => candidate.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<MemoryLeakCandidate> TryParseEvent(XElement eventElement, DateTimeOffset now)
    {
        var system = Child(eventElement, "System");
        if (system is null)
        {
            return Array.Empty<MemoryLeakCandidate>();
        }

        var eventId = ParseInt(Child(system, "EventID")?.Value);
        var observedAt = ParseTimestamp(Child(system, "TimeCreated")?.Attribute("SystemTime")?.Value) ?? now;
        var data = ReadEventData(eventElement);

        return eventId switch
        {
            WerEventId => Single(TryParseWerRadar(data, observedAt)),
            ResolverEventId => Single(TryParseResolver(data, observedAt, now)),
            DetectorEventId => TryParseDetector(data, observedAt),
            _ => Array.Empty<MemoryLeakCandidate>()
        };
    }

    /// <summary>
    /// WER 1001. The renderer emits the problem signatures as either named Data elements (P1, P2, ...) or as
    /// bare positional ones depending on the provider manifest available on the machine, so the bucket is
    /// found by scanning every value rather than by trusting one field name, and the process image is read
    /// from P1 with a fall back to the first value that looks like an image name.
    /// </summary>
    private static MemoryLeakCandidate? TryParseWerRadar(IReadOnlyList<EventDatum> data, DateTimeOffset observedAt)
    {
        var isRadar = data.Any(datum =>
            datum.Value.Contains(RadarBucketSignature, StringComparison.OrdinalIgnoreCase));
        if (!isRadar)
        {
            return null;
        }

        var processName = Named(data, "P1") ?? data
            .Select(datum => datum.Value)
            .FirstOrDefault(value => value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

        processName = NormalizeProcessName(processName);
        if (processName is null)
        {
            return null;
        }

        // WER does not report the process id or its start time, only the image that was reported.
        return new MemoryLeakCandidate(processName, null, observedAt, null, WerRadarSource);
    }

    /// <summary>
    /// Resource-Exhaustion-Resolver 1014, which names the process Windows terminated or would terminate, and
    /// carries its creation time. Process uptime is the point of this event: commit that has climbed steadily
    /// across days of process uptime is a leak, whereas the same commit on a minutes-old process is a
    /// workload.
    /// </summary>
    private static MemoryLeakCandidate? TryParseResolver(
        IReadOnlyList<EventDatum> data,
        DateTimeOffset observedAt,
        DateTimeOffset now)
    {
        var processName = NormalizeProcessName(Named(data, "ProcessImageName"));
        if (processName is null)
        {
            return null;
        }

        var processId = ParseNullableInt(Named(data, "ProcessId"));
        var createdAt = ParseProcessCreationTime(Named(data, "ProcessCreationTime"));
        TimeSpan? uptime = null;
        if (createdAt.HasValue)
        {
            var elapsed = now - createdAt.Value;
            uptime = elapsed > TimeSpan.Zero ? elapsed : TimeSpan.Zero;
        }

        return new MemoryLeakCandidate(processName, processId, observedAt, uptime, ResolverSource);
    }

    /// <summary>
    /// Resource-Exhaustion-Detector 2004 ("Windows successfully diagnosed a low virtual memory condition"),
    /// which lists the top committers rather than a single culprit, so it yields one candidate per named
    /// process.
    /// </summary>
    private static IReadOnlyList<MemoryLeakCandidate> TryParseDetector(
        IReadOnlyList<EventDatum> data,
        DateTimeOffset observedAt)
    {
        return data
            .Where(datum => datum.Name is not null
                && datum.Name.StartsWith("Process", StringComparison.OrdinalIgnoreCase)
                && !datum.Name.Equals("ProcessId", StringComparison.OrdinalIgnoreCase))
            .Select(datum => NormalizeProcessName(datum.Value))
            .Where(name => name is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => new MemoryLeakCandidate(name, null, observedAt, null, DetectorSource))
            .ToArray();
    }

    private static IReadOnlyList<MemoryLeakCandidate> Single(MemoryLeakCandidate? candidate)
    {
        return candidate is null
            ? Array.Empty<MemoryLeakCandidate>()
            : new[] { candidate };
    }

    /// <summary>
    /// Some providers report the full image path and some report the bare file name; the hypotheses match on
    /// the file name, so a path would silently fail to correlate with the process list.
    /// </summary>
    private static string? NormalizeProcessName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        var separator = trimmed.LastIndexOfAny(new[] { '\\', '/' });
        if (separator >= 0 && separator < trimmed.Length - 1)
        {
            trimmed = trimmed[(separator + 1)..];
        }

        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static IReadOnlyList<EventDatum> ReadEventData(XElement eventElement)
    {
        var containers = eventElement
            .Elements()
            .Where(element => element.Name.LocalName is "EventData" or "UserData");

        var data = new List<EventDatum>();
        foreach (var container in containers)
        {
            // UserData wraps the payload in a provider-specific element, so the Data elements are read at any
            // depth rather than only as direct children.
            foreach (var element in container.Descendants())
            {
                if (element.HasElements)
                {
                    continue;
                }

                var name = element.Attribute("Name")?.Value;
                if (name is null && element.Name.LocalName != "Data")
                {
                    // A UserData payload names its fields with the element name itself.
                    name = element.Name.LocalName;
                }

                data.Add(new EventDatum(name, element.Value.Trim()));
            }
        }

        return data;
    }

    private static string? Named(IReadOnlyList<EventDatum> data, string name)
    {
        var value = data.FirstOrDefault(datum =>
            string.Equals(datum.Name, name, StringComparison.OrdinalIgnoreCase))?.Value;
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>
    /// The creation time renders as an ISO timestamp on modern builds but as a raw FILETIME on some, and a
    /// FILETIME misread as a year would put the process uptime in the tens of thousands of days.
    /// </summary>
    private static DateTimeOffset? ParseProcessCreationTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var timestamp = ParseTimestamp(value);
        if (timestamp.HasValue)
        {
            return timestamp;
        }

        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fileTime)
            || fileTime <= 0)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromFileTime(fileTime);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static DateTimeOffset? ParseTimestamp(string? value)
    {
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed.ToUniversalTime()
            : null;
    }

    private static int ParseInt(string? value)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }

    private static int? ParseNullableInt(string? value)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static XElement? Child(XElement element, string localName)
    {
        return element.Elements().FirstOrDefault(child => child.Name.LocalName == localName);
    }

    private sealed record EventDatum(string? Name, string Value);

    private sealed class StringTupleComparer : IEqualityComparer<(string ProcessName, string Source)>
    {
        public static readonly StringTupleComparer Instance = new();

        public bool Equals((string ProcessName, string Source) x, (string ProcessName, string Source) y)
        {
            return StringComparer.OrdinalIgnoreCase.Equals(x.ProcessName, y.ProcessName)
                && StringComparer.OrdinalIgnoreCase.Equals(x.Source, y.Source);
        }

        public int GetHashCode((string ProcessName, string Source) obj)
        {
            return HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.ProcessName),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Source));
        }
    }
}

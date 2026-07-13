using System.Globalization;

namespace OneLag.Windows.Tests;

/// <summary>
/// A model of how Windows actually renders events.
///
/// Windows exposes events as XML against the published Event schema
/// (http://schemas.microsoft.com/win/2004/08/events/event), which makes the format faithfully reproducible
/// without a Windows machine. What is worth modelling is not the happy path but the variation that real
/// providers emit and that quietly breaks parsers:
///
/// - Manifest-based providers carry Provider/@Name; classic providers carry only Provider/@Guid, or a
///   Provider/@EventSourceName with no Name at all.
/// - EventID often carries a Qualifiers attribute, and the element value is still the ID.
/// - Level is a number, not a word: 1=Critical, 2=Error, 3=Warning, 4=Information, 5=Verbose.
/// - Some classic records omit Level entirely, and some omit TimeCreated.
///
/// The providers modelled here are the ones this diagnosis actually turns on: display driver resets, disk
/// I/O retries, Bluetooth transport errors, thermal and processor-power events, and Defender scans.
/// </summary>
internal static class WindowsEventFixtures
{
    public const int Critical = 1;
    public const int Error = 2;
    public const int Warning = 3;
    public const int Information = 4;

    /// <summary>
    /// "Display driver nvlddmkm stopped responding and has successfully recovered." The single most
    /// diagnostic event for lag that only appears with external displays attached.
    /// </summary>
    public static string DisplayDriverReset(DateTimeOffset when, string driver = "nvlddmkm")
    {
        return Manifest("Display", 4101, Warning, when, $"<Data Name=\"DriverName\">{driver}</Data>");
    }

    /// <summary>
    /// "The IO operation at logical block address ... was retried." Storage stalling under load.
    /// </summary>
    public static string DiskIoRetried(DateTimeOffset when)
    {
        // The disk provider is classic: it renders an EventSourceName alongside the Guid, and an EventID
        // carrying a Qualifiers attribute whose element value is still the real event ID.
        const string guid = "{1cbfaf0d-e21e-4f0a-b0f6-0b6cd18b64ee}";

        return $"""
        <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
          <System>
            <Provider Name="disk" Guid="{guid}" EventSourceName="disk" />
            <EventID Qualifiers="32772">153</EventID>
            <Level>{Warning}</Level>
            <TimeCreated SystemTime="{Stamp(when)}" />
            <Channel>System</Channel>
          </System>
        </Event>
        """;
    }

    /// <summary>Bluetooth transport failure, which shows up when a BT radio is misbehaving.</summary>
    public static string BluetoothTransportError(DateTimeOffset when)
    {
        return Manifest("BTHUSB", 17, Error, when);
    }

    /// <summary>Unexpected shutdown / power fault.</summary>
    public static string KernelPower(DateTimeOffset when)
    {
        return Manifest("Microsoft-Windows-Kernel-Power", 41, Critical, when);
    }

    /// <summary>A corrected hardware error. Points at firmware, thermals, or a failing component.</summary>
    public static string WheaCorrectedError(DateTimeOffset when)
    {
        return Manifest("Microsoft-Windows-WHEA-Logger", 47, Warning, when);
    }

    public static string DefenderScanStarted(DateTimeOffset when)
    {
        return Manifest("Microsoft-Windows-Windows Defender", 1000, Information, when);
    }

    /// <summary>
    /// A classic provider that renders only a GUID: no Name, no EventSourceName. Parsers that read
    /// Provider/@Name alone lose the provider entirely and the event stops matching any hypothesis.
    /// </summary>
    public static string GuidOnlyProvider(DateTimeOffset when, int eventId = 219)
    {
        const string guid = "{9c205a39-1250-487d-abd7-e831c6290539}";

        return $"""
        <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
          <System>
            <Provider Guid="{guid}" />
            <EventID>{eventId}</EventID>
            <Level>{Warning}</Level>
            <TimeCreated SystemTime="{Stamp(when)}" />
          </System>
        </Event>
        """;
    }

    /// <summary>A record with no Level and no TimeCreated, which older sources still produce.</summary>
    public static string MissingLevelAndTime(string provider = "Application Error", int eventId = 1000)
    {
        return $"""
        <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
          <System>
            <Provider Name="{provider}" />
            <EventID>{eventId}</EventID>
          </System>
        </Event>
        """;
    }

    public static string Malformed() => "<Event><System><Provider Name=\"broken\"";

    private static string Manifest(string provider, int eventId, int level, DateTimeOffset when, string? data = null)
    {
        var eventData = data is null
            ? string.Empty
            : $"\n  <EventData>{data}</EventData>";

        return $"""
        <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
          <System>
            <Provider Name="{provider}" />
            <EventID>{eventId}</EventID>
            <Level>{level}</Level>
            <TimeCreated SystemTime="{Stamp(when)}" />
            <Channel>System</Channel>
            <Computer>test-laptop</Computer>
          </System>{eventData}
        </Event>
        """;
    }

    private static string Stamp(DateTimeOffset when)
    {
        return when.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);
    }
}

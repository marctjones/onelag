using System.Globalization;
using OneLag.Core;

namespace OneLag.Windows;

/// <summary>
/// Turns the text table printed by <c>fltmc filters</c> into the file-system filter stack.
///
/// This is kept pure — text in, models out — so it is testable off Windows, matching
/// <see cref="RadarLeakEventParser"/> and <see cref="EventLogXmlParser"/>. The probe that actually runs
/// <c>fltmc</c> is a thin, unavoidably Windows-only shell, and every decision that can be made from the text
/// alone belongs here instead, where a macOS dev machine can prove it.
///
/// The column layout is not fixed width in any way this parser can rely on: the real tool right-aligns
/// numeric columns against a header whose own width varies with locale and console width, so this splits on
/// runs of whitespace instead of column position. Altitude is a decimal (load-order ties are broken by a
/// vendor-assigned fractional suffix such as <c>385350.5</c>), and any row this parser cannot make sense of is
/// dropped rather than allowed to throw or to fabricate a filter that was never printed.
/// </summary>
internal static class FltmcOutputParser
{
    public const string SuccessEvidenceState = "windows-fltmc-filter-enumeration";

    private static readonly char[] LineSeparators = { '\r', '\n' };

    /// <summary>
    /// Filter names that are part of Windows itself, in-box or first-party, rather than a third-party
    /// product. <c>ProcMon24</c> (Sysinternals' Process Monitor filter) is deliberately not on this list:
    /// Sysinternals ships under the Microsoft umbrella but is an optional third-party-style tool a user
    /// installed, not a component of the operating system, and treating it as in-box would hide it from the
    /// third-party filter count exactly when it is the thing worth noticing.
    /// </summary>
    private static readonly HashSet<string> MicrosoftFilterNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "WdFilter",
        "FileInfo",
        "luafv",
        "wcifs",
        "bindflt",
        "CldFlt",
        "storqosflt",
        "UCPD",
        "Wof",
        "bfs",
        "CimFS",
        "FileCrypt",
        "npsvctrig",
        "MsSecFlt"
    };

    /// <summary>
    /// Filter name (or a fragment of it) to the third-party security vendor that ships it. Matching is
    /// substring-based against a lower-cased filter name because vendors version their driver names (for
    /// example <c>CiscoAMPCEFWDriver</c>, <c>CiscoAMPHeurDriver</c>, <c>CiscoSAM</c> all belong to Cisco AMP)
    /// and a new build number should not silently fall out of classification.
    /// </summary>
    private static readonly (string Fragment, string Vendor)[] SecurityVendorFragments =
    {
        ("ciscoamp", "Cisco"),
        ("ciscosam", "Cisco"),
        ("immunet", "Cisco"),
        ("trufos", "Cisco"),
        ("csagent", "CrowdStrike"),
        ("sentinelone", "SentinelOne"),
        ("sentinel", "SentinelOne"),
        ("carbonblack", "Carbon Black"),
        ("cbstream", "Carbon Black"),
        ("cbk", "Carbon Black"),
        ("sophos", "Sophos"),
        ("savonaccess", "Sophos"),
        ("mcafee", "McAfee"),
        ("mfe", "McAfee"),
        ("symantec", "Symantec"),
        ("sysplant", "Symantec"),
        ("norton", "Norton"),
        ("srtsp", "Norton"),
        ("trendmicro", "Trend Micro"),
        ("tmpreflt", "Trend Micro"),
        ("tmactmon", "Trend Micro"),
        ("tmevtmgr", "Trend Micro"),
        ("eset", "ESET"),
        ("epfw", "ESET"),
        ("efsw", "ESET"),
        ("bitdefender", "Bitdefender"),
        ("bdfilespy", "Bitdefender"),
        ("gzflt", "Bitdefender"),
        ("kaspersky", "Kaspersky"),
        ("klif", "Kaspersky"),
        ("kltdi", "Kaspersky"),
        ("cyoptics", "Palo Alto Cortex"),
        ("cyprotectdrv", "Palo Alto Cortex"),
        ("cyveraf", "Palo Alto Cortex"),
        ("tanium", "Tanium"),
        ("netskope", "Netskope"),
        ("stagent", "Netskope"),
        ("zscaler", "Zscaler"),
        ("forcepoint", "Forcepoint"),
        ("websense", "Forcepoint"),
        ("digitalguardian", "Digital Guardian"),
        ("dgfilter", "Digital Guardian")
    };

    public static IReadOnlyList<FilterDriverInfo> ParseFilters(string? fltmcOutput)
    {
        if (string.IsNullOrWhiteSpace(fltmcOutput))
        {
            return Array.Empty<FilterDriverInfo>();
        }

        var filters = new List<FilterDriverInfo>();
        foreach (var rawLine in fltmcOutput.Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            var filter = TryParseLine(rawLine);
            if (filter is not null)
            {
                filters.Add(filter);
            }
        }

        return filters;
    }

    public static FilterDriverStack BuildStack(IReadOnlyList<FilterDriverInfo> filters, DateTimeOffset timestamp, string evidenceState)
    {
        filters ??= Array.Empty<FilterDriverInfo>();

        var securityVendors = filters
            .Where(filter => !filter.IsMicrosoft && filter.Vendor is not null)
            .Select(filter => filter.Vendor!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(vendor => vendor, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var defenderFilter = filters.FirstOrDefault(filter => string.Equals(filter.Name, "WdFilter", StringComparison.OrdinalIgnoreCase));
        var cloudFilesFilter = filters.FirstOrDefault(filter => string.Equals(filter.Name, "CldFlt", StringComparison.OrdinalIgnoreCase));

        return new FilterDriverStack(
            timestamp,
            filters,
            filters.Count,
            filters.Count(filter => !filter.IsMicrosoft),
            securityVendors,
            // WdFilter present with zero instances is Defender's real-time filter sitting passive — the
            // expected state once a third-party AV has registered as the active provider, not a defect. It
            // must read as "not currently running", not as "not installed".
            defenderFilter is not null ? defenderFilter.Instances > 0 : null,
            cloudFilesFilter is not null ? cloudFilesFilter.Instances > 0 : null,
            evidenceState);
    }

    private static FilterDriverInfo? TryParseLine(string rawLine)
    {
        var line = rawLine.Trim();
        if (line.Length == 0)
        {
            return null;
        }

        // Header row and the dashed separator beneath it.
        if (line.StartsWith("Filter Name", StringComparison.OrdinalIgnoreCase)
            || line.All(c => c is '-' or ' '))
        {
            return null;
        }

        var columns = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (columns.Length == 0)
        {
            return null;
        }

        var name = columns[0];

        // A real data row always has at least a name and an instance count, and the instance count always
        // parses cleanly as a non-negative integer. Command-error text ("Access is denied.", "An instance of
        // the filter manager is not started.") reliably fails this — its second token is a word, not a
        // number — so it is rejected as a row here rather than fabricating a filter with zero instances.
        if (columns.Length < 2 || !TryParseInt(columns[1], out var instances) || !IsPlausibleFilterName(name))
        {
            return null;
        }

        double? altitude = columns.Length > 2 ? ParseAltitude(columns[2]) : null;

        var vendor = ClassifyVendor(name);
        var isMicrosoft = MicrosoftFilterNames.Contains(name);

        return new FilterDriverInfo(name, altitude, instances, vendor, IsFileSystemFilter: true, isMicrosoft);
    }

    /// <summary>
    /// <c>fltmc</c> reports command failures (missing elevation, no filter manager, etc.) as plain sentences
    /// rather than a table row, so a first token that is not a bare identifier — because it contains spaces,
    /// punctuation typical of prose, or is implausibly long — is rejected instead of being read as a filter
    /// name.
    /// </summary>
    private static bool IsPlausibleFilterName(string candidate)
    {
        if (candidate.Length is 0 or > 64)
        {
            return false;
        }

        return candidate.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.');
    }

    private static bool TryParseInt(string value, out int parsed)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) && parsed >= 0;
    }

    private static double? ParseAltitude(string value)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    /// <summary>
    /// Returns the security vendor for a recognised third-party filter, or null otherwise. A null vendor does
    /// not mean "Microsoft" — <see cref="TryParseLine"/> derives <c>IsMicrosoft</c> separately from
    /// <see cref="MicrosoftFilterNames"/> — it means this parser recognises the filter as third-party (for
    /// example <c>npcap</c>, <c>UnionFS</c>, <c>AATFilter</c>, or any name absent from both lists) but will not
    /// guess at who ships it. A wrong guessed vendor is worse than an honest null.
    /// </summary>
    private static string? ClassifyVendor(string name)
    {
        var lowered = name.ToLowerInvariant();
        foreach (var (fragment, vendor) in SecurityVendorFragments)
        {
            if (lowered.Contains(fragment, StringComparison.Ordinal))
            {
                return vendor;
            }
        }

        return null;
    }
}

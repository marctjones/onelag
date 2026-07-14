using OneLag.Core;

namespace OneLag.Windows;

/// <summary>
/// Turns the text table printed by <c>net use</c> into mapped-drive records.
///
/// Kept pure — text in, models out — so it is testable off Windows, matching the split already used for
/// <c>fltmc filters</c> (<see cref="FltmcOutputParser"/>). The shell that actually runs <c>net use</c>
/// (<see cref="WindowsFileSystemContextProbe"/>) is deliberately the thinnest possible wrapper.
///
/// <c>net use</c>'s columns are ragged and locale-dependent, so this splits on runs of whitespace rather than
/// column position, the same approach <see cref="FltmcOutputParser"/> uses. Rows without a local drive letter
/// (a bare UNC path in the "Local" column, which <c>net use</c> prints for printer connections) are dropped:
/// they cannot be a native open-file dialog's default folder, so they carry no diagnostic value here and
/// <see cref="MappedDrive.Letter"/> has nowhere non-fabricated to put them.
/// </summary>
internal static class NetUseOutputParser
{
    public const string SuccessEvidenceState = "windows-net-use-enumeration";

    private static readonly char[] LineSeparators = { '\r', '\n' };

    /// <summary>
    /// The status words <c>net use</c> actually prints. Anything else on the first column — the "Status"
    /// header, the dashed separator, "New connections will be remembered.", "The command completed
    /// successfully." — is prose, not a data row, and is rejected here rather than mis-parsed as a drive.
    /// </summary>
    private static readonly HashSet<string> KnownStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "OK",
        "Disconnected",
        "Unavailable",
        "Connected",
        "Connecting",
        "Reconnecting",
        "Dormant",
        "Paused",
        "Error",
        "Open"
    };

    public static IReadOnlyList<MappedDrive> ParseDrives(string? netUseOutput)
    {
        if (string.IsNullOrWhiteSpace(netUseOutput))
        {
            return Array.Empty<MappedDrive>();
        }

        var drives = new List<MappedDrive>();
        foreach (var rawLine in netUseOutput.Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            var drive = TryParseLine(rawLine.Trim());
            if (drive is not null)
            {
                drives.Add(drive);
            }
        }

        return drives;
    }

    private static MappedDrive? TryParseLine(string line)
    {
        if (line.Length == 0)
        {
            return null;
        }

        var columns = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (columns.Length < 3 || !KnownStatuses.Contains(columns[0]))
        {
            return null;
        }

        if (!IsDriveLetterToken(columns[1]))
        {
            return null;
        }

        var letter = char.ToUpperInvariant(columns[1][0]) + ":";
        var remote = columns.Skip(2).FirstOrDefault(column => column.StartsWith(@"\\", StringComparison.Ordinal));
        if (remote is null)
        {
            return null;
        }

        // Reachability is deliberately not derived from this status word: "OK" here only means the connection
        // was established at mount time, not that the remote host answers now, and a truly dead UNC path is
        // exactly the case that must be re-checked live, with a bounded timeout, by the probe.
        return new MappedDrive(letter, remote, columns[0], null);
    }

    private static bool IsDriveLetterToken(string token)
    {
        return token.Length == 2 && token[1] == ':' && char.IsAsciiLetter(token[0]);
    }
}

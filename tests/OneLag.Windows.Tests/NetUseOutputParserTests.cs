using OneLag.Windows;

namespace OneLag.Windows.Tests;

/// <summary>
/// These run everywhere, which is the whole reason the <c>net use</c> output parsing is separated from the
/// shell-out in <see cref="WindowsFileSystemContextProbe"/>. The fixture below is a realistic reproduction of
/// what <c>net use</c> prints: a preamble line, a ragged header, an <c>OK</c> connection, a live-but-currently
/// -disconnected one, and — the case this parser exists to catch — a genuinely dead share reported as
/// <c>Unavailable</c>, which is exactly the kind of mapped drive that can hang a native open-file dialog for
/// 30+ seconds.
/// </summary>
public sealed class NetUseOutputParserTests
{
    private const string RealMachineFixture = """
    New connections will be remembered.


    Status       Local     Remote                          Network

    -------------------------------------------------------------------------
    OK           Z:        \\fileserver\shared               Microsoft Windows Network
    Disconnected X:        \\oldserver\backup                Microsoft Windows Network
    Unavailable  Y:        \\deadserver\data                 Microsoft Windows Network
    OK                     \\printserver\officeprinter        Microsoft Windows Network
    The command completed successfully.

    """;

    [Fact]
    public void Parses_all_drive_letter_rows_from_a_realistic_transcript()
    {
        var drives = NetUseOutputParser.ParseDrives(RealMachineFixture);

        Assert.Equal(3, drives.Count);
    }

    [Fact]
    public void Reports_the_dead_share_with_its_unavailable_status_and_no_reachability_verdict_yet()
    {
        var drives = NetUseOutputParser.ParseDrives(RealMachineFixture);

        var dead = Assert.Single(drives, drive => drive.Letter == "Y:");
        Assert.Equal(@"\\deadserver\data", dead.RemotePath);
        Assert.Equal("Unavailable", dead.Status);

        // Reachability is a live, time-boxed check the probe performs separately, not something this pure
        // parser can determine from static text.
        Assert.Null(dead.Reachable);
    }

    [Fact]
    public void Reports_healthy_and_disconnected_drives_correctly()
    {
        var drives = NetUseOutputParser.ParseDrives(RealMachineFixture);

        var healthy = Assert.Single(drives, drive => drive.Letter == "Z:");
        Assert.Equal(@"\\fileserver\shared", healthy.RemotePath);
        Assert.Equal("OK", healthy.Status);

        var disconnected = Assert.Single(drives, drive => drive.Letter == "X:");
        Assert.Equal(@"\\oldserver\backup", disconnected.RemotePath);
        Assert.Equal("Disconnected", disconnected.Status);
    }

    [Fact]
    public void Rows_without_a_local_drive_letter_are_skipped()
    {
        var drives = NetUseOutputParser.ParseDrives(RealMachineFixture);

        Assert.DoesNotContain(drives, drive => drive.RemotePath == @"\\printserver\officeprinter");
    }

    [Fact]
    public void Empty_list_reports_no_entries_without_throwing()
    {
        const string noEntries = """
        New connections will be remembered.

        There are no entries in the list.

        """;

        var drives = NetUseOutputParser.ParseDrives(noEntries);

        Assert.Empty(drives);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Null_or_blank_input_never_throws(string? input)
    {
        var drives = NetUseOutputParser.ParseDrives(input);

        Assert.Empty(drives);
    }
}

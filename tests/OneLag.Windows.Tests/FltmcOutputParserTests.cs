using OneLag.Core;
using OneLag.Windows;

namespace OneLag.Windows.Tests;

/// <summary>
/// These run everywhere, which is the whole reason the filter-driver parsing is separated from the
/// <c>fltmc</c> shell-out. The fixture below reproduces the machine that motivated this probe: eleven
/// third-party Cisco AMP/Immunet/Trufos kernel filters plus npcap, UnionFS and AATFilter, with Microsoft
/// Defender's own filter present but passive (zero instances) because a third-party AV had registered as the
/// active provider. The old <c>SecurityOrSearchScanner</c> hypothesis looked only at Defender and Search CPU
/// and scored this machine zero.
/// </summary>
public sealed class FltmcOutputParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 14, 14, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// A close reproduction of the real <c>fltmc filters</c> table: a header row, a dashed separator, ragged
    /// column widths, and a mix of integer and decimal altitudes.
    /// </summary>
    private const string RealMachineFixture = """
    Filter Name                     Num Instances    Altitude    Frame
    ------------------------------  -------------  ------------  -----
    bindflt                                 1       409800         0
    WdFilter                                0       328010         0
    CiscoAMPCEFWDriver                      4       328000         0
    CiscoAMPHeurDriver                      4       325200         0
    CiscoSAM                                4       322200         0
    ImmunetProtectDriver                    4       260500         0
    ImmunetSelfProtectDriver                4       260450         0
    Trufos                                  4       260400         0
    UnionFS                                 1       249000         0
    AATFilter                               1       245801.5       0
    npcap                                   1       241000         0
    CldFlt                                  1       180451         0
    storqosflt                              0       244000         0
    FileInfo                                1       45000          0
    Wof                                     1        40700         0
    """;

    [Fact]
    public void RealMachineFixtureCountsThirdPartyFiltersAndVendors()
    {
        var filters = FltmcOutputParser.ParseFilters(RealMachineFixture);
        var stack = FltmcOutputParser.BuildStack(filters, Now, FltmcOutputParser.SuccessEvidenceState);

        Assert.Equal(15, stack.FileSystemFilterCount);

        // Cisco AMP/Immunet/Trufos (6) + UnionFS + AATFilter + npcap = 9 third-party filters.
        Assert.Equal(9, stack.ThirdPartyFileSystemFilterCount);

        Assert.Contains("Cisco", stack.SecurityVendors);

        // WdFilter is present but has zero instances: Defender's real-time filter is passive, the expected
        // state once a third-party AV has registered, not evidence Defender is uninstalled.
        Assert.False(stack.DefenderFilterRunning);

        Assert.True(stack.CloudFilesFilterRunning);

        Assert.Equal(FltmcOutputParser.SuccessEvidenceState, stack.EvidenceState);
    }

    [Fact]
    public void RealMachineFixtureClassifiesEachCiscoDriverAsCiscoAndThirdParty()
    {
        var filters = FltmcOutputParser.ParseFilters(RealMachineFixture);

        foreach (var name in new[]
                 {
                     "CiscoAMPCEFWDriver",
                     "CiscoAMPHeurDriver",
                     "CiscoSAM",
                     "ImmunetProtectDriver",
                     "ImmunetSelfProtectDriver",
                     "Trufos"
                 })
        {
            var filter = Assert.Single(filters, f => f.Name == name);
            Assert.False(filter.IsMicrosoft);
            Assert.Equal("Cisco", filter.Vendor);
        }
    }

    [Fact]
    public void UnrecognisedThirdPartyFiltersAreThirdPartyWithNullVendor()
    {
        var filters = FltmcOutputParser.ParseFilters(RealMachineFixture);

        foreach (var name in new[] { "UnionFS", "AATFilter", "npcap" })
        {
            var filter = Assert.Single(filters, f => f.Name == name);
            Assert.False(filter.IsMicrosoft);
            Assert.Null(filter.Vendor);
        }
    }

    [Fact]
    public void MicrosoftFiltersAreClassifiedAsMicrosoftWithNoVendor()
    {
        var filters = FltmcOutputParser.ParseFilters(RealMachineFixture);

        foreach (var name in new[] { "bindflt", "WdFilter", "CldFlt", "storqosflt", "FileInfo", "Wof" })
        {
            var filter = Assert.Single(filters, f => f.Name == name);
            Assert.True(filter.IsMicrosoft);
            Assert.Null(filter.Vendor);
        }
    }

    [Fact]
    public void DecimalAltitudeIsParsed()
    {
        var filters = FltmcOutputParser.ParseFilters(RealMachineFixture);

        var aatFilter = Assert.Single(filters, f => f.Name == "AATFilter");
        Assert.Equal(245801.5, aatFilter.Altitude);
    }

    [Fact]
    public void IntegerAltitudeIsParsed()
    {
        var filters = FltmcOutputParser.ParseFilters(RealMachineFixture);

        var cldFlt = Assert.Single(filters, f => f.Name == "CldFlt");
        Assert.Equal(180451, cldFlt.Altitude);
    }

    [Fact]
    public void ZeroInstanceFilterIsStillReportedWithItsCount()
    {
        var filters = FltmcOutputParser.ParseFilters(RealMachineFixture);

        var wdFilter = Assert.Single(filters, f => f.Name == "WdFilter");
        Assert.Equal(0, wdFilter.Instances);

        var storqosflt = Assert.Single(filters, f => f.Name == "storqosflt");
        Assert.Equal(0, storqosflt.Instances);
    }

    [Fact]
    public void DefenderFilterAbsentIsReportedAsNullNotFalse()
    {
        const string noDefenderFixture = """
        Filter Name                     Num Instances    Altitude    Frame
        ------------------------------  -------------  ------------  -----
        bindflt                                 1       409800         0
        """;

        var filters = FltmcOutputParser.ParseFilters(noDefenderFixture);
        var stack = FltmcOutputParser.BuildStack(filters, Now, FltmcOutputParser.SuccessEvidenceState);

        // No WdFilter row at all is a different observation from WdFilter-present-but-zero-instances, and
        // must not be collapsed into the same false.
        Assert.Null(stack.DefenderFilterRunning);
        Assert.Null(stack.CloudFilesFilterRunning);
    }

    [Fact]
    public void DefenderFilterWithInstancesIsReportedAsRunning()
    {
        const string defenderActiveFixture = """
        Filter Name                     Num Instances    Altitude    Frame
        ------------------------------  -------------  ------------  -----
        WdFilter                                1       328010         0
        """;

        var filters = FltmcOutputParser.ParseFilters(defenderActiveFixture);
        var stack = FltmcOutputParser.BuildStack(filters, Now, FltmcOutputParser.SuccessEvidenceState);

        Assert.True(stack.DefenderFilterRunning);
    }

    [Fact]
    public void HeaderOnlyOutputYieldsNoFilters()
    {
        const string headerOnly = """
        Filter Name                     Num Instances    Altitude    Frame
        ------------------------------  -------------  ------------  -----
        """;

        Assert.Empty(FltmcOutputParser.ParseFilters(headerOnly));
    }

    [Fact]
    public void EmptyOutputYieldsAnEmptyUnavailableLookingButNonThrowingResult()
    {
        var filters = FltmcOutputParser.ParseFilters(string.Empty);
        var stack = FltmcOutputParser.BuildStack(filters, Now, "windows-fltmc-filter-enumeration");

        Assert.Empty(filters);
        Assert.Equal(0, stack.FileSystemFilterCount);
        Assert.Null(stack.DefenderFilterRunning);
        Assert.Null(stack.CloudFilesFilterRunning);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\n\n")]
    [InlineData("this is not fltmc output at all, it is complete garbage {{{ }}} \0")]
    [InlineData("Access is denied.\r\n")]
    [InlineData("An instance of the filter manager is not started.")]
    [InlineData("A required privilege is not held by the client.\n")]
    public void GarbageOrErrorTextYieldsNoFiltersAndNeverThrows(string text)
    {
        var exception = Record.Exception(() => FltmcOutputParser.ParseFilters(text));

        Assert.Null(exception);
        Assert.Empty(FltmcOutputParser.ParseFilters(text));
    }

    [Fact]
    public void NullOutputYieldsNoFiltersAndNeverThrows()
    {
        var exception = Record.Exception(() => FltmcOutputParser.ParseFilters(null));

        Assert.Null(exception);
        Assert.Empty(FltmcOutputParser.ParseFilters(null));
    }

    [Fact]
    public void BuildStackToleratesAnEmptyFilterList()
    {
        var stack = FltmcOutputParser.BuildStack(Array.Empty<FilterDriverInfo>(), Now, "fltmc-requires-elevation");

        Assert.Equal(0, stack.FileSystemFilterCount);
        Assert.Equal(0, stack.ThirdPartyFileSystemFilterCount);
        Assert.Empty(stack.SecurityVendors);
        Assert.Null(stack.DefenderFilterRunning);
        Assert.Null(stack.CloudFilesFilterRunning);
        Assert.Equal("fltmc-requires-elevation", stack.EvidenceState);
    }

    [Fact]
    public void RaggedWhitespaceAndBlankLinesAreTolerated()
    {
        const string ragged = "  bindflt      1   409800   0  \n\n\n   WdFilter    0   328010   0   \n";

        var filters = FltmcOutputParser.ParseFilters(ragged);

        Assert.Equal(2, filters.Count);
        Assert.Contains(filters, f => f.Name == "bindflt" && f.Instances == 1);
        Assert.Contains(filters, f => f.Name == "WdFilter" && f.Instances == 0);
    }

    [Fact]
    public void RowMissingAltitudeAndFrameColumnsStillYieldsNameAndInstances()
    {
        const string sparse = "bindflt   1";

        var filter = Assert.Single(FltmcOutputParser.ParseFilters(sparse));

        Assert.Equal("bindflt", filter.Name);
        Assert.Equal(1, filter.Instances);
        Assert.Null(filter.Altitude);
    }

    [Fact]
    public void NonNumericAltitudeIsDroppedRatherThanThrowing()
    {
        const string badAltitude = "bindflt   1   not-a-number   0";

        var filter = Assert.Single(FltmcOutputParser.ParseFilters(badAltitude));

        Assert.Equal("bindflt", filter.Name);
        Assert.Null(filter.Altitude);
    }
}

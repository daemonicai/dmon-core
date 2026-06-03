namespace Dmon.Protocol.Tests;

public sealed class ProtocolVersionTests
{
    [Fact]
    public void Current_Is_ZeroDotTwo()
    {
        Assert.Equal("0.2", ProtocolVersion.Current);
    }

    [Fact]
    public void MajorMinor_Current_ReturnsMajorMinorSegment()
    {
        string? result = ProtocolVersion.MajorMinor(ProtocolVersion.Current);
        Assert.Equal("0.2", result);
    }

    [Fact]
    public void MajorMinor_ThreePart_ReturnsMajorMinorOnly()
    {
        string? result = ProtocolVersion.MajorMinor("1.2.3");
        Assert.Equal("1.2", result);
    }

    [Fact]
    public void MajorMinor_TwoPart_ReturnsBothParts()
    {
        string? result = ProtocolVersion.MajorMinor("2.0");
        Assert.Equal("2.0", result);
    }

    [Fact]
    public void MajorMinor_OnePart_ReturnsNull()
    {
        string? result = ProtocolVersion.MajorMinor("1");
        Assert.Null(result);
    }

    [Fact]
    public void MajorMinor_Empty_ReturnsNull()
    {
        string? result = ProtocolVersion.MajorMinor(string.Empty);
        Assert.Null(result);
    }

    [Fact]
    public void MajorMinor_NonNumericSegment_ReturnsNull()
    {
        string? result = ProtocolVersion.MajorMinor("a.b.c");
        Assert.Null(result);
    }

    [Fact]
    public void MajorMinor_CompatibilityCheck_SameMajorMinor_IsCompatible()
    {
        // Simulate the group-3 gate: compare reported protocolVersion against Current.
        // Use a patch build of the current Major.Minor so this stays valid after future bumps.
        string currentMM = ProtocolVersion.MajorMinor(ProtocolVersion.Current)!;
        string reported = $"{currentMM}.7";
        string? reportedMM = ProtocolVersion.MajorMinor(reported);

        Assert.Equal(currentMM, reportedMM);
    }

    [Fact]
    public void MajorMinor_CompatibilityCheck_DifferentMajor_IsIncompatible()
    {
        string reported = "1.0";
        string? reportedMM = ProtocolVersion.MajorMinor(reported);
        string? currentMM  = ProtocolVersion.MajorMinor(ProtocolVersion.Current);

        Assert.NotEqual(currentMM, reportedMM);
    }
}

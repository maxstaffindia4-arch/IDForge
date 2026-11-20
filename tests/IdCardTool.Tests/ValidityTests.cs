using IdCardTool.Core;
using Xunit;

namespace IdCardTool.Tests;

public class ValidityTests
{
    [Fact]
    public void Contractual_2026_11_19_Gives_May_2027()
    {
        var d = Validity.CalculateValidUntil(new DateOnly(2026, 11, 19), "Contractual");
        Assert.Equal(new DateOnly(2027, 5, 19), d);
        Assert.Equal("Valid Until May 2027", Validity.FormatValidity(d));
    }

    [Fact]
    public void Permanent_2026_11_19_Gives_November_2027()
    {
        var d = Validity.CalculateValidUntil(new DateOnly(2026, 11, 19), "Permanent");
        Assert.Equal(new DateOnly(2027, 11, 19), d);
        Assert.Equal("Valid Until November 2027", Validity.FormatValidity(d));
    }

    [Fact]
    public void Contractual_Aug31_ClampsTo_Feb()
    {
        var d = Validity.CalculateValidUntil(new DateOnly(2026, 8, 31), "Contractual");
        Assert.Equal(new DateOnly(2027, 2, 28), d);
        Assert.Equal("Valid Until February 2027", Validity.FormatValidity(d));
    }

    [Fact]
    public void Permanent_Feb29_Leap_Gives_Feb2029()
    {
        var d = Validity.CalculateValidUntil(new DateOnly(2028, 2, 29), "Permanent");
        Assert.Equal(new DateOnly(2029, 2, 28), d);
        Assert.Equal("Valid Until February 2029", Validity.FormatValidity(d));
    }

    [Fact]
    public void Permanent_Jan31_Gives_Feb28()
    {
        var d = Validity.CalculateValidUntil(new DateOnly(2026, 1, 31), "Permanent");
        Assert.Equal(new DateOnly(2027, 1, 31), d);
    }

    [Fact]
    public void IdLine_Strips_Uid_Prefix()
    {
        Assert.Equal("ID. 20250318", Validity.FormatIdLine("UID20250318"));
        Assert.Equal("ID. 20250318", Validity.FormatIdLine("uid20250318"));
        Assert.Equal("ID. ABC123", Validity.FormatIdLine("ABC123"));
    }
}

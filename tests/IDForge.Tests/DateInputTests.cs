using IDForge.Core;
using Xunit;

namespace IDForge.Tests;

/// <summary>Re-issue date entry: dd/MM/yyyy parsing and last-print-date guard.</summary>
public class DateInputTests
{
    [Theory]
    [InlineData("1/7/2027", 2027, 7, 1)]    // en-GB: 1 July 2027, not Jan 7
    [InlineData("01/07/2027", 2027, 7, 1)]
    [InlineData("15/12/2026", 2026, 12, 15)]
    [InlineData("2027-07-01", 2027, 7, 1)]  // ISO still accepted
    [InlineData("  3/3/2027  ", 2027, 3, 3)]
    public void TryParse_DdMmYyyy_Works(string input, int y, int m, int d)
    {
        Assert.True(DateInput.TryParse(input, out var date));
        Assert.Equal(new DateOnly(y, m, d), date);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a date")]
    [InlineData("32/13/2027")]
    [InlineData("1/7/27")]
    public void TryParse_Invalid_Returns_False(string input)
        => Assert.False(DateInput.TryParse(input, out _));

    [Fact]
    public void ValidateReissue_Rejects_Date_Before_Last_Print()
    {
        var last = new DateOnly(2026, 1, 15);
        var err = DateInput.ValidateReissue(new DateOnly(2026, 1, 14), last);
        Assert.NotNull(err);
        Assert.Contains("15/01/2026", err);   // dd/MM/yyyy rendering of the last date
    }

    [Fact]
    public void ValidateReissue_Allows_Same_Or_Later_Date()
    {
        var last = new DateOnly(2026, 1, 15);
        Assert.Null(DateInput.ValidateReissue(new DateOnly(2026, 1, 15), last));
        Assert.Null(DateInput.ValidateReissue(new DateOnly(2027, 7, 1), last));
        Assert.Null(DateInput.ValidateReissue(new DateOnly(2027, 7, 1), null));  // first issue
    }
}

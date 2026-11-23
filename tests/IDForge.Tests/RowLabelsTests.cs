using IDForge.Core;
using Xunit;

namespace IDForge.Tests;

/// <summary>Batch review list labels: "Name — UID<digits>".</summary>
public class RowLabelsTests
{
    [Fact]
    public void Label_Name_And_Uid()
        => Assert.Equal("Mehdi Hasan Sakib — UID20250318",
            RowLabels.For("Mehdi Hasan Sakib", "20250318"));

    [Fact]
    public void Label_No_Name()
        => Assert.Equal("(no name) — UID20250318",
            RowLabels.For("", "20250318"));

    [Fact]
    public void Label_No_Uid_And_Prefixed_Input_Normalizes()
    {
        Assert.Equal("(no name) — (no UID)", RowLabels.For(null, ""));
        Assert.Equal("Ali — UID20250318", RowLabels.For("Ali", "UID20250318"));
        Assert.Equal("Ali — UID20250318", RowLabels.For("Ali", "ID. 20250318"));
    }
}

using System.Reflection;
using System.Text.Json;
using IDForge;
using Xunit;

namespace IDForge.Tests;

/// <summary>Phase 2: settings persistence, locked re-issue confirm, area switching.</summary>
public class Phase2Tests
{
    [Fact]
    public void Settings_Persist_To_Disk()
    {
        var origPdf = AppSettings.IncludePdf;
        var origFolder = AppSettings.SaveFolder;
        var origContract = AppSettings.DefaultContract;
        try
        {
            AppSettings.IncludePdf = true;
            AppSettings.SaveFolder = @"F:\Temp\IDForge Cards";
            AppSettings.DefaultContract = "Contractual";

            var json = File.ReadAllText(IDForge.Core.AppPaths.SettingsPath);
            using var doc = JsonDocument.Parse(json);
            Assert.True(doc.RootElement.GetProperty("includePdf").GetBoolean());
            Assert.Equal(@"F:\Temp\IDForge Cards", doc.RootElement.GetProperty("saveFolder").GetString());
            Assert.Equal("Contractual", doc.RootElement.GetProperty("defaultContract").GetString());
        }
        finally
        {
            AppSettings.IncludePdf = origPdf;
            AppSettings.SaveFolder = origFolder;
            AppSettings.DefaultContract = origContract;
        }
    }

    [Fact]
    public void Reissue_Confirm_Cannot_Be_Turned_Off()
    {
        Assert.True(AppSettings.AlwaysConfirmReissue);
        // get-only: no setter exists, so no code path can disable it
        Assert.Null(typeof(AppSettings).GetProperty(nameof(AppSettings.AlwaysConfirmReissue))!
            .GetSetMethod());
    }
}

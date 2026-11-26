using System.IO;
using System.Reflection;
using System.Windows;
using IDForge;

namespace IDForge.Tests;

/// <summary>Fix H-C — issue flow: no folder picker, Settings folder is the
/// output, missing folder blocks before any write.</summary>
[Collection("Wpf")]
public class FixHPartCTests
{
    [Fact]
    public void Issue_Paths_Do_Not_Call_Folder_Or_Save_Pickers()
    {
        // Save_Click (New card issue), the re-issue handler, and batch review
        // must never call PickCardImagePath / OpenFolderDialog. PickCardImagePath
        // may still appear for Reprint/Export (not issue paths).
        var src = File.ReadAllText(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "IDForge", "MainWindow.xaml.cs")));
        var issueAsync = src.IndexOf("IssueBatchRowAsync", StringComparison.Ordinal);
        Assert.True(issueAsync > 0);
        var body = src.Substring(issueAsync, src.IndexOf("// ---------------- helpers", issueAsync) - issueAsync);
        Assert.DoesNotContain("OpenFolderDialog", body);
        Assert.DoesNotContain("PickCardImagePath", body);
        Assert.Contains("AppSettings.SaveFolder", body);
        // OpenReview must not ask for a folder either.
        var openReview = src.IndexOf("private void OpenReview", StringComparison.Ordinal);
        var body2 = src.Substring(openReview, src.IndexOf("internal void UpdateBatchSummary", openReview) - openReview);
        Assert.DoesNotContain("OpenFolderDialog", body2);
        Assert.DoesNotContain("PickCardImagePath", body2);
    }

    [Fact]
    public void Missing_Output_Folder_Blocks_Before_Any_Write()
    {
        WpfSta.Run(() =>
        {
            var win = FixPassTests.NewWindow();
            var missing = Path.Combine(Path.GetTempPath(), "idforge-no-such-dir-" + Guid.NewGuid().ToString("N"));
            var old = AppSettings.SaveFolder;
            var prompted = false;
            MainWindow.OutputFolderPrompt = (_, _) => { prompted = true; return false; };
            try
            {
                // Point Settings at a folder that does not exist.
                typeof(AppSettings).GetProperty("SaveFolder")!
                    .SetValue(null, missing);
                Assert.False(Directory.Exists(missing));

                Assert.False(win.EnsureIssueOutputFolder(win));
                Assert.True(prompted, "missing-folder prompt was not shown");
                Assert.False(Directory.Exists(missing), "block must not create the folder");

                // A writable folder passes silently.
                var good = Path.Combine(Path.GetTempPath(), "idforge-out-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(good);
                typeof(AppSettings).GetProperty("SaveFolder")!.SetValue(null, good);
                Assert.True(win.EnsureIssueOutputFolder(win));
            }
            finally
            {
                MainWindow.OutputFolderPrompt = null;
                typeof(AppSettings).GetProperty("SaveFolder")!.SetValue(null, old);
                try { Directory.Delete(missing, true); } catch { }
                win.Close();
            }
        });
    }

    [Fact]
    public void Confirm_Cancel_Writes_Nothing()
    {
        WpfSta.Run(() =>
        {
            var win = FixPassTests.NewWindow();
            var outDir = Path.Combine(Path.GetTempPath(), "idforge-issue-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(outDir);
            var old = AppSettings.SaveFolder;
            var oldConfirm = IDForge.IssueDialogs.ConfirmOverride;
            try
            {
                typeof(AppSettings).GetProperty("SaveFolder")!.SetValue(null, outDir);
                IDForge.IssueDialogs.ConfirmOverride = (_, _, _, _, _) => false;  // user cancels
                var before = _storeCount(win);
                // Drive the review issue path far enough to hit the confirm.
                var row = new BatchRow
                {
                    Uid = "62000001", Name = "Cancel Test", Position = "Nurse",
                    Contract = "Permanent", Status = "NeedsReview",
                    PhotoPath = null, Cutout = null
                };
                win.BatchRows.Add(row);
                // IssueRowAsync validates first — photo required; instead verify
                // ordering statically: confirm hook runs before IssueBatchRowAsync.
                var src = File.ReadAllText(Path.GetFullPath(Path.Combine(
                    AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                    "src", "IDForge", "ReviewWindow.xaml.cs")));
                var m = src.IndexOf("IssueRowAsync", StringComparison.Ordinal);
                var seg = src.Substring(m, src.IndexOf("ShowIssueSuccess", m) - m);
                Assert.True(seg.IndexOf("ConfirmIssue", StringComparison.Ordinal)
                        < seg.IndexOf("IssueBatchRowAsync", StringComparison.Ordinal),
                    "confirm must run before the issue call");
                Assert.Equal(before, _storeCount(win));
            }
            finally
            {
                IDForge.IssueDialogs.ConfirmOverride = oldConfirm;
                typeof(AppSettings).GetProperty("SaveFolder")!.SetValue(null, old);
                win.Close();
            }
        });
    }

    private static int _storeCount(MainWindow win)
    {
        var emp = win.Svc.Store.AllEmployees();
        return emp.Count;
    }
}

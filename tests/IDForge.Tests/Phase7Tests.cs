using System.IO;
using System.Windows;
using System.Windows.Controls;
using IDForge;
using IDForge.Dialogs;
using Xunit;

namespace IDForge.Tests;

/// <summary>Phase 7 — dialogs & message catalogue. Verifies the shared
/// AppDialog template rules (Enter=default, Esc=cancel, destructive→Cancel),
/// the typed DELETE ALL confirm, the XAML re-issue dialog, catalogue wording,
/// the success banner, and the 7+ day backup warning.</summary>
[Collection("Wpf")]
public class Phase7Tests
{
    private static IEnumerable<DependencyObject> Logical(DependencyObject root)
    {
        yield return root;
        foreach (var c in LogicalTreeHelper.GetChildren(root))
            if (c is DependencyObject d)
                foreach (var x in Logical(d)) yield return x;
    }

    private static List<Button> Buttons(Window dlg)
        => Logical(dlg).OfType<Button>().ToList();

    // ---------- catalogue wording ----------

    [Fact]
    public void Catalogue_Uses_Spec_Wording()
    {
        Assert.Equal("DELETE ALL", Messages.DeleteAllPhrase);
        Assert.Equal("Issue a card for Amara Okafor?",
            Messages.ConfirmIssueHeading("Amara Okafor"));
        Assert.Contains("The old card should be collected",
            Messages.ReissueBody(new DateTime(2025, 3, 14)));
        Assert.Equal("Delete Ben Marshall's record?",
            Messages.DeleteRecordHeading("Ben Marshall"));
        Assert.Contains("Card files in the output folder are not deleted",
            Messages.DeleteRecordBody(DateTime.Now, DateTime.Now.AddDays(30)));
        Assert.Equal("Remove every card on this PC?", Messages.ClearAllHeading);
        Assert.Contains("Type DELETE ALL to confirm", Messages.ClearAllTypePrompt);
        Assert.Contains("1,284 records and 2 batches", Messages.ClearAllBody(1284, 2));
        Assert.Contains("backup is saved first", Messages.ClearAllBody(1284, 2));
        Assert.Equal("Choose a backup to restore", Messages.RestoreBackupHeading);
        Assert.Equal("Card issued for Amara Okafor.", Messages.IssuedBanner("Amara Okafor"));
        Assert.Equal("No backup yet. Back up now?", Messages.BackupAgeWarning(0));
        Assert.Equal("Your last backup was 9 days ago. Back up now?",
            Messages.BackupAgeWarning(9));
        Assert.Equal(7, Messages.BackupAgeWarningDays);
    }

    // ---------- AppDialog template rules ----------

    [Fact]
    public void Confirm_Normal_Enter_Is_Confirm_Esc_Is_Cancel()
    {
        WpfSta.Run(() =>
        {
            WpfTestHost.EnsureApp();
            var dlg = AppDialog.BuildConfirm(null, "t", "h", "body", "Yes");
            var btns = Buttons(dlg);
            var yes = btns.Single(b => (string)b.Content! == "Yes");
            var cancel = btns.Single(b => (string)b.Content! == "Cancel");
            Assert.True(yes.IsDefault);          // Enter activates confirm
            Assert.False(yes.IsCancel);
            Assert.True(cancel.IsCancel);        // Esc activates cancel
            Assert.False(cancel.IsDefault);
            dlg.Close();
        });
    }

    [Fact]
    public void Confirm_Destructive_Enter_Cancels()
    {
        WpfSta.Run(() =>
        {
            WpfTestHost.EnsureApp();
            var dlg = AppDialog.BuildConfirm(null, "t", "h", "body", "Delete",
                destructive: true);
            var btns = Buttons(dlg);
            var del = btns.Single(b => (string)b.Content! == "Delete");
            var cancel = btns.Single(b => (string)b.Content! == "Cancel");
            Assert.True(cancel.IsDefault);       // Enter hits Cancel, not Delete
            Assert.True(cancel.IsCancel);        // Esc hits Cancel
            Assert.False(del.IsDefault);
            dlg.Close();
        });
    }

    [Fact]
    public void TypedConfirm_Requires_Exact_Delete_All()
    {
        WpfSta.Run(() =>
        {
            WpfTestHost.EnsureApp();
            var dlg = AppDialog.BuildTyped(null, "Clear all data", "h", "body",
                Messages.ClearAllTypePrompt, Messages.DeleteAllPhrase, "Clear all data");
            var btns = Buttons(dlg);
            var ok = btns.Single(b => (string)b.Content! == "Clear all data");
            var cancel = btns.Single(b => (string)b.Content! == "Cancel");
            Assert.False(ok.IsEnabled);
            Assert.True(cancel.IsDefault && cancel.IsCancel);

            var txt = dlg.ConfirmInput!;
            Assert.NotNull(txt);
            txt.Text = "DELETE AL";
            Assert.False(ok.IsEnabled);          // partial phrase does not enable
            txt.Text = "delete all";
            Assert.False(ok.IsEnabled);          // case-sensitive
            txt.Text = Messages.DeleteAllPhrase;
            Assert.True(ok.IsEnabled);           // exact phrase enables
            dlg.Close();
        });
    }

    // ---------- XAML re-issue dialog ----------

    [Fact]
    public void ReissueDialog_Validates_Date_And_Guard()
    {
        WpfSta.Run(() =>
        {
            WpfTestHost.EnsureApp();
            var dlg = new ReissueDialog("Re-issue X's card", "body",
                DateOnly.FromDateTime(DateTime.Today), _ => null);
            var txt = (TextBox)dlg.FindName("TxtDate")!;
            var err = (TextBlock)dlg.FindName("TxtError")!;

            txt.Text = "not a date";
            Assert.False(dlg.TryConfirm());
            Assert.Contains("dd/MM/yyyy", err.Text);

            txt.Text = "12/06/2030";
            Assert.True(dlg.TryConfirm());
            Assert.Equal(new DateOnly(2030, 6, 12), dlg.PrintDate);
            dlg.Close();

            var guarded = new ReissueDialog("h", "b",
                DateOnly.FromDateTime(DateTime.Today),
                _ => "Too early — card was issued yesterday.");
            ((TextBox)guarded.FindName("TxtDate")!).Text = "12/06/2030";
            Assert.False(guarded.TryConfirm());
            Assert.Equal("Too early — card was issued yesterday.",
                ((TextBlock)guarded.FindName("TxtError")!).Text);
            guarded.Close();
        });
    }

    // ---------- success banner ----------

    [Fact]
    public void SuccessBanner_Shows_And_AutoHides()
    {
        WpfSta.Run(() =>
        {
            WpfTestHost.EnsureApp();
            MainWindow.SkipResumePrompt = true;
            var win = FixPassTests.NewWindow();
            try
            {
                win.Show();
                win.ShowSuccessBanner(Messages.IssuedBanner("Test Person"));
                var banner = (FrameworkElement)win.FindName("PnlSuccessBanner")!;
                var txt = (TextBlock)win.FindName("TxtSuccessBanner")!;
                Assert.Equal(Visibility.Visible, banner.Visibility);
                Assert.Equal("Card issued for Test Person.", txt.Text);

                // hover pauses; timer tick hides
                win.BannerTimer_Tick(null, EventArgs.Empty);
                Assert.Equal(Visibility.Collapsed, banner.Visibility);
            }
            finally { win.Close(); }
        });
    }

    // ---------- backup-age warning ----------

    [Fact]
    public void BackupAge_Warns_After_Seven_Days()
    {
        WpfSta.Run(() =>
        {
            WpfTestHost.EnsureApp();
            MainWindow.SkipResumePrompt = true;
            var win = FixPassTests.NewWindow();
            var old = AppSettings.LastBackupAt;
            try
            {
                win.Show();
                AppSettings.LastBackupAt = DateTime.Now.AddDays(-8);
                win.UpdateStatusBar();
                var status = (TextBlock)win.FindName("TxtStatusBackup")!;
                var navWarn = (TextBlock)win.FindName("TxtBackupAgeWarn")!;
                Assert.Contains("8 days ago", status.Text);
                Assert.Contains("Back up now?", status.Text);
                Assert.Equal(Visibility.Visible, navWarn.Visibility);
                Assert.Contains("8 days ago", navWarn.Text);

                AppSettings.LastBackupAt = DateTime.Now.AddDays(-2);
                win.UpdateStatusBar();
                Assert.DoesNotContain("Back up now?", status.Text);
                Assert.Equal(Visibility.Collapsed, navWarn.Visibility);
            }
            finally
            {
                AppSettings.LastBackupAt = old;
                win.Close();
            }
        });
    }
}

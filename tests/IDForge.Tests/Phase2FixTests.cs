using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using IDForge;
using Xunit;

namespace IDForge.Tests;

/// <summary>WPF tests share one AppDomain/dispatcher host — run serially.</summary>
[Collection("Wpf")]
public class Phase2FixTests
{
    private static T OnSta<T>(Func<T> work) => WpfSta.Run(work);

    private static MainWindow NewWindow()
    {
        WpfTestHost.EnsureApp();
        MainWindow.SkipResumePrompt = true;
        return new MainWindow();
    }

    private static int CountSeparators(DependencyObject root)
    {
        // Logical tree: Collapsed elements (the transient message zone) still count.
        var n = 0;
        foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(root))
        {
            if (child is DependencyObject d)
            {
                if (child is TextBlock tb && tb.Text.Trim() == "|") n++;
                n += CountSeparators(d);
            }
        }
        return n;
    }

    /// <summary>Pumps the dispatcher until the condition holds or the deadline passes.</summary>
    private static void PumpUntil(Func<bool> done, int ms = 5000)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(ms);
        while (!done() && DateTime.UtcNow < deadline)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.SystemIdle);
            Thread.Sleep(40);
        }
    }

    [Fact]
    public void Unsaved_Marker_Only_On_NewCard()
    {
        OnSta(() =>
        {
            var main = NewWindow();
            try
            {
                // Records with no edits: no marker.
                main.ShowArea("records");
                Assert.DoesNotContain("saving", main.Title, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("unsaved", main.Title, StringComparison.OrdinalIgnoreCase);
                main.ShowArea("settings");
                Assert.DoesNotContain("saving", main.Title, StringComparison.OrdinalIgnoreCase);
                main.ShowArea("batch");
                Assert.DoesNotContain("saving", main.Title, StringComparison.OrdinalIgnoreCase);

                // A New Card edit pending autosave shows "saving…", never "unsaved changes".
                ((TextBox)main.FindName("TxtName")!).Text = "Marker Test";
                main.ShowArea("newcard");
                Assert.Contains("saving", main.Title, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("unsaved", main.Title, StringComparison.OrdinalIgnoreCase);
                main.ShowArea("records");
                Assert.DoesNotContain("saving", main.Title, StringComparison.OrdinalIgnoreCase);
                main.ShowArea("newcard");
                Assert.Contains("saving", main.Title, StringComparison.OrdinalIgnoreCase);

                // After the 800 ms autosave fires, the marker clears entirely.
                PumpUntil(() => !main.Title.Contains("saving"));
                Assert.DoesNotContain("saving", main.Title, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("unsaved", main.Title, StringComparison.OrdinalIgnoreCase);
                NewCardDraft.Clear();
            }
            finally { main.Close(); }
            return true;
        });
    }

    [Fact]
    public void Marker_Clears_After_Draft_Autosave()
    {
        OnSta(() =>
        {
            var main = NewWindow();
            try
            {
                ((TextBox)main.FindName("TxtName")!).Text = "Autosave Test";
                Assert.Contains("saving", main.Title, StringComparison.OrdinalIgnoreCase);
                PumpUntil(() => !main.Title.Contains("saving"), 8000);
                Assert.DoesNotContain("saving", main.Title, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("unsaved", main.Title, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("Draft saved automatically", ((TextBlock)main.FindName("TxtDraftNote")!).Text);
                NewCardDraft.Clear();
            }
            finally { main.Close(); }
            return true;
        });
    }

    [Fact]
    public void Status_Bar_Has_Fixed_Separators()
    {
        OnSta(() =>
        {
            var main = NewWindow();
            try
            {
                var left = (StackPanel)main.FindName("PnlStatusLeft")!;
                Assert.True(CountSeparators(left) >= 3, "expected >=3 status separators");
                Assert.NotNull(main.FindName("TxtStatusCount"));
                Assert.NotNull(main.FindName("TxtStatusBackup"));
                Assert.NotNull(main.FindName("TxtStatusMsg"));
            }
            finally { main.Close(); }
            return true;
        });
    }

    [Fact]
    public void Backup_Entry_Points_Produce_Same_Message()
    {
        OnSta(() =>
        {
            var main = NewWindow();
            try
            {
                string Msg() => ((TextBlock)main.FindName("TxtStatusMsg")!).Text;
                void AssertBackupMsg(string m)
                {
                    // FIX 2: file name, count only when above zero.
                    Assert.Matches(@"^Backup saved: idcard_\d{8}_\d{6}.*\.db( — \d+ cards?)?$", m);
                }

                // Ctrl+B
                Assert.True(main.TryHandleShortcut(Key.B, ModifierKeys.Control));
                AssertBackupMsg(Msg());

                // Backed-up card / Back up now button (sender-agnostic handler)
                main.NavBackup_Click(null!, null!);
                AssertBackupMsg(Msg());

                // Tools -> Back up now (MenuItem sender)
                main.NavBackup_Click(new MenuItem(), null!);
                AssertBackupMsg(Msg());

                // Non-Control modifiers must not trigger backup.
                Assert.False(main.TryHandleShortcut(Key.B, ModifierKeys.None));
            }
            finally { main.Close(); }
            return true;
        });
    }
}

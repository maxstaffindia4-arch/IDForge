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
    private static T OnSta<T>(Func<T> work)
    {
        T? result = default;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception ex) { error = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "STA thread timed out");
        Assert.Null(error);
        return result!;
    }

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
                if (child is TextBlock tb && tb.Text == " | ") n++;
                n += CountSeparators(d);
            }
        }
        return n;
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
                Assert.DoesNotContain("unsaved", main.Title, StringComparison.OrdinalIgnoreCase);
                main.ShowArea("settings");
                Assert.DoesNotContain("unsaved", main.Title, StringComparison.OrdinalIgnoreCase);
                main.ShowArea("batch");
                Assert.DoesNotContain("unsaved", main.Title, StringComparison.OrdinalIgnoreCase);

                // Dirty the New Card form, then switch away and back.
                ((TextBox)main.FindName("TxtName")!).Text = "Marker Test";
                main.ShowArea("newcard");
                Assert.Contains("unsaved changes", main.Title, StringComparison.OrdinalIgnoreCase);
                main.ShowArea("records");
                Assert.DoesNotContain("unsaved", main.Title, StringComparison.OrdinalIgnoreCase);
                main.ShowArea("newcard");
                Assert.Contains("unsaved changes", main.Title, StringComparison.OrdinalIgnoreCase);
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
                    Assert.StartsWith("Backup saved — ", m);
                    Assert.Contains("record", m);
                    Assert.Matches(@"\d{2}/\d{2}/\d{4} \d{2}:\d{2}$", m);
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

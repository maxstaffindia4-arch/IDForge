using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using IDForge;
using IDForge.Core.Data;
using Xunit;

namespace IDForge.Tests;

/// <summary>Phase 2/3 fix-pass tests. WPF tests share the single-AppDomain
/// host, so they run in the "Wpf" collection serially.</summary>
[Collection("Wpf")]
public class FixPassTests
{
    internal static T OnSta<T>(Func<T> work)
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

    internal static MainWindow NewWindow()
    {
        WpfTestHost.EnsureApp();
        MainWindow.SkipResumePrompt = true;
        return new MainWindow();
    }

    internal static int CountSeparators(DependencyObject root)
    {
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
    public void Status_Bar_Separates_Every_Item_Including_Version()
    {
        OnSta(() =>
        {
            var main = NewWindow();
            try
            {
                var bar = ((FrameworkElement)main.FindName("PnlStatusLeft")!).Parent!;
                // offline | count | backup | version = 4 separators minimum
                Assert.True(CountSeparators(bar) >= 4,
                    $"expected >=4 separators, found {CountSeparators(bar)}");
                var version = (TextBlock)main.FindName("TxtStatusVersion")!;
                Assert.Contains("IDForge v", version.Text);
                Assert.Contains("About (F1)", version.Text);
            }
            finally { main.Close(); }
            return true;
        });
    }

    [Fact]
    public void Duplicate_Uid_Shows_Red_Message_Link_And_Disables_Issue()
    {
        OnSta(() =>
        {
            var main = NewWindow();
            try
            {
                const string uid = "99887766";
                var store = main.Svc.Store;
                var emp = store.FindEmployeeByUid(uid);
                var inserted = false;
                if (emp == null)
                {
                    emp = new Employee
                    {
                        Uid = uid, Name = "Dup Test Person", BloodGroup = "N/A",
                        Position = "Intern", ContractType = "Permanent"
                    };
                    emp.Id = store.InsertEmployee(emp);
                    inserted = true;
                }
                try
                {
                    // Photo approved must NOT re-enable Issue for a duplicate:
                    // approve first, then enter the duplicate UID.
                    main._photoApproved = true;
                    var uidBox = (TextBox)main.FindName("TxtUid")!;
                    uidBox.Text = uid;

                    var err = (TextBlock)main.FindName("ErrUid")!;
                    var errorColor = ((SolidColorBrush)main.FindResource("ErrorBrush")!).Color;
                    Assert.Equal(errorColor, ((SolidColorBrush)err.Foreground).Color);
                    Assert.Contains("already exists", err.Text);
                    Assert.Equal(Visibility.Visible,
                        ((TextBlock)main.FindName("LnkOpenRecord")!).Visibility);

                    var issue = (Button)main.FindName("BtnSave")!;
                    Assert.False(issue.IsEnabled);
                    Assert.Equal(Visibility.Visible,
                        ((TextBlock)main.FindName("TxtIssueBlocked")!).Visibility);

                    // "Open record" switches to Records with the row selected.
                    main.OpenRecord_Click(null!, null!);
                    Assert.Equal("records", main.CurrentArea);
                    Assert.Contains("Dup Test Person",
                        ((TextBlock)main.FindName("TxtRecordInfo")!).Text);
                }
                finally
                {
                    if (inserted) store.SoftDeleteEmployee(emp.Id);
                }
            }
            finally { main.Close(); }
            return true;
        });
    }
}

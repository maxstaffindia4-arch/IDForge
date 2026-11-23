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

    /// <summary>A digits-only UID not present even among soft-deleted rows
    /// (Employees.Uid is UNIQUE, so deleted rows still occupy the value).</summary>
    private static string FreshUid(IDForge.Core.Data.CardStore store)
    {
        for (var i = 0; i < 200; i++)
        {
            var u = (90000000000L + Random.Shared.NextInt64(0, 9999999999L)).ToString();
            if (store.FindEmployeeByUidIncludingDeleted(u) == null) return u;
        }
        throw new InvalidOperationException("no free test UID");
    }

    [Fact]
    public void Duplicate_Uid_Shows_Red_Message_Link_And_Disables_Issue()
    {
        OnSta(() =>
        {
            var main = NewWindow();
            try
            {
                var uid = FreshUid(main.Svc.Store);
                var store = main.Svc.Store;
                var emp = new Employee
                {
                    Uid = uid, Name = "Dup Test Person", BloodGroup = "N/A",
                    Position = "Intern", ContractType = "Permanent"
                };
                emp.Id = store.InsertEmployee(emp);
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
                finally { store.SoftDeleteEmployee(emp.Id); }
            }
            finally { main.Close(); }
            return true;
        });
    }

    private static Color ChipColor(MainWindow w, string name)
        => ((SolidColorBrush)((TextBlock)w.FindName(name)!).Foreground).Color;

    private static Color ResColor(MainWindow w, string key)
        => ((SolidColorBrush)w.FindResource(key)!).Color;

    [Fact]
    public void Completion_Chips_Reflect_Details_Uid_And_Photo()
    {
        OnSta(() =>
        {
            var main = NewWindow();
            try
            {
                var store = main.Svc.Store;
                // A UID that is guaranteed not to exist (not even soft-deleted).
                var newUid = FreshUid(store);

                // A duplicate employee for the amber/red states.
                var dupUid = FreshUid(store);
                var dupEmp = new Employee
                {
                    Uid = dupUid, Name = "Chip Dup", BloodGroup = "N/A",
                    Position = "Intern", ContractType = "Permanent"
                };
                dupEmp.Id = store.InsertEmployee(dupEmp);
                try
                {
                    var name = (TextBox)main.FindName("TxtName")!;
                    var uid = (TextBox)main.FindName("TxtUid")!;
                    var pos = (ComboBox)main.FindName("CmbPosition")!;
                    var blood = (ComboBox)main.FindName("CmbBlood")!;

                    // 1 · Valid new card, photo NOT approved.
                    name.Text = "Chip Test";
                    pos.SelectedItem = "Manager";
                    blood.SelectedItem = blood.Items.Cast<object>().Skip(1).First();
                    uid.Text = newUid;

                    Assert.Equal(ResColor(main, "SuccessBrush"), ChipColor(main, "TxtChipDetails"));
                    Assert.Equal("Employee ID is new",
                        ((TextBlock)main.FindName("TxtChipUidNew")!).Text);
                    Assert.Equal(ResColor(main, "SuccessBrush"), ChipColor(main, "TxtChipUidNew"));
                    Assert.Equal(ResColor(main, "MutedBrush"), ChipColor(main, "TxtChipPhoto"));

                    // 2 · Photo approved → photo chip goes green.
                    main._photoApproved = true;
                    name.Text = "Chip Test ";   // retrigger chip refresh
                    Assert.Equal(ResColor(main, "SuccessBrush"), ChipColor(main, "TxtChipPhoto"));

                    // 3 · Duplicate ID → details amber, ID chip red with reason.
                    uid.Text = dupUid;
                    Assert.Equal(ResColor(main, "WarningBrush"), ChipColor(main, "TxtChipDetails"));
                    Assert.Equal("Employee ID already exists",
                        ((TextBlock)main.FindName("TxtChipUidNew")!).Text);
                    Assert.Equal(ResColor(main, "ErrorBrush"), ChipColor(main, "TxtChipUidNew"));
                    Assert.Equal(ResColor(main, "SuccessBrush"), ChipColor(main, "TxtChipPhoto"));
                }
                finally
                {
                    store.SoftDeleteEmployee(dupEmp.Id);
                    NewCardDraft.Clear();
                }
            }
            finally { main.Close(); }
            return true;
        });
    }
}

using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using IDForge;
using Xunit;

namespace IDForge.Tests;

/// <summary>Phase 5 Review-window tests.</summary>
[Collection("Wpf")]
public class Phase5Tests
{
    private static ReviewWindow NewReview(MainWindow owner, params BatchRow[] rows)
    {
        var coll = new ObservableCollection<BatchRow>(rows);
        var win = new ReviewWindow(owner, coll);
        win.Show();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Render);
        return win;
    }

    private static void OnSta(Action work)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { work(); }
            catch (Exception ex) { error = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "STA thread timed out");
        Assert.Null(error);
    }

    private static BatchRow Row(string uid, string name, string status = "NeedsReview", long id = 1)
        => new() { RowId = id, Uid = uid, Name = name, Status = status };

    [Fact]
    public void Review_Opens_With_List_Preview_And_Navigation()
    {
        OnSta(() =>
        {
            var w = FixPassTests.NewWindow();
            var rows = new[] { Row("80000001", "Alpha", id: 1), Row("80000002", "Beta", "Issued", 2), Row("80000003", "Gamma", "Skipped", 3) };
            var win = NewReview(w, rows);
            try
            {
                Assert.Equal("Batch Review", win.Title.Split('—')[0].Trim());
                var list = (ListBox)FindName(win, "LstRows");
                Assert.Equal(3, list.Items.Count);
                var img = FindName<Image>(win, "ImgPreview");
                Assert.NotNull(img);
                var prev = FindButton(win, "◄ Previous");
                var next = FindButton(win, "Next ►");
                Assert.NotNull(prev); Assert.NotNull(next);
                // Next moves selection to the second row.
                next.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.Same(rows[1], win.CurrentForTest);
            }
            finally { win.Close(); w.Close(); }
        });
    }

    [Fact]
    public void Review_Filter_Narrows_Row_List()
    {
        OnSta(() =>
        {
            var w = FixPassTests.NewWindow();
            var rows = new[] { Row("80000011", "A", id: 11), Row("80000012", "B", "Failed", 12), Row("80000013", "C", "Issued", 13) };
            var win = NewReview(w, rows);
            try
            {
                var cmb = (ComboBox)FindName(win, "CmbFilter");
                cmb.SelectedIndex = 2; // Failed
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
                var list = (ListBox)FindName(win, "LstRows");
                Assert.Single(list.Items.Cast<BatchRow>().ToList());
                Assert.Equal("80000012", ((BatchRow)list.Items[0]).Uid);
            }
            finally { win.Close(); w.Close(); }
        });
    }

    [Fact]
    public void Review_Window_Size_Within_WorkArea_And_Minimum()
    {
        OnSta(() =>
        {
            var w = FixPassTests.NewWindow();
            var win = NewReview(w, Row("80000021", "Solo"));
            try
            {
                var wa = SystemParameters.WorkArea;
                Assert.True(win.Width <= wa.Width * 0.9 + 1 || win.WindowState == WindowState.Maximized);
                Assert.True(win.Height <= wa.Height * 0.9 + 1 || win.WindowState == WindowState.Maximized);
                Assert.True(win.MinWidth >= 860 && win.MinHeight >= 620);
            }
            finally { win.Close(); w.Close(); }
        });
    }

    [Fact]
    public void RemoveBg_Toggle_Switches_Mode_And_Enables_Selector()
    {
        OnSta(() =>
        {
            var w = FixPassTests.NewWindow();
            var row = Row("80000031", "Bg Test", id: 31);
            row.BgMode = "PlainWhite";
            var win = NewReview(w, row);
            try
            {
                var chk = (CheckBox)FindName(win, "ChkRemoveBg")!;
                var cmb = (ComboBox)FindName(win, "CmbBgMode")!;
                Assert.True(chk.IsChecked == true);
                Assert.True(cmb.IsEnabled);

                chk.IsChecked = false; // keep as is
                Assert.Equal("Keep", row.BgMode);
                Assert.False(cmb.IsEnabled);

                chk.IsChecked = true;
                Assert.Equal("PlainWhite", row.BgMode);
                Assert.True(cmb.IsEnabled);
            }
            finally { win.Close(); w.Close(); }
        });
    }

    [Fact]
    public void Zoom_Adjustment_Survives_Row_Switch()
    {
        OnSta(() =>
        {
            var w = FixPassTests.NewWindow();
            var a = Row("80000041", "Zoom A", id: 41);
            var b = Row("80000042", "Zoom B", id: 42);
            a.Zoom = 1.7; a.OffsetX = 12; a.OffsetY = -8;
            var win = NewReview(w, a, b);
            try
            {
                var sld = (Slider)FindName(win, "SldZoom")!;
                Assert.Equal(1.7, sld.Value, 3);
                // Move to row B and back — adjustment must still be there.
                var next = FindButton(win, "Next ►")!;
                next.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                next.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                var back = FindButton(win, "◄ Previous")!;
                back.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.Same(a, win.CurrentForTest);
                Assert.Equal(1.7, sld.Value, 3);
                Assert.Equal(12, a.OffsetX);
                Assert.Equal(-8, a.OffsetY);
            }
            finally { win.Close(); w.Close(); }
        });
    }

    [Fact]
    public void Bottom_Bar_Has_Spec_Buttons()
    {
        OnSta(() =>
        {
            var w = FixPassTests.NewWindow();
            var win = NewReview(w, Row("80000051", "Buttons", id: 51));
            try
            {
                foreach (var label in new[] { "◄ Previous", "Save draft", "Skip", "Approve & Issue", "Next ►" })
                    Assert.NotNull(FindButton(win, label));
            }
            finally { win.Close(); w.Close(); }
        });
    }

    [Fact]
    public async Task Issued_Row_Cannot_Be_Issued_Again()
    {
        await OnStaAsync(async () =>
        {
            var w = FixPassTests.NewWindow();
            var row = Row("80000061", "Once", "Issued", 61);
            row.AttemptCount = 1;
            var win = NewReview(w, row);
            try
            {
                var outcome = await win.IssueRowAsync(row, confirm: false, quiet: true);
                Assert.Null(outcome);
                Assert.Equal(1, row.AttemptCount);   // no second attempt / CardIssue
                Assert.Equal("Issued", row.Status);
            }
            finally { win.Close(); w.Close(); }
        });
    }

    [Fact]
    public async Task Invalid_Row_Issue_Writes_Nothing()
    {
        await OnStaAsync(async () =>
        {
            var w = FixPassTests.NewWindow();
            var row = Row("", "", "NeedsReview", 62);   // missing name + UID
            var win = NewReview(w, row);
            try
            {
                var outcome = await win.IssueRowAsync(row, confirm: false, quiet: true);
                Assert.Null(outcome);
                Assert.Equal(0, row.AttemptCount);
                Assert.Equal("NeedsReview", row.Status);   // not flipped to Failed, nothing persisted as issued
                Assert.Null(row.CardIssueId);
            }
            finally { win.Close(); w.Close(); }
        });
    }

    [Fact]
    public void Reissue_Always_Asks_For_Confirmation()
    {
        OnSta(() =>
        {
            var prev = AppSettings.ConfirmBeforeIssue;
            bool? sawReissue = null; int calls = 0;
            Dialogs.ConfirmOverride = (_, _, _, _, isReissue) => { calls++; sawReissue = isReissue; return true; };
            try
            {
                AppSettings.ConfirmBeforeIssue = false;
                // Non-reissue with setting off: no dialog at all.
                Assert.True(Dialogs.ConfirmIssue(null!, "A", "1", DateOnly.FromDateTime(DateTime.Today), false));
                Assert.Equal(0, calls);
                // Re-issue: always confirms even with the setting off.
                Assert.True(Dialogs.ConfirmIssue(null!, "A", "1", DateOnly.FromDateTime(DateTime.Today), true));
                Assert.Equal(1, calls);
                Assert.True(sawReissue);
            }
            finally
            {
                Dialogs.ConfirmOverride = null;
                AppSettings.ConfirmBeforeIssue = prev;
            }
        });
    }

    private static async Task OnStaAsync(Func<Task> work)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { work().GetAwaiter().GetResult(); }
            catch (Exception ex) { error = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "STA thread timed out");
        Assert.Null(error);
    }

    // ---------- helpers ----------

    private static DependencyObject? FindName(DependencyObject root, string name)
    {
        foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(root))
        {
            if (child is FrameworkElement fe && fe.Name == name) return fe;
            if (child is DependencyObject d)
            {
                var r = FindName(d, name);
                if (r != null) return r;
            }
        }
        return null;
    }

    private static T? FindName<T>(DependencyObject root, string name) where T : DependencyObject
        => FindName(root, name) as T;

    private static Button? FindButton(DependencyObject root, string content)
    {
        foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(root))
        {
            if (child is Button b && b.Content as string == content) return b;
            if (child is DependencyObject d)
            {
                var r = FindButton(d, content);
                if (r != null) return r;
            }
        }
        return null;
    }
}

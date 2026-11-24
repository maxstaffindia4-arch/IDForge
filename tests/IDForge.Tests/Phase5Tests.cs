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

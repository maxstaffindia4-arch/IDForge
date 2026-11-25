using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using IDForge;
using IDForge.Core.Data;
using Xunit;

namespace IDForge.Tests;

/// <summary>Phase 6 Records-screen tests.</summary>
[Collection("Wpf")]
public class Phase6Tests
{
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

    private static Employee Emp(string uid, string name, string contract = "Permanent")
        => new() { Uid = uid, Name = name, BloodGroup = "N/A", Position = "Tech",
                   ContractType = contract };

    // ---------- store-level ----------

    [Fact]
    public void PurgeEmployee_Removes_Emp_And_Issues()
    {
        var db = Path.Combine(Path.GetTempPath(), $"idcard-p6-{Guid.NewGuid():N}.db");
        using var store = new CardStore(db);
        var e = Emp("80000001", "Purge Me");
        var id = store.InsertEmployee(e);
        store.InsertIssue(new CardIssue
        {
            EmployeeId = id, Version = 1, PrintDate = new DateOnly(2026, 1, 1),
            ValidUntil = new DateOnly(2027, 1, 1)
        });
        store.PurgeEmployee(id);
        Assert.Null(store.FindEmployeeById(id));
        Assert.Empty(store.IssuesForEmployee(id));
    }

    [Fact]
    public void AuditForEmployee_Returns_Newest_First()
    {
        var db = Path.Combine(Path.GetTempPath(), $"idcard-p6-{Guid.NewGuid():N}.db");
        using var store = new CardStore(db);
        var id = store.InsertEmployee(Emp("80000002", "Audit Me"));
        store.LogAudit(id, "issue", "first");
        store.LogAudit(id, "reprint", "second");
        var list = store.AuditForEmployee(id);
        Assert.Equal(2, list.Count);
        Assert.Equal("reprint", list[0].Action);
        Assert.Equal("issue", list[1].Action);
    }

    [Fact]
    public void PurgeDeletedOlderThan_Keeps_Recent()
    {
        var db = Path.Combine(Path.GetTempPath(), $"idcard-p6-{Guid.NewGuid():N}.db");
        using var store = new CardStore(db);
        var old = store.InsertEmployee(Emp("80000003", "Old Deleted"));
        var recent = store.InsertEmployee(Emp("80000004", "Recent Deleted"));
        store.SoftDeleteEmployee(old);
        store.SoftDeleteEmployee(recent);
        // age the old one past 30 days
        using (var c = store.Connection.CreateCommand())
        {
            c.CommandText = "UPDATE Employees SET DeletedAt=$d WHERE Id=$id";
            c.Parameters.AddWithValue("$d", DateTimeOffset.UtcNow.AddDays(-40).ToString("O"));
            c.Parameters.AddWithValue("$id", old);
            c.ExecuteNonQuery();
        }
        Assert.Equal(1, store.PurgeDeletedOlderThan(30));
        Assert.Null(store.FindEmployeeById(old));
        Assert.NotNull(store.FindEmployeeById(recent));
    }

    [Fact]
    public void ValidityKind_Classifies_Correctly()
    {
        var today = new DateOnly(2026, 11, 25);
        Assert.Equal("None", MainWindow.ValidityKindFor(null, today));
        Assert.Equal("Expired", MainWindow.ValidityKindFor(today.AddDays(-1), today));
        Assert.Equal("Expires soon", MainWindow.ValidityKindFor(today.AddDays(10), today));
        Assert.Equal("Expires soon", MainWindow.ValidityKindFor(today.AddDays(30), today));
        Assert.Equal("Active", MainWindow.ValidityKindFor(today.AddDays(31), today));
    }

    // ---------- window-level ----------

    private static MainWindow WindowWithRecords(params Employee[] emps)
    {
        var w = FixPassTests.NewWindow();
        foreach (var e in emps)
        {
            e.Uid = "8" + Random.Shared.Next(1000000, 9999999);   // unique vs dev DB
            w.Svc.Store.InsertEmployee(e);
        }
        w.ShowArea("records");
        w.RefreshRecordsPublic();
        return w;
    }

    [Fact]
    public void Records_Filters_Narrow_Results()
    {
        OnSta(() =>
        {
            var p = "P6" + Random.Shared.Next(1000, 9999);
            var w = WindowWithRecords(
                Emp("", $"{p} Alpha One", "Permanent"),
                Emp("", $"{p} Beta Two", "Contractual"));
            try
            {
                var grid = (DataGrid)w.FindName("LstRecords")!;
                var txt = (TextBox)w.FindName("TxtSearch")!;
                txt.Text = p;
                w.RefreshRecordsPublic();
                Assert.Equal(2, grid.Items.Cast<object>().Count());

                // contract filter
                var cmb = (ComboBox)w.FindName("CmbContractFilter")!;
                cmb.SelectedIndex = 2;   // Contractual
                Assert.Single(grid.Items.Cast<object>());
                cmb.SelectedIndex = 0;

                // search narrows further
                txt.Text = $"{p} Beta";
                w.RefreshRecordsPublic();
                Assert.Single(grid.Items.Cast<object>());
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void Records_Delete_Shows_Undo_And_Undo_Restores()
    {
        OnSta(() =>
        {
            var p = "P6" + Random.Shared.Next(1000, 9999);
            var w = WindowWithRecords(Emp("", $"{p} Undo Target"));
            try
            {
                var grid = (DataGrid)w.FindName("LstRecords")!;
                ((TextBox)w.FindName("TxtSearch")!).Text = p;
                w.RefreshRecordsPublic();
                grid.SelectedIndex = 0;
                MainWindow.ConfirmDeleteRecord = _ => true;
                try
                {
                    // invoke the click handler directly
                    var mi = typeof(MainWindow).GetMethod("DeleteRecord_Click",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                    mi.Invoke(w, new object[] { w, null! });
                }
                finally { MainWindow.ConfirmDeleteRecord = null; }

                var banner = (FrameworkElement)w.FindName("PnlUndo")!;
                Assert.Equal(Visibility.Visible, banner.Visibility);
                Assert.DoesNotContain(w.Svc.Store.AllEmployees(), e => e.Name.StartsWith(p + " "));
                Assert.Contains(w.Svc.Store.DeletedEmployees(), e => e.Name.StartsWith(p + " "));

                var undo = (Button)FindByContent(banner, "Undo")!;
                undo.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Contains(w.Svc.Store.AllEmployees(), e => e.Name.StartsWith(p + " "));
                Assert.DoesNotContain(w.Svc.Store.DeletedEmployees(), e => e.Name.StartsWith(p + " "));
                Assert.Equal(Visibility.Collapsed, banner.Visibility);
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void Records_Shortcuts_Are_Wired()
    {
        OnSta(() =>
        {
            var w = WindowWithRecords(Emp("81000004", "Shortcut Target"));
            try
            {
                Assert.True(w.TryHandleShortcut(System.Windows.Input.Key.F,
                    System.Windows.Input.ModifierKeys.Control));
                Assert.True(w.TryHandleShortcut(System.Windows.Input.Key.P,
                    System.Windows.Input.ModifierKeys.Control));
                Assert.True(w.TryHandleShortcut(System.Windows.Input.Key.R,
                    System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift));
            }
            finally { w.Close(); }
        });
    }

    private static DependencyObject? FindByContent(DependencyObject root, string content)
    {
        foreach (var d in Descendants(root))
            if (d is Button b && b.Content?.ToString() == content) return b;
        return null;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
            foreach (var d in Descendants(System.Windows.Media.VisualTreeHelper.GetChild(root, i)))
                yield return d;
    }
}

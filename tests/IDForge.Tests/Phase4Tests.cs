using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using IDForge;
using IDForge.Core.Data;
using Xunit;

namespace IDForge.Tests;

/// <summary>Phase 4 (Batch screen) tests. Shares the single-AppDomain WPF host.</summary>
[Collection("Wpf")]
public class Phase4Tests
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

    private static MainWindow NewWindow()
    {
        WpfTestHost.EnsureApp();
        MainWindow.SkipResumePrompt = true;
        return new MainWindow();
    }

    private static RadioButton FilterRadio(MainWindow w, string tag)
    {
        foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(w))
            if (FindRadio(child, tag) is { } r) return r;
        throw new InvalidOperationException($"filter radio '{tag}' not found");
    }

    private static RadioButton? FindRadio(object node, string tag)
    {
        if (node is RadioButton rb && rb.Tag as string == tag) return rb;
        if (node is DependencyObject d)
            foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(d))
                if (FindRadio(child, tag) is { } r) return r;
        return null;
    }

    private static string[] ColumnHeaders(MainWindow w)
    {
        var dg = (DataGrid)w.FindName("DgBatch")!;
        return dg.Columns.Select(c => c.Header?.ToString() ?? "").ToArray();
    }

    [Fact]
    public void Grid_Has_Spec_Columns_And_Filters()
    {
        OnSta(() =>
        {
            var w = NewWindow();
            try
            {
                var headers = ColumnHeaders(w);
                Assert.Equal(new[] { "#", "Employee", "Employee ID", "Position", "Contract",
                    "Print date", "Card type", "Photo", "Status", "Message" }, headers);
                foreach (var tag in new[] { "all", "attention", "review", "approved", "reissue" })
                    Assert.NotNull(FilterRadio(w, tag));
                Assert.NotNull(w.FindName("TxtBatchCounts"));
                Assert.NotNull(w.FindName("BtnIssueBatch"));
                Assert.NotNull(w.FindName("TxtIssueBatchReason"));
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void Counts_Line_And_Issue_Batch_Gating()
    {
        OnSta(() =>
        {
            var w = NewWindow();
            try
            {
                w.BatchRows.Add(new BatchRow { Uid = "70000001", Name = "A One" });
                w.BatchRows.Add(new BatchRow { Uid = "70000002", Name = "B Two" });
                w.UpdateBatchSummary();

                var counts = ((TextBlock)w.FindName("TxtBatchCounts")!).Text;
                Assert.Contains("0 of 2 approved", counts);
                Assert.Contains("2 more before the batch can be issued", counts);

                var btn = (Button)w.FindName("BtnIssueBatch")!;
                var reason = ((TextBlock)w.FindName("TxtIssueBatchReason")!).Text;
                Assert.False(btn.IsEnabled);
                Assert.False(string.IsNullOrEmpty(reason));

                foreach (var r in w.BatchRows) r.Status = "Approved";
                w.UpdateBatchSummary();
                Assert.True(btn.IsEnabled);
                Assert.Equal("", ((TextBlock)w.FindName("TxtIssueBatchReason")!).Text);
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void Duplicate_Uid_In_Batch_Flags_Message_With_Row_Number()
    {
        OnSta(() =>
        {
            var w = NewWindow();
            try
            {
                w.BatchRows.Add(new BatchRow { Uid = "70000011", Name = "First" });
                w.BatchRows.Add(new BatchRow { Uid = "70000011", Name = "Second" });
                w.BatchRows.Add(new BatchRow { Uid = "70000012", Name = "Third" });
                w.UpdateBatchSummary();
                var rows = w.BatchRows.ToList();
                Assert.Equal(1, rows[0].RowNumber);
                Assert.DoesNotContain("Duplicate", rows[0].Message);
                Assert.Contains("Duplicate of row 1", rows[1].Message);
                Assert.DoesNotContain("Duplicate", rows[2].Message);
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void Reissue_Row_Shows_Card_Type_And_Replaces_Text()
    {
        OnSta(() =>
        {
            var w = NewWindow();
            try
            {
                var store = w.Svc.Store;
                var empId = store.InsertEmployee(new Employee
                {
                    Uid = "70000021", Name = "Old Timer", BloodGroup = "O+",
                    Position = "Driver", ContractType = "Permanent"
                });
                store.InsertIssue(new CardIssue
                {
                    EmployeeId = empId, Version = 1,
                    PrintDate = new DateOnly(2026, 1, 10),
                    ValidUntil = new DateOnly(2027, 1, 10),
                    IssuedAt = new DateTimeOffset(2026, 1, 10, 9, 0, 0, TimeSpan.Zero)
                });

                w.BatchRows.Add(new BatchRow { Uid = "70000021", Name = "Old Timer" });
                w.BatchRows.Add(new BatchRow { Uid = "70000022", Name = "New Person" });
                w.UpdateBatchSummary();
                var rows = w.BatchRows.ToList();
                Assert.Equal("Re-issue", rows[0].CardType);
                Assert.Contains("Replaces card issued 10/01/2026", rows[0].Message);
                Assert.Equal("New card", rows[1].CardType);
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void Filters_Narrow_The_Grid()
    {
        OnSta(() =>
        {
            var w = NewWindow();
            try
            {
                w.BatchRows.Add(new BatchRow { Uid = "70000031", Name = "Draft Row" });       // Draft
                var needs = new BatchRow { Uid = "70000032", Name = "Bad Row", Status = "Failed" };
                w.BatchRows.Add(needs);
                w.BatchRows.Add(new BatchRow { Uid = "70000033", Name = "Ok Row", Status = "Approved" });
                w.UpdateBatchSummary();

                var dg = (DataGrid)w.FindName("DgBatch")!;
                Assert.Equal(3, dg.Items.Count);

                FilterRadio(w, "attention").IsChecked = true;
                Assert.Equal(1, dg.Items.Count);
                Assert.Same(needs, dg.Items[0]);

                FilterRadio(w, "approved").IsChecked = true;
                Assert.Equal(1, dg.Items.Count);
                Assert.Equal("Ok Row", ((BatchRow)dg.Items[0]).Name);

                FilterRadio(w, "all").IsChecked = true;
                Assert.Equal(3, dg.Items.Count);
            }
            finally { w.Close(); }
        });
    }
}

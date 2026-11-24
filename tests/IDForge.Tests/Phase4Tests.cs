using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
                Assert.Equal(new[] { "#", "Employee", "Employee ID", "Blood group", "Position",
                    "Contract", "Print date", "Card type", "Photo", "Status", "Message" }, headers);
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
                var uid = Random.Shared.Next(1000000000, 1999999999).ToString();
                var empId = store.InsertEmployee(new Employee
                {
                    Uid = uid, Name = "Old Timer", BloodGroup = "O+",
                    Position = "Driver", ContractType = "Permanent"
                });
                store.InsertIssue(new CardIssue
                {
                    EmployeeId = empId, Version = 1,
                    PrintDate = new DateOnly(2026, 1, 10),
                    ValidUntil = new DateOnly(2027, 1, 10),
                    IssuedAt = new DateTimeOffset(2026, 1, 10, 9, 0, 0, TimeSpan.Zero)
                });

                w.BatchRows.Add(new BatchRow { Uid = uid, Name = "Old Timer" });
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

    // ---- Phase 4 fix A ----

    [Fact]
    public void Print_Date_Displays_DdMmYyyy_While_Stored_Iso()
    {
        OnSta(() =>
        {
            var row = new BatchRow { PrintDateText = "2026-08-31" };
            Assert.Equal("31/08/2026", row.PrintDateDisplay);
            row.PrintDateDisplay = "05/01/2026";
            Assert.Equal("2026-01-05", row.PrintDateText);
            row.PrintDateDisplay = "2026-03-09";
            Assert.Equal("09/03/2026", row.PrintDateDisplay);
            Assert.Equal("2026-03-09", row.PrintDateText);
        });
    }

    [Fact]
    public void Draft_And_NeedsPhoto_Rows_List_Missing_Fields()
    {
        OnSta(() =>
        {
            var w = NewWindow();
            try
            {
                w.BatchRows.Add(new BatchRow { Uid = "70000101", Status = "Draft" });
                w.BatchRows.Add(new BatchRow
                {
                    Uid = "70000102", Name = "Full", Position = "Driver",
                    PhotoPath = @"C:\nope.jpg", Status = "NeedsPhoto"
                });
                w.BatchRows.Add(new BatchRow
                {
                    Uid = "70000103", Name = "Bad", Status = "Failed", Error = "bad uid"
                });
                w.UpdateBatchSummary();
                var rows = w.BatchRows.ToList();
                Assert.Equal("Missing: name, position, photo", rows[0].Message);
                Assert.Equal("", rows[1].Message);
                Assert.Equal("bad uid", rows[2].Message);
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void Blood_Group_Column_Sits_After_Employee_Id()
    {
        OnSta(() =>
        {
            var w = NewWindow();
            try
            {
                var headers = ColumnHeaders(w);
                Assert.Equal("Blood group", headers[3]);
                Assert.Equal("Position", headers[4]);
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void Del_Asks_Only_When_Row_Has_Content()
    {
        OnSta(() =>
        {
            var w = NewWindow();
            var prompts = new List<string>();
            MainWindow.ConfirmRemoveRow = p => { prompts.Add(p); return false; };
            try
            {
                var empty = new BatchRow();
                w.BatchRows.Add(empty);
                w.UpdateBatchSummary();
                var dg = (DataGrid)w.FindName("DgBatch")!;

                // empty row: removed without asking
                dg.SelectedItem = empty;
                w.BatchDelRow_Click(null!, null!);
                Assert.Empty(prompts);
                Assert.DoesNotContain(empty, w.BatchRows);

                // row with content: prompt shown, Cancel keeps it
                var filled = new BatchRow { Uid = "70000111", Name = "Keep Me" };
                w.BatchRows.Add(filled);
                w.UpdateBatchSummary();
                dg.SelectedItem = filled;
                w.BatchDelRow_Click(null!, null!);
                Assert.Single(prompts);
                Assert.Contains("Remove row 1 (Keep Me)?", prompts[0]);
                Assert.Contains(filled, w.BatchRows);

                // confirm Remove → row goes away
                MainWindow.ConfirmRemoveRow = _ => true;
                w.BatchDelRow_Click(null!, null!);
                Assert.DoesNotContain(filled, w.BatchRows);
            }
            finally { MainWindow.ConfirmRemoveRow = null; w.Close(); }
        });
    }

    // ---- Phase 4 fix B ----

    [Fact]
    public void Zoom_Labels_Do_Not_Overlap_Or_Fall_Back_To_Tick()
    {
        var frac = (1.0 - 0.4) / (3.0 - 0.4);
        Assert.Equal(0.2307, frac, 3);

        // wide canvas: all three labels fit without overlap
        var wide = IDForge.Controls.PhotoPositionPanel.ZoomLabelLayout(300, 28, 34, 30, frac);
        Assert.True(wide.ShowMid);
        Assert.True(wide.MidLeft >= 28 + 3);
        Assert.True(wide.MidLeft + 34 <= wide.MaxLeft - 3);

        // narrow canvas (the real 150 px): mid label collides → hidden, tick remains
        var narrow = IDForge.Controls.PhotoPositionPanel.ZoomLabelLayout(150, 28, 34, 30, frac);
        Assert.False(narrow.ShowMid);
        Assert.Equal(0, narrow.MinLeft);
        Assert.True(narrow.MaxLeft > 100);
    }

    [Fact]
    public void Footer_Has_Only_The_Panel_Approve_Action()
    {
        OnSta(() =>
        {
            var w = NewWindow();
            try
            {
                Assert.Null(w.FindName("BtnApproveFooter"));
                var approveButtons = new List<Button>();
                void Walk(object node)
                {
                    if (node is Button b && (b.Name.Contains("Approve") ||
                        b.Content?.ToString() == "Approve photo" ||
                        b.Content?.ToString() == "✓ Photo approved")) approveButtons.Add(b);
                    if (node is DependencyObject d)
                        foreach (var c in System.Windows.LogicalTreeHelper.GetChildren(d)) Walk(c);
                }
                Walk(w);
                Assert.Single(approveButtons);
                Assert.Equal("BtnApprove", approveButtons[0].Name);
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void Photo_Status_Uses_Neutral_Info_Colour_And_Red_For_Errors()
    {
        OnSta(() =>
        {
            var w = NewWindow();
            try
            {
                var status = (TextBlock)w.FindName("TxtPhotoStatus")!;
                var errorColor = ((SolidColorBrush)w.FindResource("ErrorBrush")).Color;
                var mutedColor = ((SolidColorBrush)w.FindResource("MutedBrush")).Color;

                w.SetPhotoStatus("Background already transparent — removal skipped.");
                Assert.StartsWith("ℹ ", status.Text);
                Assert.Equal(mutedColor, ((SolidColorBrush)status.Foreground).Color);
                Assert.NotEqual(errorColor, ((SolidColorBrush)status.Foreground).Color);

                w.SetPhotoStatus("Photo failed: boom", isError: true);
                Assert.Equal("Photo failed: boom", status.Text);
                Assert.Equal(errorColor, ((SolidColorBrush)status.Foreground).Color);
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void Mixed_Capitals_Hint_For_Name()
    {
        OnSta(() =>
        {
            var w = NewWindow();
            try
            {
                Assert.True(MainWindow.HasMixedCapitals("mEHDI"));
                Assert.False(MainWindow.HasMixedCapitals("Mehdi"));
                Assert.False(MainWindow.HasMixedCapitals("MEHDI"));

                var name = (TextBox)w.FindName("TxtName")!;
                var hint = (TextBlock)w.FindName("TxtNameHint")!;
                name.Text = "mEHDI";
                Assert.Equal(Visibility.Visible, hint.Visibility);
                name.Text = "Mehdi";
                Assert.Equal(Visibility.Collapsed, hint.Visibility);
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void Batch_Message_Warns_On_Mixed_Capitals_Name()
    {
        OnSta(() =>
        {
            var w = NewWindow();
            try
            {
                w.BatchRows.Add(new BatchRow { Uid = "70000201", Name = "mEHDI", Status = "Draft" });
                w.BatchRows.Add(new BatchRow { Uid = "70000202", Name = "Mehdi", Status = "Draft" });
                w.UpdateBatchSummary();
                var rows = w.BatchRows.ToList();
                Assert.Contains("Mixed capitals, check spelling", rows[0].Message);
                Assert.DoesNotContain("Mixed capitals", rows[1].Message);
            }
            finally { w.Close(); }
        });
    }

    // ---- Phase 4 fix C ----

    [Fact]
    public void Reissue_Checkbox_Is_Checked_And_Disabled()
    {
        OnSta(() =>
        {
            var w = NewWindow();
            try
            {
                CheckBox? found = null;
                void Walk(object node)
                {
                    if (node is CheckBox cb && cb.Content?.ToString() == "Always confirm re-issues") found = cb;
                    if (node is DependencyObject d)
                        foreach (var c in System.Windows.LogicalTreeHelper.GetChildren(d)) Walk(c);
                }
                Walk(w);
                Assert.NotNull(found);
                Assert.True(found!.IsChecked);
                Assert.False(found.IsEnabled);
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void New_Install_Confirm_Before_Issue_Defaults_On()
    {
        // SettingsFile defaults apply when settings.json is absent or lacks the key.
        Assert.True(new AppSettings.SettingsFile().ConfirmBeforeIssue);
    }

    [Fact]
    public void Records_Dates_Display_DdMmYyyy()
    {
        OnSta(() =>
        {
            var w = NewWindow();
            try
            {
                var store = w.Svc.Store;
                var uid = Random.Shared.Next(1000000000, 1999999999).ToString();
                var empId = store.InsertEmployee(new Employee
                {
                    Uid = uid, Name = "Date Check", BloodGroup = "O+",
                    Position = "Driver", ContractType = "Permanent"
                });
                store.InsertIssue(new CardIssue
                {
                    EmployeeId = empId, Version = 1,
                    PrintDate = new DateOnly(2026, 2, 3),
                    ValidUntil = new DateOnly(2027, 2, 3),
                    IssuedAt = new DateTimeOffset(2026, 2, 3, 14, 30, 0, TimeSpan.Zero)
                });
                w.RefreshRecordsPublic();
                var list = (ListView)w.FindName("LstRecords")!;
                var row = list.Items.Cast<object>()
                    .First(i => i.GetType().GetProperty("Uid")!.GetValue(i) as string == uid);
                var validUntil = row.GetType().GetProperty("ValidUntil")!.GetValue(row) as string;
                var lastIssued = row.GetType().GetProperty("LastIssued")!.GetValue(row) as string;
                Assert.Equal("03/02/2027", validUntil);
                Assert.Matches(@"^\d{2}/\d{2}/\d{4} \d{2}:\d{2}$", lastIssued);
            }
            finally { w.Close(); }
        });
    }
}
